using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

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
        await EnsureNoBlockingTransactionAsync(address, cancellationToken).ConfigureAwait(false);
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
        await EnsureNoBlockingTransactionAsync(
            transaction.Address,
            cancellationToken).ConfigureAwait(false);

        RegistryKeySnapshot currentKey = ReadKeyRequired(transaction.Address);
        RegistrySnapshot current = currentKey.ForValue(transaction.Address.ValueName);
        EnsureExpectedValueAndSecurity(transaction, current);
        EnsureExpectedKeyBeforeUndo(transaction, currentKey);

        transaction = transaction.WithState(RegistryTransactionState.UndoPrepared);
        await _journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);

        try
        {
            _backend.Restore(transaction.Address, transaction.Before);
            bool restoredExactly = transaction.KeyBefore is not null
                ? transaction.KeyBefore.ContentEquals(ReadKeyRequired(transaction.Address))
                : transaction.Before.ContentEquals(ReadRequired(transaction.Address));
            if (!restoredExactly)
                throw new RegistryRepairException("The restored state does not match the backup.");

            transaction = transaction.WithState(RegistryTransactionState.Undone);
            await _journal.SaveAsync(transaction, CancellationToken.None).ConfigureAwait(false);
            return new RegistryUndoResult(true, transaction, "Original value restored and verified.");
        }
        catch (SimulatedProcessTerminationException)
        {
            throw;
        }
        catch (Exception error)
        {
            bool correctionRestored = TryReapplyAndVerify(transaction, out string rollbackDetail);
            transaction = transaction.WithState(
                correctionRestored
                    ? RegistryTransactionState.Committed
                    : RegistryTransactionState.UndoFailed,
                $"Undo failed: {error.Message} | {rollbackDetail}");
            await _journal.SaveAsync(transaction, CancellationToken.None).ConfigureAwait(false);
            return new RegistryUndoResult(
                false,
                transaction,
                correctionRestored
                    ? "Undo failed; the correction was restored and verified."
                    : "Undo and rollback both failed. Manual recovery is required.");
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
                pending.KeyBefore,
                out string detail);
            RegistryRepairTransaction updated = pending.State switch
            {
                RegistryTransactionState.Prepared => pending.WithState(
                    restored
                        ? RegistryTransactionState.Recovered
                        : RegistryTransactionState.RollbackFailed,
                    detail),
                RegistryTransactionState.UndoPrepared => pending.WithState(
                    restored
                        ? RegistryTransactionState.Undone
                        : RegistryTransactionState.UndoFailed,
                    restored
                        ? "Interrupted undo completed. " + detail
                        : "Interrupted undo recovery failed. " + detail),
                _ => throw new RegistryRepairException(
                    $"Unsupported incomplete transaction state: {pending.State}."),
            };
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

    private async Task EnsureNoBlockingTransactionAsync(
        RegistryAddress address,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RegistryRepairTransaction> blocking =
            await _journal.GetBlockingAsync(cancellationToken).ConfigureAwait(false);
        RegistryRepairTransaction? unresolved = blocking.FirstOrDefault(transaction =>
            transaction.Address.Hive == address.Hive &&
            transaction.Address.View == address.View &&
            string.Equals(
                transaction.Address.KeyPath,
                address.KeyPath,
                StringComparison.OrdinalIgnoreCase));
        if (unresolved is not null)
            throw new RegistryRepairException(
                "A previous registry transaction for this key is unresolved. " +
                "Automatic corrections are blocked until recovery is verified.");
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

    private bool TryReapplyAndVerify(
        RegistryRepairTransaction transaction,
        out string detail)
    {
        try
        {
            _backend.SetValue(transaction.Address, transaction.AppliedValue);
            RegistryKeySnapshot verified = ReadKeyRequired(transaction.Address);
            RegistryKeySnapshot? before = transaction.KeyBefore;
            bool valid = before is not null
                ? before.WithValue(
                    transaction.Address.ValueName,
                    transaction.AppliedValue).ContentEquals(verified)
                : transaction.AppliedValue.ContentEquals(
                    verified.ForValue(transaction.Address.ValueName).Value);
            detail = valid
                ? "Undo rollback verified."
                : "Undo rollback completed but verification failed.";
            return valid;
        }
        catch (SimulatedProcessTerminationException)
        {
            throw;
        }
        catch (Exception error)
        {
            detail = "Undo rollback failed: " + error.Message;
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

    private static void EnsureExpectedKeyBeforeUndo(
        RegistryRepairTransaction transaction,
        RegistryKeySnapshot current)
    {
        if (transaction.KeyBefore is null)
            return;

        RegistryKeySnapshot expected = transaction.KeyBefore.WithValue(
            transaction.Address.ValueName,
            transaction.AppliedValue);
        if (!expected.ContentEquals(current))
            throw new RegistryRepairException(
                "The registry key changed after the correction. Run the scan again before undoing it.");
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
            FileStream? processLock = null;
            try
            {
                string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
                string lockDirectory = Path.Combine(Path.GetTempPath(), "Tweakly.RegistryRepair.Locks");
                Directory.CreateDirectory(lockDirectory);
                string lockPath = Path.Combine(lockDirectory, digest[..32] + ".lock");
                while (processLock is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        processLock = new FileStream(
                            lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                            1, FileOptions.None);
                    }
                    catch (IOException)
                    {
                        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    }
                }
                return new AddressLease(gate, processLock);
            }
            catch
            {
                processLock?.Dispose();
                gate.Release();
                throw;
            }
        }
    }

    private sealed class AddressLease : IDisposable
    {
        private SemaphoreSlim? _gate;
        private FileStream? _processLock;

        public AddressLease(SemaphoreSlim gate, FileStream processLock)
        {
            _gate = gate;
            _processLock = processLock;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _processLock, null)?.Dispose();
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
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
