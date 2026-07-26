using System.Collections.Concurrent;

namespace RegistryRepair.Core;

public sealed class RegistryRepairEngine
{
    private readonly IRegistryBackend _backend;
    private readonly IRegistryRepairJournal _journal;
    private readonly IRegistryRepairFaultInjector _faults;

    public RegistryRepairEngine(
        IRegistryBackend backend,
        IRegistryRepairJournal journal,
        IRegistryRepairFaultInjector? faults = null)
    {
        _backend = backend;
        _journal = journal;
        _faults = faults ?? NoRegistryRepairFaults.Instance;
    }

    public IReadOnlyList<RegistryFinding> Scan(
        IEnumerable<RegistryRule> rules,
        WindowsIdentity windows)
    {
        var findings = new List<RegistryFinding>();
        foreach (RegistryRule rule in rules)
        {
            if (!rule.AppliesTo(windows))
            {
                findings.Add(new RegistryFinding(
                    rule,
                    RegistryFindingState.NotApplicable,
                    null,
                    null,
                    false));
                continue;
            }

            RegistryReadResult read = _backend.Read(rule.Address.Normalize());
            if (!read.Success || read.Snapshot is null)
            {
                findings.Add(new RegistryFinding(
                    rule,
                    RegistryFindingState.Unreadable,
                    null,
                    read.ErrorMessage ?? read.ErrorCode,
                    false));
                continue;
            }

            RegistryFindingState state = Classify(read.Snapshot, rule.ExpectedValue);
            findings.Add(new RegistryFinding(
                rule,
                state,
                read.Snapshot,
                null,
                read.Snapshot.KeyExists &&
                state is not RegistryFindingState.Healthy &&
                rule.HasTrustedCorrectionSource));
        }

        return findings;
    }

    public async Task<RegistryRepairResult> RepairAsync(
        RegistryFinding finding,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!finding.CanRepair || !finding.Rule.HasTrustedCorrectionSource)
            throw new RegistryRepairException("This finding has no trusted automatic correction.");
        if (finding.Observed is null || finding.ObservedFingerprint is null)
            throw new RegistryRepairException("The original registry state is unavailable.");

        RegistryAddress address = finding.Rule.Address.Normalize();
        using AddressLease addressLease = await AddressLocks.AcquireAsync(
            address,
            cancellationToken).ConfigureAwait(false);
        RegistryKeySnapshot keyBefore = ReadKeyRequired(address);
        RegistrySnapshot current = keyBefore.ForValue(address.ValueName);
        if (!string.Equals(
                current.Fingerprint(),
                finding.ObservedFingerprint,
                StringComparison.Ordinal))
            throw new RegistryRepairException(
                "The registry value changed after the scan. Run the scan again before repairing it.");

        var transaction = new RegistryRepairTransaction(
            Guid.NewGuid(),
            finding.Rule.Id,
            address,
            current.DeepCopy(),
            finding.Rule.ExpectedValue.DeepCopy(),
            RegistryTransactionState.Prepared,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            finding.Rule.Source.ToString(),
            finding.Rule.Reason,
            null,
            keyBefore.DeepCopy());

        // A durable Prepared record is mandatory before the first registry write.
        await _journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);

        try
        {
            _backend.SetValue(address, transaction.AppliedValue);
            _faults.AfterRegistryWrite(transaction);

            RegistryKeySnapshot verified = ReadKeyRequired(address);
            EnsureExpectedKeyAfterWrite(transaction, verified);

            transaction = transaction.WithState(RegistryTransactionState.Committed);
            await _journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);
            return new RegistryRepairResult(true, false, transaction, "Correction verified.");
        }
        catch (SimulatedProcessTerminationException)
        {
            // Represents a process disappearing before its catch/finally blocks can execute.
            throw;
        }
        catch (Exception correctionError)
        {
            bool rollbackSucceeded = TryRestoreAndVerify(
                address,
                transaction.Before,
                transaction.KeyBefore,
                out string rollbackDetail);
            transaction = transaction.WithState(
                rollbackSucceeded
                    ? RegistryTransactionState.RolledBack
                    : RegistryTransactionState.RollbackFailed,
                $"Correction failed: {correctionError.Message} | {rollbackDetail}");
            await _journal.SaveAsync(transaction, CancellationToken.None).ConfigureAwait(false);

            return new RegistryRepairResult(
                false,
                rollbackSucceeded,
                transaction,
                rollbackSucceeded
                    ? "Correction failed; the original value was restored and verified."
                    : "Correction and rollback both failed. Manual recovery is required.");
        }
    }

    public async Task<RegistryUndoResult> UndoAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        RegistryRepairTransaction transaction =
            await _journal.GetAsync(transactionId, cancellationToken).ConfigureAwait(false)
            ?? throw new RegistryRepairException("The correction history entry does not exist.");

        if (transaction.State != RegistryTransactionState.Committed)
            throw new RegistryRepairException("Only a committed correction can be undone.");

        using AddressLease addressLease = await AddressLocks.AcquireAsync(
            transaction.Address,
            cancellationToken).ConfigureAwait(false);

        RegistrySnapshot current = ReadRequired(transaction.Address);
        EnsureExpectedValueAndSecurity(transaction, current);

        try
        {
            _backend.Restore(transaction.Address, transaction.Before);
            RegistrySnapshot restored = ReadRequired(transaction.Address);
            if (!transaction.Before.ContentEquals(restored))
                throw new RegistryRepairException("The restored state does not match the backup.");

            transaction = transaction.WithState(RegistryTransactionState.Undone);
            await _journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);
            return new RegistryUndoResult(true, transaction, "Original value restored and verified.");
        }
        catch (Exception error)
        {
            transaction = transaction.WithState(
                RegistryTransactionState.UndoFailed,
                error.Message);
            await _journal.SaveAsync(transaction, CancellationToken.None).ConfigureAwait(false);
            return new RegistryUndoResult(false, transaction, "Undo failed.");
        }
    }

    public async Task<IReadOnlyList<RegistryRepairTransaction>> RecoverIncompleteAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RegistryRepairTransaction> incomplete =
            await _journal.GetIncompleteAsync(cancellationToken).ConfigureAwait(false);
        var recovered = new List<RegistryRepairTransaction>(incomplete.Count);

        foreach (RegistryRepairTransaction pending in incomplete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using AddressLease addressLease = await AddressLocks.AcquireAsync(
                pending.Address,
                cancellationToken).ConfigureAwait(false);
            bool restored = TryRestoreAndVerify(
                pending.Address,
                pending.Before,
                null,
                out string detail);
            RegistryRepairTransaction updated = pending.WithState(
                restored ? RegistryTransactionState.Recovered : RegistryTransactionState.RollbackFailed,
                detail);
            await _journal.SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
            recovered.Add(updated);
        }

        return recovered;
    }

    private RegistrySnapshot ReadRequired(RegistryAddress address)
    {
        RegistryReadResult read = _backend.Read(address);
        if (!read.Success || read.Snapshot is null)
            throw new RegistryRepairException(
                $"Registry read failed: {read.ErrorCode ?? read.ErrorMessage ?? "unknown error"}.");
        return read.Snapshot;
    }

    private RegistryKeySnapshot ReadKeyRequired(RegistryAddress address)
    {
        RegistryKeyReadResult read = _backend.ReadKey(address);
        if (!read.Success || read.Snapshot is null)
            throw new RegistryRepairException(
                $"Registry key read failed: {read.ErrorCode ?? read.ErrorMessage ?? "unknown error"}.");
        return read.Snapshot;
    }

    private bool TryRestoreAndVerify(
        RegistryAddress address,
        RegistrySnapshot expected,
        RegistryKeySnapshot? completeKeyExpected,
        out string detail)
    {
        try
        {
            _backend.Restore(address, expected);
            bool valid = completeKeyExpected is null
                ? expected.ContentEquals(ReadRequired(address))
                : completeKeyExpected.ContentEquals(ReadKeyRequired(address));
            detail = valid
                ? "Rollback verified."
                : "Rollback completed but verification failed.";
            return valid;
        }
        catch (SimulatedProcessTerminationException)
        {
            throw;
        }
        catch (Exception error)
        {
            detail = "Rollback failed: " + error.Message;
            return false;
        }
    }

    private static RegistryFindingState Classify(
        RegistrySnapshot snapshot,
        RawRegistryValue expected)
    {
        if (!snapshot.ValueExists || snapshot.Value is null)
            return RegistryFindingState.Missing;
        if (snapshot.Value.Type != expected.Type)
            return RegistryFindingState.WrongType;
        return snapshot.Value.Data.AsSpan().SequenceEqual(expected.Data)
            ? RegistryFindingState.Healthy
            : RegistryFindingState.WrongData;
    }

    private static void EnsureExpectedValueAndSecurity(
        RegistryRepairTransaction transaction,
        RegistrySnapshot verified)
    {
        if (!verified.ValueExists ||
            verified.Value?.ContentEquals(transaction.AppliedValue) != true)
            throw new RegistryRepairException("Post-write verification failed.");

        if (!string.Equals(
                transaction.Before.SecurityDescriptorSddl,
                verified.SecurityDescriptorSddl,
                StringComparison.Ordinal))
            throw new RegistryRepairException("The registry key security descriptor changed.");
    }

    private static class AddressLocks
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
            new(StringComparer.OrdinalIgnoreCase);

        public static async Task<AddressLease> AcquireAsync(
            RegistryAddress address,
            CancellationToken cancellationToken)
        {
            string key = address.Normalize().ToString();
            SemaphoreSlim gate = Gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new AddressLease(gate);
        }
    }

    private sealed class AddressLease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public AddressLease(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private static void EnsureExpectedKeyAfterWrite(
        RegistryRepairTransaction transaction,
        RegistryKeySnapshot verified)
    {
        RegistryKeySnapshot before = transaction.KeyBefore
            ?? throw new RegistryRepairException("The complete registry key backup is unavailable.");
        RegistryKeySnapshot expected = before.WithValue(
            transaction.Address.ValueName,
            transaction.AppliedValue);
        if (!expected.ContentEquals(verified))
            throw new RegistryRepairException(
                "Post-write verification detected an unexpected change in the registry key.");
    }
}
