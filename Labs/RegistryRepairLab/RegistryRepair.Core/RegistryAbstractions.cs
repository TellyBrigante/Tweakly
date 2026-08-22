namespace RegistryRepair.Core;

public interface IRegistryBackend
{
    RegistryReadResult Read(RegistryAddress address);

    RegistryKeyReadResult ReadKey(RegistryAddress address);

    void SetValue(RegistryAddress address, RawRegistryValue value);

    void Restore(RegistryAddress address, RegistrySnapshot snapshot);
}

public interface IRegistryInspectionBackend
{
    RegistryKeyReadResult ReadKey(RegistryAddress address);

    RegistrySubKeyReadResult EnumerateSubKeyNames(RegistryAddress address);
}

public interface IRegistryRepairJournal
{
    Task SaveAsync(RegistryRepairTransaction transaction, CancellationToken cancellationToken);

    Task<RegistryRepairTransaction?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<RegistryRepairTransaction>> GetIncompleteAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RegistryRepairTransaction>> GetBlockingAsync(
        CancellationToken cancellationToken);
}

public interface IRegistryRepairFaultInjector
{
    void AfterRegistryWrite(RegistryRepairTransaction transaction);
}

public sealed class NoRegistryRepairFaults : IRegistryRepairFaultInjector
{
    public static NoRegistryRepairFaults Instance { get; } = new();

    private NoRegistryRepairFaults()
    {
    }

    public void AfterRegistryWrite(RegistryRepairTransaction transaction)
    {
    }
}
