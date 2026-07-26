using System.Buffers.Binary;
using System.Text;
using RegistryRepair.Core;

namespace RegistryRepair.Windows;

public sealed record OfflineWindowsImageIdentity(
    int Build,
    int UpdateBuildRevision,
    string Edition,
    string ProductName,
    string DisplayVersion);

public sealed class OfflineWindowsImage : IDisposable, IRegistryInspectionBackend
{
    private const string CurrentVersionKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    private readonly OfflineWindowsRegistryCorpus _corpus;
    private bool _disposed;

    private OfflineWindowsImage(
        string imageRoot,
        OfflineWindowsRegistryCorpus corpus,
        OfflineWindowsRegistryCorpusFiles files,
        OfflineWindowsImageIdentity identity)
    {
        ImageRoot = imageRoot;
        _corpus = corpus;
        Files = files;
        Identity = identity;
    }

    public string ImageRoot { get; }

    public OfflineWindowsRegistryCorpusFiles Files { get; }

    public OfflineWindowsImageIdentity Identity { get; }

    public IReadOnlyDictionary<string, string> HiveHashes => _corpus.SourceHashes;

    public static OfflineWindowsImage Open(string imageRoot)
    {
        string root = Path.GetFullPath(imageRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        RejectHostWindowsRoot(root);

        string config = Path.Combine(root, "Windows", "System32", "config");
        string user = Path.Combine(root, "Users", "Default", "NTUSER.DAT");
        var files = new OfflineWindowsRegistryCorpusFiles(
            Path.Combine(config, "SOFTWARE"),
            Path.Combine(config, "SYSTEM"),
            Path.Combine(config, "DEFAULT"),
            File.Exists(user) ? user : null);

        var corpus = new OfflineWindowsRegistryCorpus(files);
        try
        {
            OfflineWindowsImageIdentity identity = ReadIdentity(corpus);
            return new OfflineWindowsImage(root, corpus, files, identity);
        }
        catch
        {
            corpus.Dispose();
            throw;
        }
    }

    public RegistryReadResult Read(RegistryAddress address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _corpus.Read(address);
    }

    public RegistryKeyReadResult ReadKey(RegistryAddress address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _corpus.ReadKey(address);
    }

    public RegistrySubKeyReadResult EnumerateSubKeyNames(RegistryAddress address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _corpus.EnumerateSubKeyNames(address);
    }

    public bool SourceHivesAreUnchanged()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _corpus.SourcesAreUnchanged(Files);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _corpus.Dispose();
    }

    private static OfflineWindowsImageIdentity ReadIdentity(
        OfflineWindowsRegistryCorpus corpus)
    {
        string buildText = ReadString(corpus, "CurrentBuildNumber");
        if (!int.TryParse(buildText, out int build) || build <= 0)
            throw new RegistryRepairException(
                "The offline Windows build number is invalid.");

        return new OfflineWindowsImageIdentity(
            build,
            ReadDword(corpus, "UBR"),
            ReadString(corpus, "EditionID"),
            ReadString(corpus, "ProductName"),
            ReadString(corpus, "DisplayVersion"));
    }

    private static string ReadString(
        OfflineWindowsRegistryCorpus corpus,
        string valueName)
    {
        RawRegistryValue value = ReadRequired(corpus, valueName);
        if (value.Type is not RegistryValueType.String and not RegistryValueType.ExpandString ||
            value.Data.Length % sizeof(char) != 0)
            throw new RegistryRepairException(
                $"Offline Windows value {valueName} has an invalid type.");

        return Encoding.Unicode.GetString(value.Data).TrimEnd('\0');
    }

    private static int ReadDword(
        OfflineWindowsRegistryCorpus corpus,
        string valueName)
    {
        RawRegistryValue value = ReadRequired(corpus, valueName);
        if (value.Type != RegistryValueType.DWord || value.Data.Length != sizeof(int))
            throw new RegistryRepairException(
                $"Offline Windows value {valueName} has an invalid type.");
        return BinaryPrimitives.ReadInt32LittleEndian(value.Data);
    }

    private static RawRegistryValue ReadRequired(
        OfflineWindowsRegistryCorpus corpus,
        string valueName)
    {
        RegistryReadResult result = corpus.Read(new RegistryAddress(
            RegistryHiveId.LocalMachine,
            RegistryViewId.Registry64,
            CurrentVersionKey,
            valueName));
        if (!result.Success || result.Snapshot?.Value is null)
            throw new RegistryRepairException(
                $"Offline Windows value {valueName} is unavailable: " +
                (result.ErrorCode ?? result.ErrorMessage ?? "missing value"));
        return result.Snapshot.Value;
    }

    private static void RejectHostWindowsRoot(string candidate)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string? hostRoot = Path.GetDirectoryName(windows.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (hostRoot is not null &&
            string.Equals(
                candidate,
                hostRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new RegistryRepairException(
                "The active Windows installation cannot be used as an offline corpus.");
    }
}
