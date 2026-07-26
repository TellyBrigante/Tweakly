namespace RegistryRepair.Core;

public enum RegistryTransactionState
{
    Prepared,
    Committed,
    RolledBack,
    RollbackFailed,
    Undone,
    UndoFailed,
    Recovered,
}

public sealed record RegistryRepairTransaction(
    Guid Id,
    string RuleId,
    RegistryAddress Address,
    RegistrySnapshot Before,
    RawRegistryValue AppliedValue,
    RegistryTransactionState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Source,
    string Reason,
    string? Detail,
    RegistryKeySnapshot? KeyBefore = null)
{
    public RegistryRepairTransaction WithState(
        RegistryTransactionState state,
        string? detail = null) =>
        this with
        {
            State = state,
            UpdatedAt = DateTimeOffset.UtcNow,
            Detail = detail,
        };
}

public sealed record RegistryRepairResult(
    bool Success,
    bool RollbackSucceeded,
    RegistryRepairTransaction Transaction,
    string Message);

public sealed record RegistryUndoResult(
    bool Success,
    RegistryRepairTransaction Transaction,
    string Message);

public sealed class RegistryRepairException : Exception
{
    public RegistryRepairException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class SimulatedProcessTerminationException : Exception
{
    public SimulatedProcessTerminationException()
        : base("Simulated abrupt process termination.")
    {
    }
}
