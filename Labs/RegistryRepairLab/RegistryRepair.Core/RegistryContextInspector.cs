using System.Buffers.Binary;
using System.Text;

namespace RegistryRepair.Core;

public enum RegistryInspectionCategory
{
    Startup,
    AppInit,
    ImageFileExecutionOptions,
    Winlogon,
    Service,
    FileAssociation,
}

public enum RegistryInspectionStatus
{
    Malformed,
    Review,
    Unreadable,
}

public sealed record RegistryInspectionFinding(
    string Code,
    RegistryInspectionCategory Category,
    RegistryInspectionStatus Status,
    RegistryAddress Address,
    string Summary,
    IReadOnlyList<string> Evidence,
    Uri Source)
{
    public bool AutomaticCorrectionAvailable => false;
}

public sealed record RegistryInspectionProgress(
    int CompletedStages,
    int TotalStages,
    string Stage);

public sealed class RegistryContextInspector
{
    private static readonly Uri RunSource = new(
        "https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys");
    private static readonly Uri AppInitSource = new(
        "https://learn.microsoft.com/windows/win32/dlls/secure-boot-and-appinit-dlls");
    private static readonly Uri IfeoSource = new(
        "https://learn.microsoft.com/windows-hardware/drivers/debugger/running-a-program-in-a-debugger");
    private static readonly Uri WinlogonSource = new(
        "https://learn.microsoft.com/windows/configuration/shell-launcher/");
    private static readonly Uri ServiceSource = new(
        "https://learn.microsoft.com/windows-hardware/drivers/install/hklm-system-currentcontrolset-services-registry-tree");
    private static readonly Uri FileAssociationSource = new(
        "https://learn.microsoft.com/windows/win32/sysinfo/hkey-classes-root-key");

    private readonly IRegistryInspectionBackend _backend;

    public RegistryContextInspector(IRegistryInspectionBackend backend) =>
        _backend = backend;

    public IReadOnlyList<RegistryInspectionFinding> Inspect(
        WindowsIdentity windows,
        Action<RegistryInspectionProgress>? progress = null)
    {
        const int totalStages = 6;
        var findings = new List<RegistryInspectionFinding>();
        ReportProgress(progress, 0, totalStages, "Startup entries");
        InspectRunKeys(windows, findings);
        ReportProgress(progress, 1, totalStages, "AppInit");
        InspectAppInit(windows, findings);
        ReportProgress(progress, 2, totalStages, "Image File Execution Options");
        InspectIfeo(windows, findings);
        ReportProgress(progress, 3, totalStages, "Winlogon");
        InspectWinlogon(findings);
        ReportProgress(progress, 4, totalStages, "Windows services");
        InspectServices(windows, findings);
        ReportProgress(progress, 5, totalStages, "File associations");
        InspectFileAssociations(windows, findings);
        ReportProgress(progress, totalStages, totalStages, "Complete");
        return findings;
    }

    private static void ReportProgress(
        Action<RegistryInspectionProgress>? progress,
        int completedStages,
        int totalStages,
        string stage) =>
        progress?.Invoke(new RegistryInspectionProgress(completedStages, totalStages, stage));

    private void InspectRunKeys(
        WindowsIdentity windows,
        ICollection<RegistryInspectionFinding> findings)
    {
        foreach (RegistryViewId view in Views(windows))
        {
            foreach (RegistryHiveId hive in new[]
                     {
                         RegistryHiveId.LocalMachine,
                         RegistryHiveId.CurrentUser,
                     })
            {
                foreach (string leaf in new[] { "Run", "RunOnce" })
                {
                    var address = new RegistryAddress(
                        hive,
                        view,
                        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\{leaf}",
                        string.Empty);
                    RegistryKeyReadResult read = _backend.ReadKey(address);
                    if (!HandleReadFailure(
                            read,
                            address,
                            RegistryInspectionCategory.Startup,
                            "STARTUP_KEY_UNREADABLE",
                            RunSource,
                            findings) ||
                        read.Snapshot?.KeyExists != true)
                        continue;

                    foreach ((string name, RawRegistryValue value) in read.Snapshot.Values)
                    {
                        RegistryAddress valueAddress = address with { ValueName = name };
                        if (!TryDecodeRegistryString(value, out string command, out string error))
                        {
                            findings.Add(Finding(
                                "STARTUP_VALUE_MALFORMED",
                                RegistryInspectionCategory.Startup,
                                RegistryInspectionStatus.Malformed,
                                valueAddress,
                                "Startup value has an invalid registry type or string encoding.",
                                [error, $"Type={(uint)value.Type}", $"Bytes={value.Data.Length}"],
                                RunSource));
                        }
                        else if (string.IsNullOrWhiteSpace(command))
                        {
                            findings.Add(Finding(
                                "STARTUP_COMMAND_EMPTY",
                                RegistryInspectionCategory.Startup,
                                RegistryInspectionStatus.Malformed,
                                valueAddress,
                                "Startup value contains no command.",
                                ["Decoded command is empty."],
                                RunSource));
                        }
                    }
                }
            }
        }
    }

    private void InspectAppInit(
        WindowsIdentity windows,
        ICollection<RegistryInspectionFinding> findings)
    {
        foreach (RegistryViewId view in Views(windows))
        {
            var key = new RegistryAddress(
                RegistryHiveId.LocalMachine,
                view,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows",
                string.Empty);
            RegistryKeyReadResult read = _backend.ReadKey(key);
            if (!HandleReadFailure(
                    read,
                    key,
                    RegistryInspectionCategory.AppInit,
                    "APPINIT_KEY_UNREADABLE",
                    AppInitSource,
                    findings) ||
                read.Snapshot?.KeyExists != true)
                continue;

            IReadOnlyDictionary<string, RawRegistryValue> values = read.Snapshot.Values;
            bool loadEnabled = false;
            if (values.TryGetValue("LoadAppInit_DLLs", out RawRegistryValue? load))
            {
                if (!TryDecodeBooleanDword(load, out loadEnabled))
                {
                    findings.Add(Finding(
                        "APPINIT_LOAD_FLAG_MALFORMED",
                        RegistryInspectionCategory.AppInit,
                        RegistryInspectionStatus.Malformed,
                        key with { ValueName = "LoadAppInit_DLLs" },
                        "AppInit activation flag is not a DWORD containing 0 or 1.",
                        [$"Type={(uint)load.Type}", $"Bytes={load.Data.Length}"],
                        AppInitSource));
                }
            }

            string dllList = string.Empty;
            bool dllListValid = true;
            if (values.TryGetValue("AppInit_DLLs", out RawRegistryValue? dlls))
            {
                dllListValid = TryDecodeRegistryString(dlls, out dllList, out string error);
                if (!dllListValid)
                {
                    findings.Add(Finding(
                        "APPINIT_DLL_LIST_MALFORMED",
                        RegistryInspectionCategory.AppInit,
                        RegistryInspectionStatus.Malformed,
                        key with { ValueName = "AppInit_DLLs" },
                        "AppInit DLL list has an invalid registry type or string encoding.",
                        [error, $"Type={(uint)dlls.Type}"],
                        AppInitSource));
                }
            }

            if (values.TryGetValue("RequireSignedAppInit_DLLs", out RawRegistryValue? signed) &&
                !TryDecodeBooleanDword(signed, out _))
            {
                findings.Add(Finding(
                    "APPINIT_SIGNATURE_FLAG_MALFORMED",
                    RegistryInspectionCategory.AppInit,
                    RegistryInspectionStatus.Malformed,
                    key with { ValueName = "RequireSignedAppInit_DLLs" },
                    "AppInit signature requirement is not a DWORD containing 0 or 1.",
                    [$"Type={(uint)signed.Type}", $"Bytes={signed.Data.Length}"],
                    AppInitSource));
            }

            if (loadEnabled && dllListValid && !string.IsNullOrWhiteSpace(dllList))
            {
                findings.Add(Finding(
                    "APPINIT_DLLS_ACTIVE",
                    RegistryInspectionCategory.AppInit,
                    RegistryInspectionStatus.Review,
                    key with { ValueName = "AppInit_DLLs" },
                    "AppInit DLL injection is configured and enabled.",
                    [$"Configured DLLs={dllList}"],
                    AppInitSource));
            }
        }
    }

    private void InspectIfeo(
        WindowsIdentity windows,
        ICollection<RegistryInspectionFinding> findings)
    {
        foreach (RegistryViewId view in Views(windows))
        {
            var root = new RegistryAddress(
                RegistryHiveId.LocalMachine,
                view,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
                string.Empty);
            RegistrySubKeyReadResult subKeys = _backend.EnumerateSubKeyNames(root);
            if (!subKeys.Success)
            {
                findings.Add(Finding(
                    "IFEO_KEY_UNREADABLE",
                    RegistryInspectionCategory.ImageFileExecutionOptions,
                    RegistryInspectionStatus.Unreadable,
                    root,
                    "Image File Execution Options could not be enumerated.",
                    [subKeys.ErrorCode ?? "unknown error", subKeys.ErrorMessage ?? string.Empty],
                    IfeoSource));
                continue;
            }
            if (!subKeys.KeyExists)
                continue;

            foreach (string imageName in subKeys.SubKeyNames)
            {
                RegistryAddress imageKey = root with
                {
                    KeyPath = root.KeyPath + "\\" + imageName,
                };
                RegistryKeyReadResult read = _backend.ReadKey(imageKey);
                if (!HandleReadFailure(
                        read,
                        imageKey,
                        RegistryInspectionCategory.ImageFileExecutionOptions,
                        "IFEO_IMAGE_KEY_UNREADABLE",
                        IfeoSource,
                        findings) ||
                    read.Snapshot?.KeyExists != true ||
                    !read.Snapshot.Values.TryGetValue("Debugger", out RawRegistryValue? debugger))
                    continue;

                RegistryAddress debuggerAddress = imageKey with { ValueName = "Debugger" };
                if (!TryDecodeRegistryString(debugger, out string command, out string error))
                {
                    findings.Add(Finding(
                        "IFEO_DEBUGGER_MALFORMED",
                        RegistryInspectionCategory.ImageFileExecutionOptions,
                        RegistryInspectionStatus.Malformed,
                        debuggerAddress,
                        "IFEO Debugger has an invalid registry type or string encoding.",
                        [error, $"Type={(uint)debugger.Type}"],
                        IfeoSource));
                }
                else if (!string.IsNullOrWhiteSpace(command))
                {
                    findings.Add(Finding(
                        "IFEO_DEBUGGER_CONFIGURED",
                        RegistryInspectionCategory.ImageFileExecutionOptions,
                        RegistryInspectionStatus.Review,
                        debuggerAddress,
                        "A debugger is configured for this executable.",
                        [$"Image={imageName}", $"Debugger={command}"],
                        IfeoSource));
                }
            }
        }
    }

    private void InspectWinlogon(ICollection<RegistryInspectionFinding> findings)
    {
        var key = new RegistryAddress(
            RegistryHiveId.LocalMachine,
            RegistryViewId.Registry64,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
            string.Empty);
        RegistryKeyReadResult read = _backend.ReadKey(key);
        if (!HandleReadFailure(
                read,
                key,
                RegistryInspectionCategory.Winlogon,
                "WINLOGON_KEY_UNREADABLE",
                WinlogonSource,
                findings) ||
            read.Snapshot?.KeyExists != true)
            return;

        InspectWinlogonString(
            read.Snapshot.Values,
            key,
            "Shell",
            IsStandardShell,
            "WINLOGON_SHELL_REVIEW",
            "Winlogon uses a shell other than the standard Windows shell.",
            findings);
        InspectWinlogonString(
            read.Snapshot.Values,
            key,
            "Userinit",
            IsStandardUserinit,
            "WINLOGON_USERINIT_REVIEW",
            "Winlogon Userinit contains an additional or non-standard command.",
            findings);
    }

    private void InspectServices(
        WindowsIdentity windows,
        ICollection<RegistryInspectionFinding> findings)
    {
        RegistryViewId view = SystemView(windows);
        var selectKey = new RegistryAddress(
            RegistryHiveId.LocalMachine,
            view,
            @"SYSTEM\Select",
            string.Empty);
        RegistryKeyReadResult select = _backend.ReadKey(selectKey);
        if (!HandleReadFailure(
                select,
                selectKey,
                RegistryInspectionCategory.Service,
                "SERVICE_CONTROL_SET_UNREADABLE",
                ServiceSource,
                findings) ||
            select.Snapshot?.KeyExists != true)
            return;

        if (!select.Snapshot.Values.TryGetValue("Current", out RawRegistryValue? currentValue))
            return;
        if (!TryDecodeDword(currentValue, out uint current) || current is 0 or > 999)
        {
            findings.Add(Finding(
                "SERVICE_CONTROL_SET_MALFORMED",
                RegistryInspectionCategory.Service,
                RegistryInspectionStatus.Malformed,
                selectKey with { ValueName = "Current" },
                "The active Windows control set number is malformed.",
                [$"Type={(uint)currentValue.Type}", $"Bytes={currentValue.Data.Length}"],
                ServiceSource));
            return;
        }

        var servicesRoot = new RegistryAddress(
            RegistryHiveId.LocalMachine,
            view,
            $@"SYSTEM\ControlSet{current:000}\Services",
            string.Empty);
        RegistrySubKeyReadResult services = _backend.EnumerateSubKeyNames(servicesRoot);
        if (!services.Success)
        {
            findings.Add(Finding(
                "SERVICE_ROOT_UNREADABLE",
                RegistryInspectionCategory.Service,
                RegistryInspectionStatus.Unreadable,
                servicesRoot,
                "The active Windows services tree could not be enumerated.",
                [services.ErrorCode ?? "unknown error", services.ErrorMessage ?? string.Empty],
                ServiceSource));
            return;
        }
        if (!services.KeyExists)
            return;

        foreach (string serviceName in services.SubKeyNames)
        {
            RegistryAddress serviceKey = servicesRoot with
            {
                KeyPath = servicesRoot.KeyPath + "\\" + serviceName,
            };
            RegistryKeyReadResult read = _backend.ReadKey(serviceKey);
            if (!HandleReadFailure(
                    read,
                    serviceKey,
                    RegistryInspectionCategory.Service,
                    "SERVICE_KEY_UNREADABLE",
                    ServiceSource,
                    findings) ||
                read.Snapshot?.KeyExists != true)
                continue;

            InspectServiceDword(read.Snapshot.Values, serviceKey, serviceName, "Type", false, findings);
            InspectServiceDword(read.Snapshot.Values, serviceKey, serviceName, "Start", true, findings);
            if (!read.Snapshot.Values.TryGetValue("ImagePath", out RawRegistryValue? imagePath))
                continue;
            if (!TryDecodeRegistryString(imagePath, out _, out string error))
            {
                findings.Add(Finding(
                    "SERVICE_IMAGE_PATH_MALFORMED",
                    RegistryInspectionCategory.Service,
                    RegistryInspectionStatus.Malformed,
                    serviceKey with { ValueName = "ImagePath" },
                    "A Windows service ImagePath has an invalid registry type or string encoding.",
                    [$"Service={serviceName}", error, $"Type={(uint)imagePath.Type}"],
                    ServiceSource));
            }
        }
    }

    private static void InspectServiceDword(
        IReadOnlyDictionary<string, RawRegistryValue> values,
        RegistryAddress serviceKey,
        string serviceName,
        string valueName,
        bool validateStartRange,
        ICollection<RegistryInspectionFinding> findings)
    {
        if (!values.TryGetValue(valueName, out RawRegistryValue? value))
            return;
        if (TryDecodeDword(value, out uint decoded) && (!validateStartRange || decoded <= 4))
            return;

        findings.Add(Finding(
            validateStartRange ? "SERVICE_START_MALFORMED" : "SERVICE_TYPE_MALFORMED",
            RegistryInspectionCategory.Service,
            RegistryInspectionStatus.Malformed,
            serviceKey with { ValueName = valueName },
            $"A Windows service {valueName} value is malformed.",
            [$"Service={serviceName}", $"Type={(uint)value.Type}", $"Bytes={value.Data.Length}"],
            ServiceSource));
    }

    private void InspectFileAssociations(
        WindowsIdentity windows,
        ICollection<RegistryInspectionFinding> findings)
    {
        RegistryViewId view = SystemView(windows);
        IReadOnlyDictionary<string, AssociationReference> machine =
            ReadAssociationReferences(RegistryHiveId.LocalMachine, view, findings);
        IReadOnlyDictionary<string, AssociationReference> user =
            ReadAssociationReferences(RegistryHiveId.CurrentUser, view, findings);

        var effective = new Dictionary<string, AssociationReference>(machine, StringComparer.OrdinalIgnoreCase);
        foreach ((string extension, AssociationReference association) in user)
            effective[extension] = association;

        foreach ((string extension, AssociationReference association) in effective)
        {
            AssociationLookupResult lookup = LookupAssociationProgId(association.ProgId, view);
            if (lookup.State == AssociationLookupState.Exists)
                continue;

            if (lookup.State == AssociationLookupState.Unreadable)
            {
                findings.Add(Finding(
                    "FILE_ASSOCIATION_PROGID_UNREADABLE",
                    RegistryInspectionCategory.FileAssociation,
                    RegistryInspectionStatus.Unreadable,
                    association.Address,
                    "The ProgID referenced by a file extension could not be verified.",
                    [$"Extension={extension}", $"ProgID={association.ProgId}", .. lookup.Evidence],
                    FileAssociationSource));
                continue;
            }

            findings.Add(Finding(
                "FILE_ASSOCIATION_PROGID_MISSING",
                RegistryInspectionCategory.FileAssociation,
                RegistryInspectionStatus.Review,
                association.Address,
                "A file extension references a ProgID that is not registered.",
                [$"Extension={extension}", $"ProgID={association.ProgId}"],
                FileAssociationSource));
        }
    }

    private IReadOnlyDictionary<string, AssociationReference> ReadAssociationReferences(
        RegistryHiveId hive,
        RegistryViewId view,
        ICollection<RegistryInspectionFinding> findings)
    {
        var root = new RegistryAddress(hive, view, @"SOFTWARE\Classes", string.Empty);
        RegistrySubKeyReadResult subKeys = _backend.EnumerateSubKeyNames(root);
        if (!subKeys.Success)
        {
            findings.Add(Finding(
                "FILE_ASSOCIATION_ROOT_UNREADABLE",
                RegistryInspectionCategory.FileAssociation,
                RegistryInspectionStatus.Unreadable,
                root,
                "File associations could not be enumerated.",
                [subKeys.ErrorCode ?? "unknown error", subKeys.ErrorMessage ?? string.Empty],
                FileAssociationSource));
            return new Dictionary<string, AssociationReference>(StringComparer.OrdinalIgnoreCase);
        }

        var associations = new Dictionary<string, AssociationReference>(StringComparer.OrdinalIgnoreCase);
        foreach (string extension in subKeys.SubKeyNames.Where(IsFileExtensionKey))
        {
            RegistryAddress key = root with { KeyPath = root.KeyPath + "\\" + extension };
            RegistryKeyReadResult read = _backend.ReadKey(key);
            if (!HandleReadFailure(
                    read,
                    key,
                    RegistryInspectionCategory.FileAssociation,
                    "FILE_ASSOCIATION_KEY_UNREADABLE",
                    FileAssociationSource,
                    findings) ||
                read.Snapshot?.KeyExists != true ||
                !read.Snapshot.Values.TryGetValue(string.Empty, out RawRegistryValue? defaultValue))
                continue;

            RegistryAddress valueAddress = key with { ValueName = string.Empty };
            if (!TryDecodeRegistryString(defaultValue, out string progId, out string error))
            {
                findings.Add(Finding(
                    "FILE_ASSOCIATION_VALUE_MALFORMED",
                    RegistryInspectionCategory.FileAssociation,
                    RegistryInspectionStatus.Malformed,
                    valueAddress,
                    "A file association has an invalid registry type or string encoding.",
                    [$"Extension={extension}", error, $"Type={(uint)defaultValue.Type}"],
                    FileAssociationSource));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(progId))
                associations[extension] = new AssociationReference(valueAddress, progId.Trim());
        }

        return associations;
    }

    private AssociationLookupResult LookupAssociationProgId(string progId, RegistryViewId view)
    {
        var errors = new List<string>();
        foreach (RegistryHiveId hive in new[]
                 {
                     RegistryHiveId.CurrentUser,
                     RegistryHiveId.LocalMachine,
                 })
        {
            var address = new RegistryAddress(
                hive,
                view,
                @"SOFTWARE\Classes\" + progId,
                string.Empty);
            RegistryKeyReadResult read = _backend.ReadKey(address);
            if (read.Success && read.Snapshot?.KeyExists == true)
                return new AssociationLookupResult(AssociationLookupState.Exists, []);
            if (!read.Success)
                errors.Add($"{hive}: {read.ErrorCode ?? "unknown error"} - {read.ErrorMessage ?? string.Empty}");
        }
        return errors.Count == 0
            ? new AssociationLookupResult(AssociationLookupState.Missing, [])
            : new AssociationLookupResult(AssociationLookupState.Unreadable, errors);
    }

    private static void InspectWinlogonString(
        IReadOnlyDictionary<string, RawRegistryValue> values,
        RegistryAddress key,
        string valueName,
        Func<string, bool> isStandard,
        string reviewCode,
        string reviewSummary,
        ICollection<RegistryInspectionFinding> findings)
    {
        if (!values.TryGetValue(valueName, out RawRegistryValue? value))
            return;

        RegistryAddress address = key with { ValueName = valueName };
        if (!TryDecodeRegistryString(value, out string decoded, out string error))
        {
            findings.Add(Finding(
                "WINLOGON_VALUE_MALFORMED",
                RegistryInspectionCategory.Winlogon,
                RegistryInspectionStatus.Malformed,
                address,
                $"Winlogon {valueName} has an invalid registry type or string encoding.",
                [error, $"Type={(uint)value.Type}"],
                WinlogonSource));
        }
        else if (!isStandard(decoded))
        {
            findings.Add(Finding(
                reviewCode,
                RegistryInspectionCategory.Winlogon,
                RegistryInspectionStatus.Review,
                address,
                reviewSummary,
                [$"Configured value={decoded}", "A custom shell can be legitimate."],
                WinlogonSource));
        }
    }

    private static bool HandleReadFailure(
        RegistryKeyReadResult read,
        RegistryAddress address,
        RegistryInspectionCategory category,
        string code,
        Uri source,
        ICollection<RegistryInspectionFinding> findings)
    {
        if (read.Success)
            return true;

        findings.Add(Finding(
            code,
            category,
            RegistryInspectionStatus.Unreadable,
            address,
            "Registry key could not be read.",
            [read.ErrorCode ?? "unknown error", read.ErrorMessage ?? string.Empty],
            source));
        return false;
    }

    private static RegistryInspectionFinding Finding(
        string code,
        RegistryInspectionCategory category,
        RegistryInspectionStatus status,
        RegistryAddress address,
        string summary,
        IReadOnlyList<string> evidence,
        Uri source) =>
        new(code, category, status, address, summary, evidence, source);

    private static IEnumerable<RegistryViewId> Views(WindowsIdentity windows) =>
        windows.Is64BitOperatingSystem
            ? new[] { RegistryViewId.Registry64, RegistryViewId.Registry32 }
            : new[] { RegistryViewId.Registry32 };

    private static bool TryDecodeBooleanDword(
        RawRegistryValue value,
        out bool enabled)
    {
        enabled = false;
        if (value.Type != RegistryValueType.DWord || value.Data.Length != sizeof(int))
            return false;

        int raw = BinaryPrimitives.ReadInt32LittleEndian(value.Data);
        if (raw is not 0 and not 1)
            return false;
        enabled = raw == 1;
        return true;
    }

    private static bool TryDecodeDword(RawRegistryValue value, out uint decoded)
    {
        decoded = 0;
        if (value.Type != RegistryValueType.DWord || value.Data.Length != sizeof(uint))
            return false;
        decoded = BinaryPrimitives.ReadUInt32LittleEndian(value.Data);
        return true;
    }

    private static bool TryDecodeRegistryString(
        RawRegistryValue value,
        out string decoded,
        out string error)
    {
        decoded = string.Empty;
        if (value.Type is not RegistryValueType.String and not RegistryValueType.ExpandString)
        {
            error = "Expected REG_SZ or REG_EXPAND_SZ.";
            return false;
        }
        if (value.Data.Length < sizeof(char) || value.Data.Length % sizeof(char) != 0)
        {
            error = "UTF-16 byte length is invalid.";
            return false;
        }
        if (value.Data[^1] != 0 || value.Data[^2] != 0)
        {
            error = "The string has no UTF-16 terminator.";
            return false;
        }

        decoded = Encoding.Unicode.GetString(value.Data).TrimEnd('\0');
        if (decoded.Contains('\0'))
        {
            error = "The string contains an embedded null character.";
            decoded = string.Empty;
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsStandardShell(string value) =>
        string.Equals(value.Trim(), "explorer.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsStandardUserinit(string value)
    {
        string[] commands = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return commands.Length == 1 &&
               string.Equals(
                   Path.GetFileName(commands[0]),
                   "userinit.exe",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static RegistryViewId SystemView(WindowsIdentity windows) =>
        windows.Is64BitOperatingSystem
            ? RegistryViewId.Registry64
            : RegistryViewId.Registry32;

    private static bool IsFileExtensionKey(string keyName) =>
        keyName.Length > 1 && keyName[0] == '.';

    private sealed record AssociationReference(RegistryAddress Address, string ProgId);

    private enum AssociationLookupState
    {
        Exists,
        Missing,
        Unreadable,
    }

    private sealed record AssociationLookupResult(
        AssociationLookupState State,
        IReadOnlyList<string> Evidence);
}
