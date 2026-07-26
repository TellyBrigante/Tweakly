using Microsoft.Win32.SafeHandles;
using RegistryRepair.Core;

namespace RegistryRepair.Windows;

public sealed class IsolatedRegistryHive : IDisposable
{
    private readonly string _directory;
    private readonly SafeRegistryHandle _view32;
    private readonly SafeRegistryHandle _view64;
    private bool _disposed;

    public IsolatedRegistryHive(string directory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows is required.");

        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _view32 = Load(Path.Combine(_directory, "registry32.hiv"));
        try
        {
            _view64 = Load(Path.Combine(_directory, "registry64.hiv"));
        }
        catch
        {
            _view32.Dispose();
            throw;
        }
    }

    public WindowsRegistryBackend CreateBackend() => new(
        ResolveRoot,
        applyWow64ViewFlags: false,
        static address => address);

    public void Seed(RegistryAddress address, RawRegistryValue value)
    {
        ThrowIfDisposed();
        RegistryAddress normalized = address.Normalize();
        int error = NativeRegistry.RegCreateKeyEx(
            ResolveRoot(normalized),
            normalized.KeyPath,
            0,
            null,
            0,
            NativeRegistry.KeyWrite | NativeRegistry.KeyQueryValue,
            nint.Zero,
            out SafeRegistryHandle key,
            out _);
        NativeRegistry.ThrowIfError(error, "Unable to create the isolated registry key.");
        using (key)
        {
            error = NativeRegistry.RegSetValueEx(
                key,
                normalized.ValueName,
                0,
                (uint)value.Type,
                value.Data,
                checked((uint)value.Data.Length));
            NativeRegistry.ThrowIfError(error, "Unable to seed the isolated registry value.");
        }
    }

    public void CreateKey(RegistryAddress address)
    {
        ThrowIfDisposed();
        RegistryAddress normalized = address.Normalize();
        int error = NativeRegistry.RegCreateKeyEx(
            ResolveRoot(normalized),
            normalized.KeyPath,
            0,
            null,
            0,
            NativeRegistry.KeyWrite | NativeRegistry.KeyQueryValue,
            nint.Zero,
            out SafeRegistryHandle key,
            out _);
        NativeRegistry.ThrowIfError(error, "Unable to create the isolated registry key.");
        key.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _view32.Dispose();
        _view64.Dispose();
    }

    public void DeleteFiles()
    {
        if (!_disposed)
            throw new InvalidOperationException("Dispose the hive before deleting its files.");
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private nint ResolveRoot(RegistryAddress address)
    {
        ThrowIfDisposed();
        return address.View == RegistryViewId.Registry64
            ? _view64.DangerousGetHandle()
            : _view32.DangerousGetHandle();
    }

    private static SafeRegistryHandle Load(string path)
    {
        int access = NativeRegistry.KeyRead | NativeRegistry.KeyWrite;
        int error = NativeRegistry.RegLoadAppKey(path, out SafeRegistryHandle key, access, 0, 0);
        NativeRegistry.ThrowIfError(error, "Unable to load an isolated registry hive.");
        return key;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
