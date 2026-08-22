using System.Security.Cryptography;
using System.Text.Json;

namespace RegistryRepair.Core;

public sealed class FileRegistryRepairJournal : IRegistryRepairJournal, IDisposable
{
    private const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly byte[] _authenticationKey;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileRegistryRepairJournal(string directory, ReadOnlySpan<byte> authenticationKey)
    {
        if (authenticationKey.Length < 32)
            throw new ArgumentException("The journal authentication key must contain at least 32 bytes.", nameof(authenticationKey));
        _directory = Path.GetFullPath(directory);
        _authenticationKey = authenticationKey.ToArray();
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(
        RegistryRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string destination = PathFor(transaction.Id);
            string temporary = destination + ".tmp";
            byte[] transactionJson = JsonSerializer.SerializeToUtf8Bytes(transaction, JsonOptions);
            var envelope = new JournalEnvelope(
                CurrentSchemaVersion,
                transaction,
                Convert.ToHexString(HMACSHA256.HashData(_authenticationKey, transactionJson)));
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

            await using (var stream = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RegistryRepairTransaction?> GetAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadFileAsync(PathFor(id), id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<RegistryRepairTransaction>> GetIncompleteAsync(
        CancellationToken cancellationToken) =>
        GetByStateAsync(
            static state => state is
                RegistryTransactionState.Prepared or
                RegistryTransactionState.UndoPrepared,
            cancellationToken);

    public Task<IReadOnlyList<RegistryRepairTransaction>> GetBlockingAsync(
        CancellationToken cancellationToken) =>
        GetByStateAsync(
            static state => state is
                RegistryTransactionState.Prepared or
                RegistryTransactionState.UndoPrepared or
                RegistryTransactionState.RollbackFailed or
                RegistryTransactionState.UndoFailed,
            cancellationToken);

    private async Task<IReadOnlyList<RegistryRepairTransaction>> GetByStateAsync(
        Func<RegistryTransactionState, bool> predicate,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new List<RegistryRepairTransaction>();
            foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id))
                    throw new RegistryRepairException(
                        $"Invalid registry repair journal file name: {Path.GetFileName(path)}.");

                RegistryRepairTransaction? transaction =
                    await ReadFileAsync(path, id, cancellationToken).ConfigureAwait(false);
                if (transaction is not null && predicate(transaction.State))
                    result.Add(transaction);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_authenticationKey);
        _gate.Dispose();
    }

    private string PathFor(Guid id) => Path.Combine(_directory, id.ToString("N") + ".json");

    private async Task<RegistryRepairTransaction?> ReadFileAsync(
        string path,
        Guid expectedId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            byte[] json = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            JournalEnvelope envelope = JsonSerializer.Deserialize<JournalEnvelope>(json, JsonOptions)
                ?? throw new RegistryRepairException("The registry repair journal is empty.");

            if (envelope.Transaction is null || string.IsNullOrWhiteSpace(envelope.Mac))
                throw new RegistryRepairException(
                    "The registry repair journal is incomplete.");

            if (envelope.SchemaVersion != CurrentSchemaVersion)
                throw new RegistryRepairException(
                    $"Unsupported registry repair journal schema: {envelope.SchemaVersion}.");
            if (envelope.Transaction.Id != expectedId)
                throw new RegistryRepairException(
                    "The registry repair journal identifier does not match its file name.");

            byte[] transactionJson = JsonSerializer.SerializeToUtf8Bytes(
                envelope.Transaction,
                JsonOptions);
            byte[] actualMac = HMACSHA256.HashData(_authenticationKey, transactionJson);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(envelope.Mac),
                    actualMac))
                throw new RegistryRepairException(
                    "The registry repair journal failed its authenticated integrity check.");

            return envelope.Transaction;
        }
        catch (RegistryRepairException)
        {
            throw;
        }
        catch (Exception error) when (
            error is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new RegistryRepairException(
                $"Unable to validate registry repair journal {Path.GetFileName(path)}.",
                error);
        }
    }

    private sealed record JournalEnvelope(
        int SchemaVersion,
        RegistryRepairTransaction Transaction,
        string Mac);
}
