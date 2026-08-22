using System.Security.Cryptography;
using System.Text.Json;
using RegistryRepair.Core;
using RegistryRepair.Tests;

var tests = new (string Name, Func<Task> Run)[]
{
    ("healthy value", HealthyValue),
    ("missing value repair", MissingValueRepair),
    ("wrong type repair", WrongTypeRepair),
    ("wrong data repair", WrongDataRepair),
    ("32 and 64-bit views stay isolated", RegistryViewsAreIsolated),
    ("read refusal is not repairable", ReadRefusal),
    ("journal failure prevents write", JournalFailurePreventsWrite),
    ("write refusal rolls back", WriteRefusalRollsBack),
    ("verification failure rolls back", VerificationFailureRollsBack),
    ("ACL mutation rolls back", AclMutationRollsBack),
    ("abrupt termination is recovered", AbruptTerminationIsRecovered),
    ("undo restores original value", UndoRestoresOriginal),
    ("undo failure restores correction", UndoFailureRestoresCorrection),
    ("abrupt termination during undo is recovered", AbruptUndoIsRecovered),
    ("undo refuses later user change", UndoRefusesLaterChange),
    ("undo refuses later sibling change", UndoRefusesLaterSiblingChange),
    ("non-Microsoft source is read-only", NonMicrosoftSourceIsReadOnly),
    ("unsupported build is skipped", UnsupportedBuildIsSkipped),
    ("missing key is read-only", MissingKeyIsReadOnly),
    ("absent original value is removed by undo", UndoRestoresAbsence),
    ("rollback failure is explicit", RollbackFailureIsExplicit),
    ("unresolved rollback blocks later repair", UnresolvedRollbackBlocksLaterRepair),
    ("file journal persists atomically", FileJournalPersistsAtomically),
    ("corrupted journal is rejected", CorruptedJournalIsRejected),
    ("journal opened with another key is rejected", WrongJournalKeyIsRejected),
    ("truncated journal is rejected", TruncatedJournalIsRejected),
    ("same address repairs are serialized", SameAddressRepairsAreSerialized),
    ("abrupt termination during rollback is recovered", AbruptRollbackIsRecovered),
    ("complete key backup preserves sibling values", CompleteKeyBackupPreservesSiblings),
    ("unexpected sibling mutation triggers rollback", SiblingMutationTriggersRollback),
    ("signed catalog is accepted", SignedCatalogIsAccepted),
    ("tampered catalog is rejected", TamperedCatalogIsRejected),
    ("duplicate catalog rule is rejected", DuplicateCatalogRuleIsRejected),
    ("corrective rule requires explicit editions", CorrectiveRuleRequiresEditions),
    ("malformed catalog value is rejected", MalformedCatalogValueIsRejected),
    ("incomplete signed catalog is rejected", IncompleteSignedCatalogIsRejected),
    ("weak catalog key is rejected", WeakCatalogKeyIsRejected),
    ("standard registry context is silent", StandardRegistryContextIsSilent),
    ("malformed startup value is detected", MalformedStartupValueIsDetected),
    ("oversized startup command is a certain anomaly", OversizedStartupCommandIsCertain),
    ("active AppInit configuration is reviewed", ActiveAppInitIsReviewed),
    ("IFEO debugger is reviewed", IfeoDebuggerIsReviewed),
    ("custom Winlogon configuration is reviewed", CustomWinlogonIsReviewed),
    ("malformed service start is detected", MalformedServiceStartIsDetected),
    ("standard service configuration is silent", StandardServiceConfigurationIsSilent),
    ("missing file association ProgID is informational", MissingAssociationProgIdIsReviewed),
    ("valid OpenWith ProgID resolves a legacy association", OpenWithProgIdResolvesAssociation),
    ("user file association overrides machine default", UserAssociationOverridesMachine),
    ("unreadable file association ProgID is not reported missing", UnreadableAssociationIsNotMissing),
};

int failed = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.WriteLine($"FAIL {name}: {error.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static RegistryAddress Address(RegistryViewId view = RegistryViewId.Registry64) =>
    new(RegistryHiveId.LocalMachine, view, @"SOFTWARE\TweaklyLab", "DocumentedValue");

static RegistrySnapshot Existing(RawRegistryValue value, string sddl = "D:AI") =>
    new(true, true, value, sddl);

static RegistrySnapshot Missing(string sddl = "D:AI") =>
    new(true, false, null, sddl);

static RegistryRule TrustedRule(
    RegistryAddress? address = null,
    int minBuild = 22000,
    Uri? source = null) =>
    new(
        "LAB-EXACT-001",
        "Documented test value",
        address ?? Address(),
        RawRegistryValue.DWord(1),
        minBuild,
        null,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        source ?? new Uri("https://learn.microsoft.com/windows/test"),
        RuleEvidenceLevel.MicrosoftDocumentedExact,
        RegistryCorrectionPolicy.SetDocumentedValue,
        "Synthetic rule used to validate the repair transaction engine.");

static WindowsIdentity Windows() => new(26100, "Professional", true);

static RegistryFinding ScanOne(
    RegistryRepairEngine engine,
    RegistryRule rule,
    WindowsIdentity? windows = null) =>
    Single(engine.Scan([rule], windows ?? Windows()));

static async Task HealthyValue()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(Address(), Existing(RawRegistryValue.DWord(1)));
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    RegistryFinding finding = ScanOne(engine, TrustedRule());
    Equal(RegistryFindingState.Healthy, finding.State);
    False(finding.CanRepair);
    await Task.CompletedTask;
}

static async Task MissingValueRepair()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(Address(), Missing());
    var journal = new InMemoryJournal();
    var engine = new RegistryRepairEngine(backend, journal);
    RegistryRepairResult result = await engine.RepairAsync(ScanOne(engine, TrustedRule()));
    True(result.Success);
    Equal(RegistryTransactionState.Committed, result.Transaction.State);
    True(backend.Get(Address()).Value?.ContentEquals(RawRegistryValue.DWord(1)) == true);
}

static async Task WrongTypeRepair()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(Address(), Existing(RawRegistryValue.String("1")));
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    RegistryFinding finding = ScanOne(engine, TrustedRule());
    Equal(RegistryFindingState.WrongType, finding.State);
    True((await engine.RepairAsync(finding)).Success);
}

static async Task WrongDataRepair()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(Address(), Existing(RawRegistryValue.DWord(2)));
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    RegistryFinding finding = ScanOne(engine, TrustedRule());
    Equal(RegistryFindingState.WrongData, finding.State);
    True((await engine.RepairAsync(finding)).Success);
}

static async Task RegistryViewsAreIsolated()
{
    RegistryAddress address32 = Address(RegistryViewId.Registry32);
    RegistryAddress address64 = Address(RegistryViewId.Registry64);
    var backend = new InMemoryRegistryBackend();
    backend.Seed(address32, Existing(RawRegistryValue.DWord(2)));
    backend.Seed(address64, Existing(RawRegistryValue.DWord(1)));
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    True((await engine.RepairAsync(ScanOne(engine, TrustedRule(address32)))).Success);
    True(backend.Get(address64).Value?.ContentEquals(RawRegistryValue.DWord(1)) == true);
}

static async Task ReadRefusal()
{
    var backend = new InMemoryRegistryBackend { DenyReads = true };
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    RegistryFinding finding = ScanOne(engine, TrustedRule());
    Equal(RegistryFindingState.Unreadable, finding.State);
    False(finding.CanRepair);
    await Task.CompletedTask;
}

static async Task JournalFailurePreventsWrite()
{
    var backend = new InMemoryRegistryBackend();
    RegistrySnapshot original = Existing(RawRegistryValue.DWord(2));
    backend.Seed(Address(), original);
    var journal = new InMemoryJournal { FailNextSave = true };
    var engine = new RegistryRepairEngine(backend, journal);
    await Throws<IOException>(() => engine.RepairAsync(ScanOne(engine, TrustedRule())));
    True(original.ContentEquals(backend.Get(Address())));
}

static async Task WriteRefusalRollsBack()
{
    var backend = new InMemoryRegistryBackend { FailNextWrite = true };
    RegistrySnapshot original = Existing(RawRegistryValue.DWord(2));
    backend.Seed(Address(), original);
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    RegistryRepairResult result = await engine.RepairAsync(ScanOne(engine, TrustedRule()));
    False(result.Success);
    True(result.RollbackSucceeded);
    True(original.ContentEquals(backend.Get(Address())));
}

static async Task VerificationFailureRollsBack()
{
    var backend = new InMemoryRegistryBackend { CorruptNextWrite = true };
    RegistrySnapshot original = Existing(RawRegistryValue.DWord(2));
    backend.Seed(Address(), original);
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    RegistryRepairResult result = await engine.RepairAsync(ScanOne(engine, TrustedRule()));
    False(result.Success);
    True(result.RollbackSucceeded);
    True(original.ContentEquals(backend.Get(Address())));
}

static async Task AclMutationRollsBack()
{
    var backend = new InMemoryRegistryBackend { ChangeAclOnNextWrite = true };
    RegistrySnapshot original = Existing(RawRegistryValue.DWord(2), "D:ORIGINAL");
    backend.Seed(Address(), original);
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    RegistryRepairResult result = await engine.RepairAsync(ScanOne(engine, TrustedRule()));
    False(result.Success);
    True(result.RollbackSucceeded);
    True(original.ContentEquals(backend.Get(Address())));
}

static async Task AbruptTerminationIsRecovered()
{
    var backend = new InMemoryRegistryBackend();
    RegistrySnapshot original = Existing(RawRegistryValue.DWord(2));
    backend.Seed(Address(), original);
    var journal = new InMemoryJournal();
    var crashingEngine = new RegistryRepairEngine(backend, journal, new TerminateAfterWrite());
    await Throws<SimulatedProcessTerminationException>(
        () => crashingEngine.RepairAsync(ScanOne(crashingEngine, TrustedRule())));
    True(backend.Get(Address()).Value?.ContentEquals(RawRegistryValue.DWord(1)) == true);

    var recoveryEngine = new RegistryRepairEngine(backend, journal);
    IReadOnlyList<RegistryRepairTransaction> recovered =
        await recoveryEngine.RecoverIncompleteAsync();
    Equal(1, recovered.Count);
    Equal(RegistryTransactionState.Recovered, recovered[0].State);
    True(original.ContentEquals(backend.Get(Address())));
}

static async Task UndoRestoresOriginal()
{
    var backend = new InMemoryRegistryBackend();
    RegistrySnapshot original = Existing(RawRegistryValue.DWord(2));
    backend.Seed(Address(), original);
    var journal = new InMemoryJournal();
    var engine = new RegistryRepairEngine(backend, journal);
    RegistryRepairResult repair = await engine.RepairAsync(ScanOne(engine, TrustedRule()));
    RegistryUndoResult undo = await engine.UndoAsync(repair.Transaction.Id);
    True(undo.Success);
    True(original.ContentEquals(backend.Get(Address())));
}

static async Task UndoFailureRestoresCorrection()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(Address(), Existing(RawRegistryValue.DWord(2)));
    var journal = new InMemoryJournal();
    var engine = new RegistryRepairEngine(backend, journal);
    RegistryRepairResult repair = await engine.RepairAsync(ScanOne(engine, TrustedRule()));

    backend.FailNextRestore = true;
    RegistryUndoResult failedUndo = await engine.UndoAsync(repair.Transaction.Id);
    False(failedUndo.Success);
    Equal(RegistryTransactionState.Committed, failedUndo.Transaction.State);
    True(backend.Get(Address()).Value?.ContentEquals(RawRegistryValue.DWord(1)) == true);

    RegistryUndoResult retry = await engine.UndoAsync(repair.Transaction.Id);
    True(retry.Success);
    True(backend.Get(Address()).Value?.ContentEquals(RawRegistryValue.DWord(2)) == true);
}

static async Task AbruptUndoIsRecovered()
{
    var backend = new InMemoryRegistryBackend();
    RegistrySnapshot original = Existing(RawRegistryValue.DWord(2));
    backend.Seed(Address(), original);
    var journal = new InMemoryJournal();
    var engine = new RegistryRepairEngine(backend, journal);
    RegistryRepairResult repair = await engine.RepairAsync(ScanOne(engine, TrustedRule()));

    backend.TerminateNextRestore = true;
    await Throws<SimulatedProcessTerminationException>(
        () => engine.UndoAsync(repair.Transaction.Id));
    Equal(1, (await journal.GetIncompleteAsync(CancellationToken.None)).Count);

    IReadOnlyList<RegistryRepairTransaction> recovered =
        await engine.RecoverIncompleteAsync();
    Equal(1, recovered.Count);
    Equal(RegistryTransactionState.Undone, recovered[0].State);
    True(original.ContentEquals(backend.Get(Address())));
}

static async Task UndoRefusesLaterChange()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(Address(), Existing(RawRegistryValue.DWord(2)));
    var journal = new InMemoryJournal();
    var engine = new RegistryRepairEngine(backend, journal);
    RegistryRepairResult repair = await engine.RepairAsync(ScanOne(engine, TrustedRule()));
    backend.SetValue(Address(), RawRegistryValue.DWord(3));
    await Throws<RegistryRepairException>(() => engine.UndoAsync(repair.Transaction.Id));
    True(backend.Get(Address()).Value?.ContentEquals(RawRegistryValue.DWord(3)) == true);
}

static async Task UndoRefusesLaterSiblingChange()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(Address(), Existing(RawRegistryValue.DWord(2)));
    RegistryAddress sibling = Address() with { ValueName = "Sibling" };
    backend.Seed(sibling, Existing(RawRegistryValue.String("before")));
    var journal = new InMemoryJournal();
    var engine = new RegistryRepairEngine(backend, journal);
    RegistryRepairResult repair = await engine.RepairAsync(ScanOne(engine, TrustedRule()));

    backend.SetValue(sibling, RawRegistryValue.String("after"));
    await Throws<RegistryRepairException>(() => engine.UndoAsync(repair.Transaction.Id));
    True(backend.Get(Address()).Value?.ContentEquals(RawRegistryValue.DWord(1)) == true);
    True(backend.Get(sibling).Value?.ContentEquals(RawRegistryValue.String("after")) == true);
}

static async Task NonMicrosoftSourceIsReadOnly()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(Address(), Existing(RawRegistryValue.DWord(2)));
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    RegistryRule rule = TrustedRule(source: new Uri("https://example.com/value"));
    RegistryFinding finding = ScanOne(engine, rule);
    False(finding.CanRepair);
    await Task.CompletedTask;
}

static async Task UnsupportedBuildIsSkipped()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(Address(), Existing(RawRegistryValue.DWord(2)));
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    RegistryFinding finding = ScanOne(engine, TrustedRule(minBuild: 30000));
    Equal(RegistryFindingState.NotApplicable, finding.State);
    False(finding.CanRepair);
    await Task.CompletedTask;
}

static async Task MissingKeyIsReadOnly()
{
    var backend = new InMemoryRegistryBackend();
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    RegistryFinding finding = ScanOne(engine, TrustedRule());
    Equal(RegistryFindingState.Missing, finding.State);
    False(finding.CanRepair);
    await Task.CompletedTask;
}

static async Task UndoRestoresAbsence()
{
    var backend = new InMemoryRegistryBackend();
    RegistrySnapshot original = Missing();
    backend.Seed(Address(), original);
    var journal = new InMemoryJournal();
    var engine = new RegistryRepairEngine(backend, journal);
    RegistryRepairResult repair = await engine.RepairAsync(ScanOne(engine, TrustedRule()));
    RegistryUndoResult undo = await engine.UndoAsync(repair.Transaction.Id);
    True(undo.Success);
    True(original.ContentEquals(backend.Get(Address())));
}

static async Task RollbackFailureIsExplicit()
{
    var backend = new InMemoryRegistryBackend
    {
        CorruptNextWrite = true,
        FailNextRestore = true,
    };
    backend.Seed(Address(), Existing(RawRegistryValue.DWord(2)));
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    RegistryRepairResult result = await engine.RepairAsync(ScanOne(engine, TrustedRule()));
    False(result.Success);
    False(result.RollbackSucceeded);
    Equal(RegistryTransactionState.RollbackFailed, result.Transaction.State);
}

static async Task UnresolvedRollbackBlocksLaterRepair()
{
    var backend = new InMemoryRegistryBackend
    {
        CorruptNextWrite = true,
        FailNextRestore = true,
    };
    backend.Seed(Address(), Existing(RawRegistryValue.DWord(2)));
    var journal = new InMemoryJournal();
    var engine = new RegistryRepairEngine(backend, journal);

    RegistryRepairResult failed = await engine.RepairAsync(ScanOne(engine, TrustedRule()));
    Equal(RegistryTransactionState.RollbackFailed, failed.Transaction.State);

    await Throws<RegistryRepairException>(
        () => engine.RepairAsync(ScanOne(engine, TrustedRule())));
}

static async Task FileJournalPersistsAtomically()
{
    string directory = Path.Combine(Path.GetTempPath(), "TweaklyRegistryRepairLab", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var transaction = new RegistryRepairTransaction(
            Guid.NewGuid(),
            "LAB-JOURNAL-001",
            Address(),
            Existing(RawRegistryValue.DWord(2)),
            RawRegistryValue.DWord(1),
            RegistryTransactionState.Prepared,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "https://learn.microsoft.com/windows/test",
            "Durability test.",
            null);

        using var journal = new FileRegistryRepairJournal(directory, JournalAuthenticationKey());
        await journal.SaveAsync(transaction, CancellationToken.None);
        RegistryRepairTransaction? loaded = await journal.GetAsync(
            transaction.Id,
            CancellationToken.None);
        True(loaded is not null);
        Equal(RegistryTransactionState.Prepared, loaded!.State);
        Equal(1, (await journal.GetIncompleteAsync(CancellationToken.None)).Count);

        await journal.SaveAsync(
            transaction.WithState(RegistryTransactionState.Committed),
            CancellationToken.None);
        Equal(0, (await journal.GetIncompleteAsync(CancellationToken.None)).Count);

        await journal.SaveAsync(
            transaction.WithState(RegistryTransactionState.UndoPrepared),
            CancellationToken.None);
        Equal(1, (await journal.GetIncompleteAsync(CancellationToken.None)).Count);

        await journal.SaveAsync(
            transaction.WithState(RegistryTransactionState.Undone),
            CancellationToken.None);
        Equal(0, (await journal.GetIncompleteAsync(CancellationToken.None)).Count);
        Equal(0, (await journal.GetBlockingAsync(CancellationToken.None)).Count);

        await journal.SaveAsync(
            transaction.WithState(RegistryTransactionState.RollbackFailed),
            CancellationToken.None);
        Equal(1, (await journal.GetBlockingAsync(CancellationToken.None)).Count);
        Equal(0, Directory.EnumerateFiles(directory, "*.tmp").Count());
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task CorruptedJournalIsRejected()
{
    string directory = TemporaryDirectory();
    try
    {
        RegistryRepairTransaction transaction = TestTransaction();
        using var journal = new FileRegistryRepairJournal(directory, JournalAuthenticationKey());
        await journal.SaveAsync(transaction, CancellationToken.None);
        string path = Path.Combine(directory, transaction.Id.ToString("N") + ".json");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes);
        await Throws<RegistryRepairException>(
            () => journal.GetAsync(transaction.Id, CancellationToken.None));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task TruncatedJournalIsRejected()
{
    string directory = TemporaryDirectory();
    try
    {
        RegistryRepairTransaction transaction = TestTransaction();
        using var journal = new FileRegistryRepairJournal(directory, JournalAuthenticationKey());
        await journal.SaveAsync(transaction, CancellationToken.None);
        string path = Path.Combine(directory, transaction.Id.ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":1,");
        await Throws<RegistryRepairException>(
            () => journal.GetAsync(transaction.Id, CancellationToken.None));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task WrongJournalKeyIsRejected()
{
    string directory = TemporaryDirectory();
    try
    {
        RegistryRepairTransaction transaction = TestTransaction();
        using (var writer = new FileRegistryRepairJournal(directory, JournalAuthenticationKey()))
            await writer.SaveAsync(transaction, CancellationToken.None);

        byte[] otherKey = Enumerable.Range(101, 32).Select(static value => (byte)value).ToArray();
        using var reader = new FileRegistryRepairJournal(directory, otherKey);
        await Throws<RegistryRepairException>(
            () => reader.GetAsync(transaction.Id, CancellationToken.None));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task SameAddressRepairsAreSerialized()
{
    var backend = new InMemoryRegistryBackend { WriteDelayMilliseconds = 150 };
    backend.Seed(Address(), Existing(RawRegistryValue.DWord(2)));
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());
    RegistryFinding first = ScanOne(engine, TrustedRule());
    RegistryFinding second = ScanOne(engine, TrustedRule());

    Task<RegistryRepairResult> firstTask = Task.Run(() => engine.RepairAsync(first));
    await Task.Delay(20);
    Task<RegistryRepairResult> secondTask = Task.Run(() => engine.RepairAsync(second));

    RegistryRepairResult result = await firstTask;
    True(result.Success);
    await Throws<RegistryRepairException>(async () => await secondTask);
    Equal(1, backend.MaximumConcurrentWrites);
}

static async Task AbruptRollbackIsRecovered()
{
    var backend = new InMemoryRegistryBackend
    {
        CorruptNextWrite = true,
        TerminateNextRestore = true,
    };
    RegistrySnapshot original = Existing(RawRegistryValue.DWord(2));
    backend.Seed(Address(), original);
    var journal = new InMemoryJournal();
    var engine = new RegistryRepairEngine(backend, journal);

    await Throws<SimulatedProcessTerminationException>(
        () => engine.RepairAsync(ScanOne(engine, TrustedRule())));
    Equal(1, (await journal.GetIncompleteAsync(CancellationToken.None)).Count);

    IReadOnlyList<RegistryRepairTransaction> recovered =
        await engine.RecoverIncompleteAsync();
    Equal(1, recovered.Count);
    Equal(RegistryTransactionState.Recovered, recovered[0].State);
    True(original.ContentEquals(backend.Get(Address())));
}

static async Task CompleteKeyBackupPreservesSiblings()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(Address(), Existing(RawRegistryValue.DWord(2)));
    RegistryAddress sibling = Address() with { ValueName = "Sibling" };
    backend.Seed(sibling, Existing(RawRegistryValue.String("untouched")));
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());

    RegistryRepairResult result = await engine.RepairAsync(ScanOne(engine, TrustedRule()));
    True(result.Success);
    Equal(2, result.Transaction.KeyBefore!.Values.Count);
    True(backend.Get(sibling).Value?.ContentEquals(RawRegistryValue.String("untouched")) == true);
}

static async Task SiblingMutationTriggersRollback()
{
    var backend = new InMemoryRegistryBackend { MutateSiblingOnNextWrite = true };
    RegistrySnapshot original = Existing(RawRegistryValue.DWord(2));
    backend.Seed(Address(), original);
    var engine = new RegistryRepairEngine(backend, new InMemoryJournal());

    RegistryRepairResult result = await engine.RepairAsync(ScanOne(engine, TrustedRule()));
    False(result.Success);
    False(result.RollbackSucceeded);
    Equal(RegistryTransactionState.RollbackFailed, result.Transaction.State);
    True(original.ContentEquals(backend.Get(Address())));
}

static async Task SignedCatalogIsAccepted()
{
    using RSA rsa = RSA.Create(2048);
    RegistryRuleCatalog catalog = Catalog([CatalogRule()]);
    byte[] envelope = SignCatalog(catalog, rsa, "test-key-1");
    RegistryRuleCatalog loaded = RegistryRuleCatalogLoader.LoadAndVerify(
        envelope,
        rsa.ExportSubjectPublicKeyInfoPem(),
        "test-key-1");
    Equal("1.0.0", loaded.CatalogVersion);
    Equal(1, loaded.Rules.Count);
    await Task.CompletedTask;
}

static async Task TamperedCatalogIsRejected()
{
    using RSA rsa = RSA.Create(2048);
    byte[] envelope = SignCatalog(Catalog([CatalogRule()]), rsa, "test-key-1");
    SignedRegistryRuleCatalog parsed = JsonSerializer.Deserialize<SignedRegistryRuleCatalog>(envelope)!;
    byte[] payload = Convert.FromBase64String(parsed.Payload);
    payload[payload.Length / 2] ^= 0x01;
    byte[] tampered = JsonSerializer.SerializeToUtf8Bytes(
        parsed with { Payload = Convert.ToBase64String(payload) });
    await Throws<RegistryRuleCatalogException>(() => Task.Run(() =>
        RegistryRuleCatalogLoader.LoadAndVerify(
            tampered,
            rsa.ExportSubjectPublicKeyInfoPem(),
            "test-key-1")));
}

static async Task DuplicateCatalogRuleIsRejected()
{
    RegistryRule rule = CatalogRule();
    await Throws<RegistryRuleCatalogException>(() => Task.Run(() =>
        RegistryRuleCatalogLoader.Validate(Catalog([rule, rule]))));
}

static async Task CorrectiveRuleRequiresEditions()
{
    RegistryRule rule = CatalogRule() with { SupportedEditions = new HashSet<string>() };
    await Throws<RegistryRuleCatalogException>(() => Task.Run(() =>
        RegistryRuleCatalogLoader.Validate(Catalog([rule]))));
}

static async Task MalformedCatalogValueIsRejected()
{
    RegistryRule rule = CatalogRule() with
    {
        ExpectedValue = new RawRegistryValue(RegistryValueType.DWord, [0x01]),
    };
    await Throws<RegistryRuleCatalogException>(() => Task.Run(() =>
        RegistryRuleCatalogLoader.Validate(Catalog([rule]))));
}

static async Task IncompleteSignedCatalogIsRejected()
{
    using RSA rsa = RSA.Create(2048);
    byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new
    {
        Algorithm = RegistryRuleCatalogLoader.SupportedAlgorithm,
        KeyId = "test-key-1",
        Payload = (string?)null,
        Signature = (string?)null,
    });
    await Throws<RegistryRuleCatalogException>(() => Task.Run(() =>
        RegistryRuleCatalogLoader.LoadAndVerify(
            envelope,
            rsa.ExportSubjectPublicKeyInfoPem(),
            "test-key-1")));
}

static async Task WeakCatalogKeyIsRejected()
{
    using RSA rsa = RSA.Create(1024);
    byte[] envelope = SignCatalog(Catalog([CatalogRule()]), rsa, "weak-key");
    await Throws<RegistryRuleCatalogException>(() => Task.Run(() =>
        RegistryRuleCatalogLoader.LoadAndVerify(
            envelope,
            rsa.ExportSubjectPublicKeyInfoPem(),
            "weak-key")));
}

static async Task StandardRegistryContextIsSilent()
{
    var backend = new InMemoryRegistryBackend();
    SeedWinlogon(backend, "explorer.exe", @"C:\Windows\System32\userinit.exe,");
    var inspector = new RegistryContextInspector(backend);
    Equal(0, inspector.Inspect(Windows()).Count);
    await Task.CompletedTask;
}

static async Task MalformedStartupValueIsDetected()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(new RegistryAddress(
        RegistryHiveId.CurrentUser,
        RegistryViewId.Registry64,
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        "Broken"), Existing(new RawRegistryValue(RegistryValueType.Binary, [0x01])));
    RegistryInspectionFinding finding = Single(
        new RegistryContextInspector(backend).Inspect(Windows()));
    Equal("STARTUP_VALUE_MALFORMED", finding.Code);
    Equal(RegistryInspectionAssessment.CertainAnomaly, finding.Assessment);
    False(finding.AutomaticCorrectionAvailable);
    await Task.CompletedTask;
}

static async Task OversizedStartupCommandIsCertain()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(new RegistryAddress(
        RegistryHiveId.CurrentUser,
        RegistryViewId.Registry64,
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        "TooLong"), Existing(RawRegistryValue.String(new string('a', 261))));

    RegistryInspectionFinding finding = new RegistryContextInspector(backend)
        .Inspect(Windows())
        .Single(item => item.Code == "STARTUP_COMMAND_TOO_LONG");
    Equal(RegistryInspectionAssessment.CertainAnomaly, finding.Assessment);
    False(finding.AutomaticCorrectionAvailable);
    await Task.CompletedTask;
}

static async Task ActiveAppInitIsReviewed()
{
    var backend = new InMemoryRegistryBackend();
    RegistryAddress key = new(
        RegistryHiveId.LocalMachine,
        RegistryViewId.Registry64,
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows",
        "LoadAppInit_DLLs");
    backend.Seed(key, Existing(RawRegistryValue.DWord(1)));
    backend.Seed(key with { ValueName = "AppInit_DLLs" }, Existing(RawRegistryValue.String("sample.dll")));
    IReadOnlyList<RegistryInspectionFinding> findings =
        new RegistryContextInspector(backend).Inspect(Windows());
    True(findings.Any(finding => finding.Code == "APPINIT_DLLS_ACTIVE"));
    True(findings.All(finding => !finding.AutomaticCorrectionAvailable));
    await Task.CompletedTask;
}

static async Task IfeoDebuggerIsReviewed()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(new RegistryAddress(
        RegistryHiveId.LocalMachine,
        RegistryViewId.Registry64,
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\notepad.exe",
        "Debugger"), Existing(RawRegistryValue.String(@"C:\Tools\debugger.exe")));
    IReadOnlyList<RegistryInspectionFinding> findings =
        new RegistryContextInspector(backend).Inspect(Windows());
    RegistryInspectionFinding finding = findings.Single(item =>
        item.Code == "IFEO_DEBUGGER_CONFIGURED");
    Equal(RegistryInspectionStatus.Review, finding.Status);
    Equal(RegistryInspectionAssessment.Unusual, finding.Assessment);
    False(finding.AutomaticCorrectionAvailable);
    await Task.CompletedTask;
}

static async Task CustomWinlogonIsReviewed()
{
    var backend = new InMemoryRegistryBackend();
    SeedWinlogon(
        backend,
        @"C:\Kiosk\shell.exe",
        @"C:\Windows\System32\userinit.exe,C:\Tools\helper.exe");
    IReadOnlyList<RegistryInspectionFinding> findings =
        new RegistryContextInspector(backend).Inspect(Windows());
    True(findings.Any(finding => finding.Code == "WINLOGON_SHELL_REVIEW"));
    True(findings.Any(finding => finding.Code == "WINLOGON_USERINIT_REVIEW"));
    True(findings.All(finding => !finding.AutomaticCorrectionAvailable));
    await Task.CompletedTask;
}

static async Task MalformedServiceStartIsDetected()
{
    var backend = new InMemoryRegistryBackend();
    SeedCurrentControlSet(backend);
    RegistryAddress service = ServiceAddress("BrokenService", "Start");
    backend.Seed(service, Existing(RawRegistryValue.DWord(7)));

    RegistryInspectionFinding finding = new RegistryContextInspector(backend)
        .Inspect(Windows())
        .Single(item => item.Code == "SERVICE_START_MALFORMED");
    Equal(RegistryInspectionStatus.Malformed, finding.Status);
    Equal(RegistryInspectionAssessment.CertainAnomaly, finding.Assessment);
    False(finding.AutomaticCorrectionAvailable);
    await Task.CompletedTask;
}

static async Task StandardServiceConfigurationIsSilent()
{
    var backend = new InMemoryRegistryBackend();
    SeedCurrentControlSet(backend);
    RegistryAddress service = ServiceAddress("SampleService", "Start");
    backend.Seed(service, Existing(RawRegistryValue.DWord(2)));
    backend.Seed(service with { ValueName = "Type" }, Existing(RawRegistryValue.DWord(0x10)));
    backend.Seed(
        service with { ValueName = "ImagePath" },
        Existing(RawRegistryValue.String(@"C:\Windows\System32\sample.exe")));

    Equal(0, new RegistryContextInspector(backend).Inspect(Windows()).Count);
    await Task.CompletedTask;
}

static async Task MissingAssociationProgIdIsReviewed()
{
    var backend = new InMemoryRegistryBackend();
    RegistryAddress extension = AssociationAddress(
        RegistryHiveId.LocalMachine,
        ".tweakly-test");
    backend.Seed(extension, Existing(RawRegistryValue.String("Tweakly.Missing.1")));

    RegistryInspectionFinding finding = new RegistryContextInspector(backend)
        .Inspect(Windows())
        .Single(item => item.Code == "FILE_ASSOCIATION_PROGID_MISSING");
    Equal(RegistryInspectionStatus.Informational, finding.Status);
    Equal(RegistryInspectionAssessment.Information, finding.Assessment);
    False(finding.AutomaticCorrectionAvailable);
    await Task.CompletedTask;
}

static async Task OpenWithProgIdResolvesAssociation()
{
    var backend = new InMemoryRegistryBackend();
    RegistryAddress extension = AssociationAddress(RegistryHiveId.LocalMachine, ".tweakly-test");
    backend.Seed(extension, Existing(RawRegistryValue.String("Tweakly.Legacy.1")));
    backend.Seed(
        AssociationAddress(RegistryHiveId.LocalMachine, @".tweakly-test\OpenWithProgids")
            with { ValueName = "Tweakly.AppX.1" },
        Existing(new RawRegistryValue(RegistryValueType.None, [])));
    backend.Seed(
        AssociationAddress(RegistryHiveId.LocalMachine, "Tweakly.AppX.1"),
        Existing(RawRegistryValue.String("Tweakly application")));

    Equal(0, new RegistryContextInspector(backend).Inspect(Windows()).Count);
    await Task.CompletedTask;
}

static async Task UserAssociationOverridesMachine()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(
        AssociationAddress(RegistryHiveId.LocalMachine, ".tweakly-test"),
        Existing(RawRegistryValue.String("Tweakly.Missing.1")));
    backend.Seed(
        AssociationAddress(RegistryHiveId.CurrentUser, ".tweakly-test"),
        Existing(RawRegistryValue.String("Tweakly.User.1")));
    backend.Seed(
        AssociationAddress(RegistryHiveId.CurrentUser, "Tweakly.User.1"),
        Existing(RawRegistryValue.String("Tweakly test file")));

    Equal(0, new RegistryContextInspector(backend).Inspect(Windows()).Count);
    await Task.CompletedTask;
}

static async Task UnreadableAssociationIsNotMissing()
{
    var backend = new InMemoryRegistryBackend();
    backend.Seed(
        AssociationAddress(RegistryHiveId.LocalMachine, ".tweakly-test"),
        Existing(RawRegistryValue.String("Tweakly.Protected.1")));
    backend.DeniedInspectionAddresses.Add(
        AssociationAddress(RegistryHiveId.LocalMachine, "Tweakly.Protected.1"));

    IReadOnlyList<RegistryInspectionFinding> findings =
        new RegistryContextInspector(backend).Inspect(Windows());
    True(findings.Any(item => item.Code == "FILE_ASSOCIATION_PROGID_UNREADABLE"));
    False(findings.Any(item => item.Code == "FILE_ASSOCIATION_PROGID_MISSING"));
    await Task.CompletedTask;
}

static void SeedCurrentControlSet(InMemoryRegistryBackend backend) =>
    backend.Seed(
        new RegistryAddress(
            RegistryHiveId.LocalMachine,
            RegistryViewId.Registry64,
            @"SYSTEM\Select",
            "Current"),
        Existing(RawRegistryValue.DWord(1)));

static RegistryAddress ServiceAddress(string serviceName, string valueName) => new(
    RegistryHiveId.LocalMachine,
    RegistryViewId.Registry64,
    $@"SYSTEM\ControlSet001\Services\{serviceName}",
    valueName);

static RegistryAddress AssociationAddress(RegistryHiveId hive, string keyName) => new(
    hive,
    RegistryViewId.Registry64,
    $@"SOFTWARE\Classes\{keyName}",
    string.Empty);

static void SeedWinlogon(
    InMemoryRegistryBackend backend,
    string shell,
    string userinit)
{
    RegistryAddress address = new(
        RegistryHiveId.LocalMachine,
        RegistryViewId.Registry64,
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
        "Shell");
    backend.Seed(address, Existing(RawRegistryValue.String(shell)));
    backend.Seed(address with { ValueName = "Userinit" }, Existing(RawRegistryValue.String(userinit)));
}

static RegistryRuleCatalog Catalog(IReadOnlyList<RegistryRule> rules) => new(
    1,
    "1.0.0",
    DateTimeOffset.UtcNow,
    "Windows11",
    rules);

static RegistryRule CatalogRule() => TrustedRule() with
{
    Id = "WIN11-LAB-001",
    SupportedEditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Professional",
    },
};

static byte[] SignCatalog(RegistryRuleCatalog catalog, RSA rsa, string keyId)
{
    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(catalog);
    byte[] signature = rsa.SignData(
        payload,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pss);
    return JsonSerializer.SerializeToUtf8Bytes(new SignedRegistryRuleCatalog(
        RegistryRuleCatalogLoader.SupportedAlgorithm,
        keyId,
        Convert.ToBase64String(payload),
        Convert.ToBase64String(signature)));
}

static string TemporaryDirectory()
{
    string directory = Path.Combine(
        Path.GetTempPath(),
        "TweaklyRegistryRepairLab",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return directory;
}

static RegistryRepairTransaction TestTransaction() => new(
    Guid.NewGuid(),
    "LAB-JOURNAL-001",
    Address(),
    Existing(RawRegistryValue.DWord(2)),
    RawRegistryValue.DWord(1),
    RegistryTransactionState.Prepared,
    DateTimeOffset.UtcNow,
    DateTimeOffset.UtcNow,
    "https://learn.microsoft.com/windows/test",
    "Durability test.",
    null);

static T Single<T>(IReadOnlyList<T> values)
{
    Equal(1, values.Count);
    return values[0];
}

static void True(bool condition)
{
    if (!condition)
        throw new InvalidOperationException("Expected true.");
}

static byte[] JournalAuthenticationKey() => Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();

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
