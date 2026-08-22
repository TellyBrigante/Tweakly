using RegistryRepair.Core;
using RegistryRepair.Windows;

string root = Path.Combine(
    Path.GetTempPath(),
    "TweaklyRegistryHiveTests",
    Guid.NewGuid().ToString("N"));
var hive = new IsolatedRegistryHive(root);
int failed = 0;
try
{
    failed += await Run("exact raw DWORD repair and undo", DwordRepairAndUndo);
    failed += await Run("exact raw binary repair and undo", BinaryRepairAndUndo);
    failed += await Run("isolated 32 and 64-bit views", IsolatedViews);
    failed += await Run("missing key cannot be repaired", MissingKeyCannotBeRepaired);
    failed += await Run("subkeys are enumerated without write access", SubKeysAreEnumerated);
    failed += await Run("context inspection works on an isolated hive", ContextInspectionUsesIsolatedHive);
    failed += await Run("offline corpus reads copied hives only", OfflineCorpusReadsCopies);
    failed += await Run("offline corpus refuses unresolved 32-bit view", OfflineCorpusRefusesUnprovenView);
    failed += await Run("offline Windows image identity is exact", OfflineImageIdentityIsExact);
    failed += await Run("active Windows root is rejected", ActiveWindowsRootIsRejected);
}
finally
{
    hive.Dispose();
    hive.DeleteFiles();
}

Console.WriteLine($"{10 - failed}/10 Windows hive tests passed.");
return failed == 0 ? 0 : 1;

async Task DwordRepairAndUndo()
{
    RegistryAddress address = Address(RegistryViewId.Registry64, "Dword");
    hive.Seed(address, RawRegistryValue.DWord(2));
    WindowsRegistryBackend backend = hive.CreateBackend();
    using var journal = new FileRegistryRepairJournal(Path.Combine(root, "journal-dword"), JournalAuthenticationKey());
    var engine = new RegistryRepairEngine(backend, journal);
    RegistryFinding finding = Single(engine.Scan([Rule(address, RawRegistryValue.DWord(1))], Windows()));
    Equal(RegistryFindingState.WrongData, finding.State);
    RegistryRepairResult repair = await engine.RepairAsync(finding);
    True(repair.Success);
    True(ReadValue(backend, address).ContentEquals(RawRegistryValue.DWord(1)));
    True((await engine.UndoAsync(repair.Transaction.Id)).Success);
    True(ReadValue(backend, address).ContentEquals(RawRegistryValue.DWord(2)));
}

async Task BinaryRepairAndUndo()
{
    RegistryAddress address = Address(RegistryViewId.Registry64, "Binary");
    var original = new RawRegistryValue(RegistryValueType.Binary, [0x00, 0xFF, 0x10, 0x80]);
    var expected = new RawRegistryValue(RegistryValueType.Binary, [0x10, 0x20, 0x30]);
    hive.Seed(address, original);
    WindowsRegistryBackend backend = hive.CreateBackend();
    using var journal = new FileRegistryRepairJournal(Path.Combine(root, "journal-binary"), JournalAuthenticationKey());
    var engine = new RegistryRepairEngine(backend, journal);
    RegistryRepairResult repair = await engine.RepairAsync(
        Single(engine.Scan([Rule(address, expected)], Windows())));
    True(repair.Success);
    True(ReadValue(backend, address).ContentEquals(expected));
    True((await engine.UndoAsync(repair.Transaction.Id)).Success);
    True(ReadValue(backend, address).ContentEquals(original));
}

async Task IsolatedViews()
{
    RegistryAddress address32 = Address(RegistryViewId.Registry32, "View");
    RegistryAddress address64 = Address(RegistryViewId.Registry64, "View");
    hive.Seed(address32, RawRegistryValue.DWord(32));
    hive.Seed(address64, RawRegistryValue.DWord(64));
    WindowsRegistryBackend backend = hive.CreateBackend();
    True(ReadValue(backend, address32).ContentEquals(RawRegistryValue.DWord(32)));
    True(ReadValue(backend, address64).ContentEquals(RawRegistryValue.DWord(64)));
    await Task.CompletedTask;
}

async Task MissingKeyCannotBeRepaired()
{
    RegistryAddress address = new(
        RegistryHiveId.LocalMachine,
        RegistryViewId.Registry64,
        @"Missing\Key",
        "Value");
    WindowsRegistryBackend backend = hive.CreateBackend();
    var engine = new RegistryRepairEngine(
        backend,
        new FileRegistryRepairJournal(Path.Combine(root, "journal-missing"), JournalAuthenticationKey()));
    RegistryFinding finding = Single(engine.Scan([Rule(address, RawRegistryValue.DWord(1))], Windows()));
    Equal(RegistryFindingState.Missing, finding.State);
    False(finding.CanRepair);
    await Task.CompletedTask;
}

async Task SubKeysAreEnumerated()
{
    var rootAddress = new RegistryAddress(
        RegistryHiveId.LocalMachine,
        RegistryViewId.Registry64,
        @"SOFTWARE\TweaklyEnumeration",
        string.Empty);
    hive.CreateKey(rootAddress with { KeyPath = rootAddress.KeyPath + @"\ChildA" });
    hive.CreateKey(rootAddress with { KeyPath = rootAddress.KeyPath + @"\ChildB" });
    RegistrySubKeyReadResult result = hive.CreateBackend().EnumerateSubKeyNames(rootAddress);
    if (!result.Success)
        throw new InvalidOperationException($"{result.ErrorCode}: {result.ErrorMessage}");
    True(result.KeyExists);
    Equal(2, result.SubKeyNames.Count);
    True(result.SubKeyNames.Contains("ChildA"));
    True(result.SubKeyNames.Contains("ChildB"));
    await Task.CompletedTask;
}

async Task ContextInspectionUsesIsolatedHive()
{
    hive.Seed(new RegistryAddress(
        RegistryHiveId.LocalMachine,
        RegistryViewId.Registry64,
        @"SYSTEM\Select",
        "Current"), RawRegistryValue.DWord(1));
    hive.Seed(new RegistryAddress(
        RegistryHiveId.LocalMachine,
        RegistryViewId.Registry64,
        @"SYSTEM\ControlSet001\Services\BrokenService",
        "Start"), RawRegistryValue.DWord(7));
    hive.Seed(new RegistryAddress(
        RegistryHiveId.LocalMachine,
        RegistryViewId.Registry64,
        @"SOFTWARE\Classes\.tweakly-test",
        string.Empty), RawRegistryValue.String("Tweakly.Missing.1"));

    IReadOnlyList<RegistryInspectionFinding> findings =
        new RegistryContextInspector(hive.CreateBackend())
            .Inspect(new WindowsIdentity(26100, "Professional", true));
    True(findings.Any(item => item.Code == "SERVICE_START_MALFORMED"));
    True(findings.Any(item => item.Code == "FILE_ASSOCIATION_PROGID_MISSING"));
    True(findings.All(item => !item.AutomaticCorrectionAvailable));
    await Task.CompletedTask;
}

async Task OfflineCorpusReadsCopies()
{
    string corpusRoot = Path.Combine(root, "offline-corpus");
    Directory.CreateDirectory(corpusRoot);
    hive.Seed(new RegistryAddress(
        RegistryHiveId.LocalMachine,
        RegistryViewId.Registry64,
        @"TweaklyLab",
        "OfflineDword"), RawRegistryValue.DWord(2));
    RegistryAddress currentVersion = new(
        RegistryHiveId.LocalMachine,
        RegistryViewId.Registry64,
        @"Microsoft\Windows NT\CurrentVersion",
        "CurrentBuildNumber");
    hive.Seed(currentVersion, RawRegistryValue.String("26100"));
    hive.Seed(currentVersion with { ValueName = "UBR" }, RawRegistryValue.DWord(4652));
    hive.Seed(currentVersion with { ValueName = "EditionID" }, RawRegistryValue.String("Professional"));
    hive.Seed(currentVersion with { ValueName = "ProductName" }, RawRegistryValue.String("Windows 11 Pro"));
    hive.Seed(currentVersion with { ValueName = "DisplayVersion" }, RawRegistryValue.String("24H2"));
    hive.Dispose();

    string source = Path.Combine(root, "registry64.hiv");
    string software = Copy(source, Path.Combine(corpusRoot, "SOFTWARE"));
    string system = Copy(source, Path.Combine(corpusRoot, "SYSTEM"));
    string defaultHive = Copy(source, Path.Combine(corpusRoot, "DEFAULT"));
    string user = Copy(source, Path.Combine(corpusRoot, "NTUSER.DAT"));
    var files = new OfflineWindowsRegistryCorpusFiles(software, system, defaultHive, user);

    using var corpus = new OfflineWindowsRegistryCorpus(files);
    RegistryReadResult result = corpus.Read(new RegistryAddress(
        RegistryHiveId.LocalMachine,
        RegistryViewId.Registry64,
        @"SOFTWARE\TweaklyLab",
        "OfflineDword"));
    True(result.Success);
    True(result.Snapshot?.Value?.ContentEquals(RawRegistryValue.DWord(2)) == true);
    True(corpus.SourcesAreUnchanged(files));
    await Task.CompletedTask;
}

async Task OfflineCorpusRefusesUnprovenView()
{
    string corpusRoot = Path.Combine(root, "offline-corpus-view");
    Directory.CreateDirectory(corpusRoot);
    string source = Path.Combine(root, "registry64.hiv");
    var files = new OfflineWindowsRegistryCorpusFiles(
        Copy(source, Path.Combine(corpusRoot, "SOFTWARE")),
        Copy(source, Path.Combine(corpusRoot, "SYSTEM")),
        Copy(source, Path.Combine(corpusRoot, "DEFAULT")));

    using var corpus = new OfflineWindowsRegistryCorpus(files);
    RegistryReadResult result = corpus.Read(new RegistryAddress(
        RegistryHiveId.LocalMachine,
        RegistryViewId.Registry32,
        @"SOFTWARE\TweaklyLab",
        "Dword"));
    False(result.Success);
    Equal("OFFLINE_32_VIEW_UNRESOLVED", result.ErrorCode!);
    await Task.CompletedTask;
}

async Task OfflineImageIdentityIsExact()
{
    string imageRoot = Path.Combine(root, "mounted-image");
    string config = Path.Combine(imageRoot, "Windows", "System32", "config");
    string defaultUser = Path.Combine(imageRoot, "Users", "Default");
    Directory.CreateDirectory(config);
    Directory.CreateDirectory(defaultUser);
    string source = Path.Combine(root, "registry64.hiv");
    Copy(source, Path.Combine(config, "SOFTWARE"));
    Copy(source, Path.Combine(config, "SYSTEM"));
    Copy(source, Path.Combine(config, "DEFAULT"));
    Copy(source, Path.Combine(defaultUser, "NTUSER.DAT"));

    using var image = OfflineWindowsImage.Open(imageRoot);
    Equal(26100, image.Identity.Build);
    Equal(4652, image.Identity.UpdateBuildRevision);
    Equal("Professional", image.Identity.Edition);
    Equal("Windows 11 Pro", image.Identity.ProductName);
    Equal("24H2", image.Identity.DisplayVersion);
    Equal(4, image.HiveHashes.Count);
    True(image.SourceHivesAreUnchanged());
    await Task.CompletedTask;
}

async Task ActiveWindowsRootIsRejected()
{
    string activeRoot = Path.GetDirectoryName(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
    await Throws<RegistryRepairException>(() => Task.Run(() =>
    {
        using OfflineWindowsImage _ = OfflineWindowsImage.Open(activeRoot);
    }));
}

static string Copy(string source, string destination)
{
    File.Copy(source, destination, overwrite: false);
    return destination;
}

static RegistryAddress Address(RegistryViewId view, string name) =>
    new(RegistryHiveId.LocalMachine, view, @"SOFTWARE\TweaklyLab", name);

static RegistryRule Rule(RegistryAddress address, RawRegistryValue expected) =>
    new(
        "WINDOWS-HIVE-TEST-" + address.ValueName,
        "Windows hive API test",
        address,
        expected,
        22000,
        null,
        new HashSet<string>(),
        new Uri("https://learn.microsoft.com/windows/test"),
        RuleEvidenceLevel.MicrosoftDocumentedExact,
        RegistryCorrectionPolicy.SetDocumentedValue,
        "Windows application hive integration test.");

static WindowsIdentity Windows() => new(26100, "Professional", true);

static RawRegistryValue ReadValue(IRegistryBackend backend, RegistryAddress address)
{
    RegistryReadResult result = backend.Read(address);
    if (!result.Success || result.Snapshot?.Value is null)
        throw new InvalidOperationException(result.ErrorMessage ?? "Value unavailable.");
    return result.Snapshot.Value;
}

static T Single<T>(IReadOnlyList<T> values)
{
    Equal(1, values.Count);
    return values[0];
}

static async Task<int> Run(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine("PASS " + name);
        return 0;
    }
    catch (Exception error)
    {
        Console.WriteLine($"FAIL {name}: {error}");
        return 1;
    }
}

static void True(bool condition)
{
    if (!condition)
        throw new InvalidOperationException("Expected true.");
}

static byte[] JournalAuthenticationKey() => Enumerable.Range(33, 32).Select(static value => (byte)value).ToArray();

static void False(bool condition) => True(!condition);

static void Equal<T>(T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
}

static async Task Throws<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
