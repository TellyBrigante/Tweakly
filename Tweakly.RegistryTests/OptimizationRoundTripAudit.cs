using Microsoft.Win32;
using Optimisation_Tool.Helpers;
using Optimisation_Tool.Pages;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

internal sealed record OptimizationAuditResult(bool Success, string Report);

internal static class OptimizationRoundTripAudit
{
    private const string TcpInterfaces =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string NetworkClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    public static OptimizationAuditResult Run()
    {
        var lines = new List<string>();
        bool success = true;
        if (!IsAdministrator())
            return new OptimizationAuditResult(false,
                "Audit refusé : le processus de test n'est pas administrateur.");

        lines.Add($"Audit des optimisations — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        lines.Add($"Utilisateur : {Environment.UserName} | Processus 64 bits : {Environment.Is64BitProcess}");

        SystemSnapshot snapshot;
        try
        {
            snapshot = SystemSnapshot.Capture();
            lines.Add("[OK] Snapshot initial capturé.");
        }
        catch (Exception ex)
        {
            lines.Add("[ÉCHEC] Snapshot initial : " + ex.Message);
            lines.Add("RÉSULTAT : ÉCHEC — aucune modification appliquée");
            return new OptimizationAuditResult(false, string.Join(Environment.NewLine, lines));
        }
        try
        {
            RunGroup(
                typeof(PageWindows), "ApplyChanges", 7, "ReadState",
                new[]
                {
                    new Case("HAGS", 0, "ACTIVÉ", "restauré"),
                    new Case("Barre de jeu Xbox", 1, "DÉSACTIVÉE", "ACTIVÉE"),
                    new Case("Enregistrement DVR", 2, "DÉSACTIVÉ", "ACTIVÉ"),
                    new Case("Priorité GPU", 3, "forcée", "restauré"),
                    new Case("Mode MSI GPU", 4, "ACTIVÉ", "DÉSACTIVÉ"),
                },
                lines, ref success);

            RunGroup(
                typeof(PageWindows), "ApplyChanges2", 3, "ReadState2",
                new[]
                {
                    new Case("Mode Jeu Windows", 0, "ACTIVÉ", "désactivé"),
                    new Case("Optimisations jeux fenêtrés", 1, "ACTIVÉES", "désactivées"),
                    new Case("Popups d'accessibilité", 2, "COUPÉS", "restaurés"),
                },
                lines, ref success);

            TestUltimatePowerPlanLifecycle(lines, ref success);

            RunGroup(
                typeof(PageCPU), "ApplyChanges", 4, "ReadState",
                new[]
                {
                    new Case("Performances ultimes", 0, "activé", "restauré"),
                    new Case("Power Throttling", 1, "DÉSACTIVÉ", "ACTIVÉ"),
                    new Case("SystemResponsiveness", 2, "0 (jeux)", "20 (par défaut)"),
                    new Case("Memory Integrity (HVCI)", 3, "DÉSACTIVÉ", "restauré"),
                },
                lines, ref success);

            RunGroup(
                typeof(PageReseau), "ApplyChanges", 5, "ReadState",
                new[]
                {
                    new Case("Nagle", 0, "DÉSACTIVÉ", "ACTIVÉ"),
                    new Case("DNS", 1, "1.1.1.1 / 8.8.8.8", "automatique"),
                    new Case("Veille adaptateur réseau", 2, "DÉSACTIVÉE", "RESTAURÉE"),
                    new Case("WPAD", 3, "DÉSACTIVÉ", "ACTIVÉ"),
                    new Case("Bridage réseau", 4, "DÉSACTIVÉ", "restauré"),
                },
                lines, ref success);

            RunGroup(
                typeof(PagePrivacy), "ApplyChanges", 9, "ReadState",
                new[]
                {
                    new Case("Télémétrie Windows", 0, "DÉSACTIVÉE", "RESTAURÉE"),
                    new Case("Identifiant publicitaire", 1, "DÉSACTIVÉ", "ACTIVÉ"),
                    new Case("Historique d'activité", 2, "DÉSACTIVÉ", "ACTIVÉ"),
                    new Case("Recherche Bing", 3, "DÉSACTIVÉE", "ACTIVÉE"),
                    new Case("Personnalisation de saisie", 4, "DÉSACTIVÉE", "ACTIVÉE"),
                    new Case("Localisation", 5, "DÉSACTIVÉE", "ACTIVÉE"),
                    new Case("Rapports d'erreurs Windows", 6, "DÉSACTIVÉ", "ACTIVÉ"),
                    new Case("Expériences personnalisées", 7, "DÉSACTIVÉES", "ACTIVÉES"),
                    new Case("Inventaire applications", 8, "DÉSACTIVÉE", "ACTIVÉE"),
                },
                lines, ref success);

            TestExternalEditors(lines, ref success);
        }
        catch (Exception ex)
        {
            success = false;
            lines.Add($"[ÉCHEC] Audit interrompu : {ex.GetBaseException().Message}");
        }
        finally
        {
            bool restored = snapshot.Restore(out IReadOnlyList<string> restoreErrors);
            if (restored)
                lines.Add("[OK] État initial restauré et vérifié.");
            else
            {
                success = false;
                lines.Add("[ÉCHEC] Restauration de l'état initial incomplète : " +
                          string.Join(" | ", restoreErrors));
            }
        }

        lines.Add(success ? "RÉSULTAT : OK" : "RÉSULTAT : ÉCHEC");
        return new OptimizationAuditResult(success, string.Join(Environment.NewLine, lines));
    }

    private static void RunGroup(
        Type pageType,
        string applyMethodName,
        int settingCount,
        string readMethodName,
        IReadOnlyList<Case> cases,
        ICollection<string> lines,
        ref bool success)
    {
        MethodInfo apply = pageType.GetMethod(
            applyMethodName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{pageType.Name}.{applyMethodName} introuvable");
        MethodInfo read = pageType.GetMethod(
            readMethodName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{pageType.Name}.{readMethodName} introuvable");

        List<bool> original = ReadState(read);
        foreach (Case testCase in cases)
        {
            bool caseSuccess = true;
            try
            {
                caseSuccess &= ApplyAndVerify(
                    apply, read, settingCount, testCase, true, testCase.OnToken, lines);
                caseSuccess &= ApplyAndVerify(
                    apply, read, settingCount, testCase, false, testCase.OffToken, lines);
            }
            catch (Exception ex)
            {
                caseSuccess = false;
                lines.Add($"[ÉCHEC] {testCase.Name} : {ex.GetBaseException().Message}");
            }
            finally
            {
                try
                {
                    ApplySingle(apply, settingCount, testCase.Index, original[testCase.Index]);
                    bool restored = ReadState(read)[testCase.Index] == original[testCase.Index];
                    if (!restored)
                    {
                        caseSuccess = false;
                        lines.Add($"[ÉCHEC] {testCase.Name} : état fonctionnel initial non restauré");
                    }
                }
                catch (Exception ex)
                {
                    caseSuccess = false;
                    lines.Add($"[ÉCHEC] {testCase.Name} : restauration fonctionnelle — " +
                              ex.GetBaseException().Message);
                }
            }

            if (caseSuccess)
                lines.Add($"[OK] {testCase.Name} : coché / décoché / message / relecture");
            success &= caseSuccess;
        }
    }

    private static bool ApplyAndVerify(
        MethodInfo apply,
        MethodInfo read,
        int settingCount,
        Case testCase,
        bool requested,
        string expectedToken,
        ICollection<string> lines)
    {
        List<string> messages = ApplySingle(
            apply, settingCount, testCase.Index, requested);
        string joined = string.Join(" | ", messages);
        if (messages.Any(FeedbackMessageClassifier.IsFailure))
        {
            lines.Add($"[ÉCHEC] {testCase.Name} {(requested ? "coché" : "décoché")} : {joined}");
            return false;
        }

        bool actual = ReadState(read)[testCase.Index];
        if (actual != requested)
        {
            lines.Add($"[ÉCHEC] {testCase.Name} : demandé={requested}, relu={actual} | {joined}");
            return false;
        }

        if (!joined.Contains(expectedToken, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"[ÉCHEC] {testCase.Name} : message inattendu pour {(requested ? "coché" : "décoché")} | {joined}");
            return false;
        }
        return true;
    }

    private static List<string> ApplySingle(
        MethodInfo apply,
        int settingCount,
        int index,
        bool value)
    {
        var messages = new List<string>();
        var parameters = new object?[settingCount + 1];
        for (int i = 0; i < settingCount; i++) parameters[i] = null;
        parameters[index] = (bool?)value;
        parameters[^1] = (Action<string>)(message => messages.Add(message));
        apply.Invoke(null, parameters);
        return messages;
    }

    private static List<bool> ReadState(MethodInfo read)
    {
        object tuple = read.Invoke(null, null)
            ?? throw new InvalidOperationException($"{read.Name} a retourné null");
        return FlattenTuple(tuple).Select(value => Convert.ToBoolean(value)).ToList();
    }

    private static IEnumerable<object?> FlattenTuple(object tuple)
    {
        Type type = tuple.GetType();
        for (int i = 1; i <= 7; i++)
        {
            FieldInfo? item = type.GetField($"Item{i}");
            if (item == null) break;
            yield return item.GetValue(tuple);
        }

        object? rest = type.GetField("Rest")?.GetValue(tuple);
        if (rest == null) yield break;
        foreach (object? value in FlattenTuple(rest)) yield return value;
    }

    private static void TestExternalEditors(ICollection<string> lines, ref bool success)
    {
        string json = Path.Combine(Path.GetTempPath(), $"tweakly-discord-{Guid.NewGuid():N}.json");
        string vdf = Path.Combine(Path.GetTempPath(), $"tweakly-steam-{Guid.NewGuid():N}.vdf");
        try
        {
            File.WriteAllText(json,
                "{\"enableHardwareAcceleration\":true,\"nested\":{\"keep\":7}}",
                new UTF8Encoding(false));
            JsonSettingsEditor.SetBooleanAtomically(json, "enableHardwareAcceleration", false);
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            bool discordOk = !document.RootElement.GetProperty("enableHardwareAcceleration").GetBoolean() &&
                             document.RootElement.GetProperty("nested").GetProperty("keep").GetInt32() == 7;

            File.WriteAllText(vdf, "\"EnableGameOverlay\"\t\t\"1\"", new UTF8Encoding(false));
            MethodInfo steamEditor = typeof(PageWindows).GetMethod(
                "SetSteamOverlay", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Éditeur Steam introuvable");
            steamEditor.Invoke(null, new object[] { vdf, true });
            bool steamOff = Regex.IsMatch(
                File.ReadAllText(vdf), "\\\"EnableGameOverlay\\\"\\s+\\\"0\\\"");
            steamEditor.Invoke(null, new object[] { vdf, false });
            bool steamOn = Regex.IsMatch(
                File.ReadAllText(vdf), "\\\"EnableGameOverlay\\\"\\s+\\\"1\\\"");

            if (discordOk && steamOff && steamOn)
                lines.Add("[OK] Discord / Steam : édition atomique et relecture sur copies temporaires");
            else
            {
                success = false;
                lines.Add("[ÉCHEC] Discord / Steam : résultat de fichier incorrect");
            }
        }
        catch (Exception ex)
        {
            success = false;
            lines.Add("[ÉCHEC] Discord / Steam : " + ex.GetBaseException().Message);
        }
        finally
        {
            try { File.Delete(json); } catch { }
            try { File.Delete(vdf); } catch { }
        }
    }

    private static void TestUltimatePowerPlanLifecycle(
        ICollection<string> lines,
        ref bool success)
    {
        MethodInfo apply = typeof(PageCPU).GetMethod(
            "ApplyChanges", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("PageCPU.ApplyChanges introuvable");
        MethodInfo read = typeof(PageCPU).GetMethod(
            "ReadState", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("PageCPU.ReadState introuvable");

        Guid originalActive = ReadActivePowerSchemeGuid();
        HashSet<Guid> originalSchemes = ReadPowerSchemeIds();
        bool caseSuccess = true;
        try
        {
            List<string> firstMessages = ApplySingle(apply, 4, 0, true);
            HashSet<Guid> afterFirst = ReadPowerSchemeIds();
            List<string> secondMessages = ApplySingle(apply, 4, 0, true);
            HashSet<Guid> afterSecond = ReadPowerSchemeIds();

            bool messagesOk = !firstMessages.Concat(secondMessages)
                .Any(FeedbackMessageClassifier.IsFailure);
            bool activeOk = ReadState(read)[0];
            bool firstCreationBounded = afterFirst.Except(originalSchemes).Count() <= 1;
            bool secondActivationIdempotent = afterFirst.SetEquals(afterSecond);
            caseSuccess = messagesOk && activeOk && firstCreationBounded && secondActivationIdempotent;

            if (caseSuccess)
            {
                lines.Add("[OK] Performances ultimes : activation répétée sans nouveau profil");
            }
            else
            {
                lines.Add(
                    "[ÉCHEC] Performances ultimes : " +
                    $"messages={messagesOk}, actif={activeOk}, " +
                    $"créations={afterFirst.Except(originalSchemes).Count()}, " +
                    $"nouveau profil au second passage={!secondActivationIdempotent}");
            }
        }
        catch (Exception ex)
        {
            caseSuccess = false;
            lines.Add("[ÉCHEC] Performances ultimes : " + ex.GetBaseException().Message);
        }
        finally
        {
            CommandResult restore = Run("powercfg", $"/setactive {originalActive:D}");
            if (!restore.Success)
            {
                caseSuccess = false;
                lines.Add("[ÉCHEC] Performances ultimes : restauration du plan actif impossible");
            }

            foreach (Guid extra in ReadPowerSchemeIds().Except(originalSchemes))
            {
                CommandResult delete = Run("powercfg", $"/delete {extra:D}");
                if (!delete.Success)
                {
                    caseSuccess = false;
                    lines.Add($"[ÉCHEC] Performances ultimes : profil de test {extra:D} non supprimé");
                }
            }
        }

        success &= caseSuccess;
    }

    private static Guid ReadActivePowerSchemeGuid()
    {
        CommandResult result = Run("powercfg", "/getactivescheme");
        Match match = Regex.Match(result.Output, @"[0-9a-f-]{36}", RegexOptions.IgnoreCase);
        if (!result.Success || !match.Success || !Guid.TryParse(match.Value, out Guid value))
            throw new InvalidOperationException("plan d'alimentation actif illisible");
        return value;
    }

    private static HashSet<Guid> ReadPowerSchemeIds()
    {
        CommandResult result = Run("powercfg", "/list");
        if (!result.Success)
            throw new InvalidOperationException("liste des plans d'alimentation inaccessible");

        return Regex.Matches(result.Output, @"[0-9a-f-]{36}", RegexOptions.IgnoreCase)
            .Select(match => Guid.Parse(match.Value))
            .ToHashSet();
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record Case(string Name, int Index, string OnToken, string OffToken);

    private sealed class SystemSnapshot
    {
        private readonly List<RegistryValueSnapshot> _registry;
        private readonly string _activePowerPlan;
        private readonly List<ServiceSnapshot> _services;
        private readonly List<DnsSnapshot> _dns;

        private SystemSnapshot(
            List<RegistryValueSnapshot> registry,
            string activePowerPlan,
            List<ServiceSnapshot> services,
            List<DnsSnapshot> dns)
        {
            _registry = registry;
            _activePowerPlan = activePowerPlan;
            _services = services;
            _dns = dns;
        }

        public static SystemSnapshot Capture()
        {
            var specs = FixedRegistrySpecs();
            try
            {
                AddDynamicRegistrySpecs(specs);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("inventaire registre dynamique — " + ex.Message, ex);
            }

            List<RegistryValueSnapshot> registry;
            try
            {
                registry = specs.Distinct().Select(RegistryValueSnapshot.Capture).ToList();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("snapshot registre — " + ex.Message, ex);
            }

            string activePowerPlan;
            try { activePowerPlan = ReadActivePowerPlan(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("snapshot alimentation — " + ex.Message, ex);
            }

            List<ServiceSnapshot> services;
            try { services = CaptureServices(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("snapshot services — " + ex.Message, ex);
            }

            List<DnsSnapshot> dns;
            try { dns = CaptureDns(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("snapshot DNS — " + ex.Message, ex);
            }

            return new SystemSnapshot(registry, activePowerPlan, services, dns);
        }

        public bool Restore(out IReadOnlyList<string> errors)
        {
            var failures = new List<string>();
            foreach (RegistryValueSnapshot value in _registry.AsEnumerable().Reverse())
            {
                try { value.RestoreAndVerify(); }
                catch (Exception ex) { failures.Add($"registre {value.Spec.Name}: {ex.Message}"); }
            }

            try { RestorePowerPlan(_activePowerPlan); }
            catch (Exception ex) { failures.Add("plan d'alimentation : " + ex.Message); }

            foreach (DnsSnapshot dns in _dns)
            {
                try { dns.Restore(); }
                catch (Exception ex) { failures.Add($"DNS {dns.SettingId}: {ex.Message}"); }
            }

            foreach (ServiceSnapshot service in _services)
            {
                try { service.RestoreRuntimeState(); }
                catch (Exception ex) { failures.Add($"service {service.Name}: {ex.Message}"); }
            }

            errors = failures;
            return failures.Count == 0;
        }

        private static List<RegistrySpec> FixedRegistrySpecs() => new()
        {
            Lm(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode"),
            Cu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
            Cu(@"System\GameConfigStore", "GameDVR_Enabled"),
            Cu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled"),
            Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority"),
            Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority"),
            Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category"),
            Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "SFIO Priority"),
            Cu(@"Software\Microsoft\GameBar", "AutoGameModeEnabled"),
            Cu(@"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings"),
            Cu(@"Control Panel\Accessibility\StickyKeys", "Flags"),
            Cu(@"Control Panel\Accessibility\ToggleKeys", "Flags"),
            Cu(@"Control Panel\Accessibility\Keyboard Response", "Flags"),
            Lm(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff"),
            Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness"),
            Lm(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled"),
            Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex"),
            Lm(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpAckFrequency"),
            Lm(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpNoDelay"),
            Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp", "DisableWpad"),
            Lm(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry"),
            Cu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled"),
            Lm(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed"),
            Cu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled"),
            Cu(@"SOFTWARE\Microsoft\InputPersonalization", "RestrictImplicitInkCollection"),
            Cu(@"SOFTWARE\Microsoft\InputPersonalization", "RestrictImplicitTextCollection"),
            Lm(@"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation"),
            Lm(@"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled"),
            Cu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled"),
            Lm(@"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisableInventory"),
            Lm(@"SYSTEM\CurrentControlSet\Services\DiagTrack", "Start"),
            Lm(@"SYSTEM\CurrentControlSet\Services\dmwappushservice", "Start"),
            Lm(@"SYSTEM\CurrentControlSet\Services\WerSvc", "Start"),
        };

        private static void AddDynamicRegistrySpecs(ICollection<RegistrySpec> specs)
        {
            try
            {
                if (GpuMsiMode.TryGetRegistryPath(out string msiPath))
                    specs.Add(Lm(msiPath, "MSISupported"));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("chemin MSI — " + ex.Message, ex);
            }

            string[] activeIds;
            try { activeIds = ActiveNetworkIds().ToArray(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("interfaces réseau actives — " + ex.Message, ex);
            }

            foreach (string id in activeIds)
            {
                string path = $@"{TcpInterfaces}\{{{id}}}";
                specs.Add(Lm(path, "TcpAckFrequency"));
                specs.Add(Lm(path, "TcpNoDelay"));
                specs.Add(Lm(path, "NameServer"));
            }

            try
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(NetworkClass);
                if (root == null) return;
                var active = activeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                int captured = 0;
                foreach (string name in root.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? key = root.OpenSubKey(name);
                        string id = NormalizeId(Convert.ToString(key?.GetValue("NetCfgInstanceId")) ?? "");
                        if (!active.Contains(id)) continue;
                        specs.Add(Lm($@"{NetworkClass}\{name}", "PnPCapabilities"));
                        captured++;
                    }
                    catch
                    {
                        // Ancienne instance protégée : elle ne doit pas masquer l'adaptateur actif suivant.
                    }
                }
                if (active.Count > 0 && captured == 0)
                    throw new InvalidOperationException("aucun adaptateur actif sauvegardé");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("classe pilote réseau — " + ex.Message, ex);
            }
        }

        private static List<ServiceSnapshot> CaptureServices()
        {
            var result = new List<ServiceSnapshot>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, State FROM Win32_Service WHERE Name='DiagTrack' OR Name='dmwappushservice' OR Name='WerSvc'");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                    result.Add(new ServiceSnapshot(
                        Convert.ToString(obj["Name"]) ?? "",
                        string.Equals(Convert.ToString(obj["State"]), "Running", StringComparison.OrdinalIgnoreCase)));
            }
            return result;
        }

        private static List<DnsSnapshot> CaptureDns()
        {
            var result = new List<DnsSnapshot>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT SettingID, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    string id = NormalizeId(Convert.ToString(obj["SettingID"]) ?? "");
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{TcpInterfaces}\{{{id}}}");
                    bool automatic = string.IsNullOrWhiteSpace(Convert.ToString(key?.GetValue("NameServer")));
                    result.Add(new DnsSnapshot(
                        id,
                        automatic,
                        (obj["DNSServerSearchOrder"] as string[]) ?? Array.Empty<string>()));
                }
            }
            return result;
        }

        private static IEnumerable<string> ActiveNetworkIds() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                               item.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                               item.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(item => NormalizeId(item.Id));

        private static string ReadActivePowerPlan()
        {
            CommandResult result = Run("powercfg", "/getactivescheme");
            Match match = Regex.Match(result.Output, @"[0-9a-f-]{36}", RegexOptions.IgnoreCase);
            if (!result.Success || !match.Success)
                throw new InvalidOperationException("plan d'alimentation actif illisible");
            return match.Value;
        }

        private static void RestorePowerPlan(string guid)
        {
            CommandResult result = Run("powercfg", $"/setactive {guid}");
            if (!result.Success)
                throw new InvalidOperationException(result.Error);
        }

        private static RegistrySpec Lm(string path, string name) =>
            new(RegistryHive.LocalMachine, path, name);
        private static RegistrySpec Cu(string path, string name) =>
            new(RegistryHive.CurrentUser, path, name);
    }

    private sealed record RegistrySpec(RegistryHive Hive, string Path, string Name);

    private sealed record RegistryValueSnapshot(
        RegistrySpec Spec,
        bool Existed,
        object? Value,
        RegistryValueKind Kind)
    {
        public static RegistryValueSnapshot Capture(RegistrySpec spec)
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(spec.Path);
                object? value = key?.GetValue(
                    spec.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                return value == null
                    ? new RegistryValueSnapshot(spec, false, null, RegistryValueKind.Unknown)
                    : new RegistryValueSnapshot(spec, true, value, key!.GetValueKind(spec.Name));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{spec.Hive}\\{spec.Path} [{spec.Name}] — {ex.Message}", ex);
            }
        }

        public void RestoreAndVerify()
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(Spec.Hive, RegistryView.Registry64);
            if (!Existed)
            {
                using RegistryKey? key = root.OpenSubKey(Spec.Path, writable: true);
                key?.DeleteValue(Spec.Name, throwOnMissingValue: false);
            }
            else
            {
                using RegistryKey key = root.CreateSubKey(Spec.Path, writable: true)
                    ?? throw new InvalidOperationException("clé inaccessible");
                key.SetValue(Spec.Name, Value!, Kind);
                key.Flush();
            }

            using RegistryKey? verify = root.OpenSubKey(Spec.Path);
            object? actual = verify?.GetValue(
                Spec.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (Existed ? !RegistryValuesEqual(actual, Value) : actual != null)
                throw new InvalidOperationException("valeur initiale non restaurée");
        }

        private static bool RegistryValuesEqual(object? left, object? right)
        {
            if (left is byte[] leftBytes && right is byte[] rightBytes)
                return leftBytes.SequenceEqual(rightBytes);
            if (left is string[] leftStrings && right is string[] rightStrings)
                return leftStrings.SequenceEqual(rightStrings, StringComparer.Ordinal);
            return Equals(left, right);
        }
    }

    private sealed record ServiceSnapshot(string Name, bool WasRunning)
    {
        public void RestoreRuntimeState()
        {
            CommandResult current = Run("sc", $"query \"{Name}\"");
            bool running = current.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
            if (running == WasRunning) return;

            CommandResult result = Run("sc", $"{(WasRunning ? "start" : "stop")} \"{Name}\"");
            if (!result.Success && result.ExitCode is not 1056 and not 1062)
                throw new InvalidOperationException(result.Error);
        }
    }

    private sealed record DnsSnapshot(string SettingId, bool Automatic, string[] Servers)
    {
        public void Restore()
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True AND SettingID = '{{{SettingId}}}'");
            ManagementObject? adapter = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (adapter == null) throw new InvalidOperationException("adaptateur introuvable");
            using (adapter)
            {
                object? result = Automatic
                    ? adapter.InvokeMethod("SetDNSServerSearchOrder", new object?[] { null })
                    : adapter.InvokeMethod("SetDNSServerSearchOrder", new object[] { Servers });
                uint code = result == null ? 0 : Convert.ToUInt32(result);
                if (code is not 0 and not 1)
                    throw new InvalidOperationException($"code WMI {code}");
            }
        }
    }

    private static CommandResult Run(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process == null) return new CommandResult(false, "", "lancement impossible", -1);
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new CommandResult(false, output, "délai dépassé", -1);
            }
            return new CommandResult(process.ExitCode == 0, output, error.Trim(), process.ExitCode);
        }
        catch (Exception ex)
        {
            return new CommandResult(false, "", ex.Message, -1);
        }
    }

    private static string NormalizeId(string value) => value.Trim().Trim('{', '}');
    private sealed record CommandResult(bool Success, string Output, string Error, int ExitCode);
}
