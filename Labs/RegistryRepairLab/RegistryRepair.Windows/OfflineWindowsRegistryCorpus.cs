using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using RegistryRepair.Core;

namespace RegistryRepair.Windows;

public sealed record OfflineWindowsRegistryCorpusFiles(
    string Software,
    string System,
    string Default,
    string? User = null);

public sealed class OfflineWindowsRegistryCorpus : IDisposable
{
    private readonly string _workingDirectory;
    private readonly SafeRegistryHandle _software;
    private readonly SafeRegistryHandle _system;
    private readonly SafeRegistryHandle _default;
    private readonly SafeRegistryHandle? _user;
    private readonly WindowsRegistryBackend _backend;
    private bool _disposed;

    public OfflineWindowsRegistryCorpus(OfflineWindowsRegistryCorpusFiles files)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows is required.");

        IReadOnlyDictionary<string, string> sources = ValidateSources(files);
        SourceHashes = sources.ToDictionary(
            item => item.Key,
            item => ComputeSha256(item.Value),
            StringComparer.OrdinalIgnoreCase);

        _workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "TweaklyRegistryCorpus",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workingDirectory);

        try
        {
            _software = LoadCopy(sources["SOFTWARE"], "SOFTWARE.hiv");
            _system = LoadCopy(sources["SYSTEM"], "SYSTEM.hiv");
            _default = LoadCopy(sources["DEFAULT"], "DEFAULT.hiv");
            _user = sources.TryGetValue("NTUSER.DAT", out string? user)
                ? LoadCopy(user, "NTUSER.DAT.hiv")
                : null;
            _backend = new WindowsRegistryBackend(
                ResolveRoot,
                applyWow64ViewFlags: false,
                TranslateAddress);
        }
        catch
        {
            DisposeHandles();
            TryDeleteWorkingDirectory();
            throw;
        }
    }

    public IReadOnlyDictionary<string, string> SourceHashes { get; }

    public RegistryReadResult Read(RegistryAddress address)
    {
        ThrowIfDisposed();
        RegistryAddress normalized = address.Normalize();
        if (normalized.View != RegistryViewId.Registry64)
        {
            return RegistryReadResult.Failure(
                "OFFLINE_32_VIEW_UNRESOLVED",
                "The physical WOW64 path is not proven for this offline rule.");
        }

        try
        {
            return _backend.Read(normalized);
        }
        catch (RegistryRepairException error)
        {
            return RegistryReadResult.Failure("OFFLINE_ADDRESS_REJECTED", error.Message);
        }
    }

    public RegistryKeyReadResult ReadKey(RegistryAddress address)
    {
        RegistryReadResult eligibility = CheckOfflineEligibility(address);
        if (!eligibility.Success)
            return RegistryKeyReadResult.Failure(
                eligibility.ErrorCode ?? "OFFLINE_ADDRESS_REJECTED",
                eligibility.ErrorMessage ?? "Offline address rejected.");

        try
        {
            return _backend.ReadKey(address.Normalize());
        }
        catch (RegistryRepairException error)
        {
            return RegistryKeyReadResult.Failure("OFFLINE_ADDRESS_REJECTED", error.Message);
        }
    }

    public RegistrySubKeyReadResult EnumerateSubKeyNames(RegistryAddress address)
    {
        RegistryReadResult eligibility = CheckOfflineEligibility(address);
        if (!eligibility.Success)
            return RegistrySubKeyReadResult.Failure(
                eligibility.ErrorCode ?? "OFFLINE_ADDRESS_REJECTED",
                eligibility.ErrorMessage ?? "Offline address rejected.");

        try
        {
            return _backend.EnumerateSubKeyNames(address.Normalize());
        }
        catch (RegistryRepairException error)
        {
            return RegistrySubKeyReadResult.Failure("OFFLINE_ADDRESS_REJECTED", error.Message);
        }
    }

    public bool SourcesAreUnchanged(OfflineWindowsRegistryCorpusFiles files)
    {
        ThrowIfDisposed();
        IReadOnlyDictionary<string, string> sources = ValidateSources(files);
        return SourceHashes.All(item =>
            sources.TryGetValue(item.Key, out string? path) &&
            string.Equals(item.Value, ComputeSha256(path), StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeHandles();
        TryDeleteWorkingDirectory();
    }

    private nint ResolveRoot(RegistryAddress address)
    {
        ThrowIfDisposed();
        if (address.Hive == RegistryHiveId.CurrentUser)
        {
            if (_user is null)
                throw new RegistryRepairException("The offline corpus has no NTUSER.DAT hive.");
            return _user.DangerousGetHandle();
        }

        string rootName = FirstPathPart(address.KeyPath);
        return rootName.ToUpperInvariant() switch
        {
            "SOFTWARE" => _software.DangerousGetHandle(),
            "SYSTEM" => _system.DangerousGetHandle(),
            "DEFAULT" => _default.DangerousGetHandle(),
            _ => throw new RegistryRepairException(
                $"Unsupported offline HKLM hive prefix: {rootName}."),
        };
    }

    private RegistryReadResult CheckOfflineEligibility(RegistryAddress address)
    {
        ThrowIfDisposed();
        RegistryAddress normalized = address.Normalize();
        return normalized.View == RegistryViewId.Registry64
            ? RegistryReadResult.FromSnapshot(new RegistrySnapshot(false, false, null, null))
            : RegistryReadResult.Failure(
                "OFFLINE_32_VIEW_UNRESOLVED",
                "The physical WOW64 path is not proven for this offline rule.");
    }

    private RegistryAddress TranslateAddress(RegistryAddress address)
    {
        if (address.Hive == RegistryHiveId.CurrentUser)
            return address;

        int separator = address.KeyPath.IndexOf('\\');
        if (separator < 0 || separator == address.KeyPath.Length - 1)
            throw new RegistryRepairException(
                "An offline HKLM address must contain a hive prefix and a key path.");

        return address with { KeyPath = address.KeyPath[(separator + 1)..] };
    }

    private SafeRegistryHandle LoadCopy(string source, string fileName)
    {
        string copy = Path.Combine(_workingDirectory, fileName);
        File.Copy(source, copy, overwrite: false);
        int error = NativeRegistry.RegLoadAppKey(
            copy,
            out SafeRegistryHandle handle,
            NativeRegistry.KeyRead,
            0,
            0);
        NativeRegistry.ThrowIfError(error, $"Unable to load offline hive {fileName}.");
        return handle;
    }

    private static IReadOnlyDictionary<string, string> ValidateSources(
        OfflineWindowsRegistryCorpusFiles files)
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SOFTWARE"] = RequireFile(files.Software, "SOFTWARE"),
            ["SYSTEM"] = RequireFile(files.System, "SYSTEM"),
            ["DEFAULT"] = RequireFile(files.Default, "DEFAULT"),
        };
        if (!string.IsNullOrWhiteSpace(files.User))
            sources["NTUSER.DAT"] = RequireFile(files.User, "NTUSER.DAT");
        return sources;
    }

    private static string RequireFile(string path, string label)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Offline hive {label} was not found.", fullPath);
        return fullPath;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string FirstPathPart(string path)
    {
        int separator = path.IndexOf('\\');
        return separator < 0 ? path : path[..separator];
    }

    private void DisposeHandles()
    {
        _user?.Dispose();
        _default?.Dispose();
        _system?.Dispose();
        _software?.Dispose();
    }

    private void TryDeleteWorkingDirectory()
    {
        if (!Directory.Exists(_workingDirectory))
            return;

        try
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
