using RegistryRepair.Core;

namespace RegistryRepair.Tests;

internal sealed class InMemoryRegistryBackend : IRegistryBackend, IRegistryInspectionBackend
{
    private readonly Dictionary<RegistryAddress, RegistrySnapshot> _values = new();
    private readonly object _sync = new();
    private int _activeWrites;

    public bool DenyReads { get; set; }
    public bool FailNextWrite { get; set; }
    public bool CorruptNextWrite { get; set; }
    public bool ChangeAclOnNextWrite { get; set; }
    public bool FailNextRestore { get; set; }
    public bool TerminateNextRestore { get; set; }
    public bool MutateSiblingOnNextWrite { get; set; }
    public int WriteDelayMilliseconds { get; set; }
    public int MaximumConcurrentWrites { get; private set; }
    public HashSet<RegistryAddress> DeniedInspectionAddresses { get; } = [];

    public void Seed(RegistryAddress address, RegistrySnapshot snapshot)
    {
        lock (_sync)
            _values[address.Normalize()] = snapshot.DeepCopy();
    }

    public RegistrySnapshot Get(RegistryAddress address)
    {
        lock (_sync)
        {
            return _values.TryGetValue(address.Normalize(), out RegistrySnapshot? snapshot)
                ? snapshot.DeepCopy()
                : new RegistrySnapshot(false, false, null, null);
        }
    }

    public RegistryReadResult Read(RegistryAddress address)
    {
        if (DenyReads)
            return RegistryReadResult.Failure("ACCESS_DENIED", "Access denied.");
        return RegistryReadResult.FromSnapshot(Get(address));
    }

    public RegistryKeyReadResult ReadKey(RegistryAddress address)
    {
        RegistryAddress normalized = address.Normalize();
        if (DenyReads || DeniedInspectionAddresses.Contains(normalized))
            return RegistryKeyReadResult.Failure("ACCESS_DENIED", "Access denied.");

        lock (_sync)
        {
            RegistrySnapshot[] matching = _values
                .Where(item =>
                    item.Key.Hive == normalized.Hive &&
                    item.Key.View == normalized.View &&
                    string.Equals(
                        item.Key.KeyPath,
                        normalized.KeyPath,
                        StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Value)
                .ToArray();
            if (matching.Length == 0)
                return RegistryKeyReadResult.FromSnapshot(
                    new RegistryKeySnapshot(false, new Dictionary<string, RawRegistryValue>(), null));

            var values = new Dictionary<string, RawRegistryValue>(StringComparer.OrdinalIgnoreCase);
            foreach ((RegistryAddress candidate, RegistrySnapshot snapshot) in _values)
            {
                if (candidate.Hive == normalized.Hive &&
                    candidate.View == normalized.View &&
                    string.Equals(candidate.KeyPath, normalized.KeyPath, StringComparison.OrdinalIgnoreCase) &&
                    snapshot.ValueExists &&
                    snapshot.Value is not null)
                    values[candidate.ValueName] = snapshot.Value.DeepCopy();
            }

            return RegistryKeyReadResult.FromSnapshot(new RegistryKeySnapshot(
                true,
                values,
                matching[0].SecurityDescriptorSddl));
        }
    }

    public RegistrySubKeyReadResult EnumerateSubKeyNames(RegistryAddress address)
    {
        if (DenyReads)
            return RegistrySubKeyReadResult.Failure("ACCESS_DENIED", "Access denied.");

        RegistryAddress normalized = address.Normalize();
        string prefix = normalized.KeyPath.TrimEnd('\\') + "\\";
        lock (_sync)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool keyExists = _values.Keys.Any(candidate =>
                candidate.Hive == normalized.Hive &&
                candidate.View == normalized.View &&
                (string.Equals(candidate.KeyPath, normalized.KeyPath, StringComparison.OrdinalIgnoreCase) ||
                 candidate.KeyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            foreach (RegistryAddress candidate in _values.Keys)
            {
                if (candidate.Hive != normalized.Hive ||
                    candidate.View != normalized.View ||
                    !candidate.KeyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string remainder = candidate.KeyPath[prefix.Length..];
                int separator = remainder.IndexOf('\\');
                names.Add(separator < 0 ? remainder : remainder[..separator]);
            }

            return RegistrySubKeyReadResult.FromNames(keyExists, names.OrderBy(name => name));
        }
    }

    public void SetValue(RegistryAddress address, RawRegistryValue value)
    {
        RegistryAddress normalized = address.Normalize();
        lock (_sync)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new UnauthorizedAccessException("Simulated write refusal.");
            }

            _activeWrites++;
            MaximumConcurrentWrites = Math.Max(MaximumConcurrentWrites, _activeWrites);
        }

        try
        {
            if (WriteDelayMilliseconds > 0)
                Thread.Sleep(WriteDelayMilliseconds);

            lock (_sync)
            {
                RegistrySnapshot before = _values.TryGetValue(normalized, out RegistrySnapshot? snapshot)
                    ? snapshot.DeepCopy()
                    : new RegistrySnapshot(false, false, null, null);
                RawRegistryValue written = CorruptNextWrite
                    ? RawRegistryValue.DWord(0x12345678)
                    : value.DeepCopy();
                CorruptNextWrite = false;
                string? sddl = ChangeAclOnNextWrite ? "D:CHANGED" : before.SecurityDescriptorSddl;
                ChangeAclOnNextWrite = false;
                _values[normalized] = new RegistrySnapshot(true, true, written, sddl);
                if (MutateSiblingOnNextWrite)
                {
                    MutateSiblingOnNextWrite = false;
                    RegistryAddress sibling = normalized with { ValueName = "UnexpectedSibling" };
                    _values[sibling] = new RegistrySnapshot(
                        true,
                        true,
                        RawRegistryValue.DWord(999),
                        sddl);
                }
            }
        }
        finally
        {
            lock (_sync)
                _activeWrites--;
        }
    }

    public void Restore(RegistryAddress address, RegistrySnapshot snapshot)
    {
        lock (_sync)
        {
            if (TerminateNextRestore)
            {
                TerminateNextRestore = false;
                throw new SimulatedProcessTerminationException();
            }
            if (FailNextRestore)
            {
                FailNextRestore = false;
                throw new UnauthorizedAccessException("Simulated restore refusal.");
            }

            _values[address.Normalize()] = snapshot.DeepCopy();
        }
    }
}

internal sealed class InMemoryJournal : IRegistryRepairJournal
{
    private readonly Dictionary<Guid, RegistryRepairTransaction> _entries = new();

    public bool FailNextSave { get; set; }

    public Task SaveAsync(
        RegistryRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailNextSave)
        {
            FailNextSave = false;
            throw new IOException("Simulated durable journal failure.");
        }

        _entries[transaction.Id] = transaction;
        return Task.CompletedTask;
    }

    public Task<RegistryRepairTransaction?> GetAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.TryGetValue(id, out RegistryRepairTransaction? transaction);
        return Task.FromResult(transaction);
    }

    public Task<IReadOnlyList<RegistryRepairTransaction>> GetIncompleteAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<RegistryRepairTransaction> pending = _entries.Values
            .Where(entry => entry.State == RegistryTransactionState.Prepared)
            .ToArray();
        return Task.FromResult(pending);
    }
}

internal sealed class TerminateAfterWrite : IRegistryRepairFaultInjector
{
    public void AfterRegistryWrite(RegistryRepairTransaction transaction) =>
        throw new SimulatedProcessTerminationException();
}
