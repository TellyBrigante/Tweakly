using System.ComponentModel;
using RegistryRepair.Core;
using Microsoft.Win32.SafeHandles;

namespace RegistryRepair.Windows;

public sealed class WindowsRegistryBackend : IRegistryBackend, IRegistryInspectionBackend
{
    private readonly Func<RegistryAddress, nint> _rootResolver;
    private readonly Func<RegistryAddress, RegistryAddress> _addressMapper;
    private readonly bool _applyWow64ViewFlags;

    public WindowsRegistryBackend()
        : this(ResolveSystemRoot, applyWow64ViewFlags: true, static address => address)
    {
    }

    internal WindowsRegistryBackend(
        Func<RegistryAddress, nint> rootResolver,
        bool applyWow64ViewFlags,
        Func<RegistryAddress, RegistryAddress>? addressMapper = null)
    {
        _rootResolver = rootResolver;
        _applyWow64ViewFlags = applyWow64ViewFlags;
        _addressMapper = addressMapper ?? (static address => address);
    }

    public RegistryReadResult Read(RegistryAddress address)
    {
        RegistryAddress normalized = address.Normalize();
        int error = OpenKey(normalized, NativeRegistry.KeyQueryValue | NativeRegistry.KeyReadControl, out SafeRegistryHandle key);
        if (error == NativeRegistry.ErrorFileNotFound)
            return RegistryReadResult.FromSnapshot(new RegistrySnapshot(false, false, null, null));
        if (error != NativeRegistry.ErrorSuccess)
            return RegistryReadResult.Failure(error.ToString(), "Unable to open the registry key.");

        using (key)
        {
            string security;
            try
            {
                security = ReadSecurityDescriptor(key);
            }
            catch (Exception exception)
            {
                return RegistryReadResult.Failure(
                    "SECURITY_DESCRIPTOR_UNAVAILABLE",
                    exception.Message);
            }

            uint size = 0;
            error = NativeRegistry.RegQueryValueEx(
                key,
                normalized.ValueName,
                nint.Zero,
                out uint type,
                null,
                ref size);
            if (error == NativeRegistry.ErrorFileNotFound)
                return RegistryReadResult.FromSnapshot(
                    new RegistrySnapshot(true, false, null, security));
            if (error is not NativeRegistry.ErrorSuccess and not NativeRegistry.ErrorInsufficientBuffer)
                return RegistryReadResult.Failure(error.ToString(), "Unable to query the registry value.");

            byte[] data = new byte[size];
            error = NativeRegistry.RegQueryValueEx(
                key,
                normalized.ValueName,
                nint.Zero,
                out type,
                data,
                ref size);
            if (error != NativeRegistry.ErrorSuccess)
                return RegistryReadResult.Failure(error.ToString(), "Unable to read the registry value.");
            if (size != data.Length)
                Array.Resize(ref data, checked((int)size));

            return RegistryReadResult.FromSnapshot(new RegistrySnapshot(
                true,
                true,
                new RawRegistryValue((RegistryValueType)type, data),
                security));
        }
    }

    public RegistryKeyReadResult ReadKey(RegistryAddress address)
    {
        RegistryAddress normalized = address.Normalize();
        int error = OpenKey(
            normalized,
            NativeRegistry.KeyQueryValue | NativeRegistry.KeyReadControl,
            out SafeRegistryHandle key);
        if (error == NativeRegistry.ErrorFileNotFound)
            return RegistryKeyReadResult.FromSnapshot(new RegistryKeySnapshot(
                false,
                new Dictionary<string, RawRegistryValue>(),
                null));
        if (error != NativeRegistry.ErrorSuccess)
            return RegistryKeyReadResult.Failure(
                error.ToString(),
                "Unable to open the registry key.");

        using (key)
        {
            try
            {
                string security = ReadSecurityDescriptor(key);
                var values = ReadAllValues(key);
                return RegistryKeyReadResult.FromSnapshot(new RegistryKeySnapshot(
                    true,
                    values,
                    security));
            }
            catch (Exception exception)
            {
                return RegistryKeyReadResult.Failure(
                    "KEY_BACKUP_UNAVAILABLE",
                    exception.Message);
            }
        }
    }

    public RegistrySubKeyReadResult EnumerateSubKeyNames(RegistryAddress address)
    {
        RegistryAddress normalized = address.Normalize();
        int error = OpenKey(
            normalized,
            NativeRegistry.KeyEnumerateSubKeys |
            NativeRegistry.KeyQueryValue |
            NativeRegistry.KeyReadControl,
            out SafeRegistryHandle key);
        if (error == NativeRegistry.ErrorFileNotFound)
            return RegistrySubKeyReadResult.FromNames(false, Array.Empty<string>());
        if (error != NativeRegistry.ErrorSuccess)
            return RegistrySubKeyReadResult.Failure(
                error.ToString(),
                "Unable to open the registry key for enumeration.");

        using (key)
        {
            try
            {
                return RegistrySubKeyReadResult.FromNames(true, ReadAllSubKeyNames(key));
            }
            catch (Win32Exception exception)
            {
                return RegistrySubKeyReadResult.Failure(
                    exception.NativeErrorCode.ToString(),
                    exception.Message);
            }
            catch (Exception exception)
            {
                return RegistrySubKeyReadResult.Failure(
                    "SUBKEY_ENUMERATION_FAILED",
                    exception.Message);
            }
        }
    }

    public void SetValue(RegistryAddress address, RawRegistryValue value)
    {
        RegistryAddress normalized = address.Normalize();
        int error = OpenKey(normalized, NativeRegistry.KeySetValue | NativeRegistry.KeyReadControl, out SafeRegistryHandle key);
        NativeRegistry.ThrowIfError(error, "Unable to open the registry key for writing.");
        using (key)
        {
            error = NativeRegistry.RegSetValueEx(
                key,
                normalized.ValueName,
                0,
                (uint)value.Type,
                value.Data,
                checked((uint)value.Data.Length));
            NativeRegistry.ThrowIfError(error, "Unable to write the registry value.");
        }
    }

    public void Restore(RegistryAddress address, RegistrySnapshot snapshot)
    {
        if (!snapshot.KeyExists)
            throw new NotSupportedException("Registry key creation/deletion is disabled in v1.");

        RegistryAddress normalized = address.Normalize();
        int error = OpenKey(normalized, NativeRegistry.KeySetValue | NativeRegistry.KeyReadControl, out SafeRegistryHandle key);
        NativeRegistry.ThrowIfError(error, "Unable to open the registry key for restoration.");
        using (key)
        {
            if (snapshot.ValueExists && snapshot.Value is not null)
            {
                error = NativeRegistry.RegSetValueEx(
                    key,
                    normalized.ValueName,
                    0,
                    (uint)snapshot.Value.Type,
                    snapshot.Value.Data,
                    checked((uint)snapshot.Value.Data.Length));
                NativeRegistry.ThrowIfError(error, "Unable to restore the registry value.");
                return;
            }

            error = NativeRegistry.RegDeleteValue(key, normalized.ValueName);
            if (error is not NativeRegistry.ErrorSuccess and not NativeRegistry.ErrorFileNotFound)
                NativeRegistry.ThrowIfError(error, "Unable to remove the repaired registry value.");
        }
    }

    private int OpenKey(
        RegistryAddress address,
        int desiredAccess,
        out SafeRegistryHandle key)
    {
        RegistryAddress mapped = _addressMapper(address);
        int view = _applyWow64ViewFlags
            ? address.View == RegistryViewId.Registry64
                ? NativeRegistry.KeyWow64_64Key
                : NativeRegistry.KeyWow64_32Key
            : 0;
        return NativeRegistry.RegOpenKeyEx(
            _rootResolver(address),
            mapped.KeyPath,
            0,
            desiredAccess | view,
            out key);
    }

    private static nint ResolveSystemRoot(RegistryAddress address) => address.Hive switch
    {
        RegistryHiveId.CurrentUser => NativeRegistry.HkeyCurrentUser,
        RegistryHiveId.LocalMachine => NativeRegistry.HkeyLocalMachine,
        _ => throw new ArgumentOutOfRangeException(nameof(address)),
    };

    private static string ReadSecurityDescriptor(SafeRegistryHandle key)
    {
        uint size = 0;
        int error = NativeRegistry.RegGetKeySecurity(
            key,
            NativeRegistry.SecurityInformation,
            null,
            ref size);
        if (error != NativeRegistry.ErrorInsufficientBuffer)
            NativeRegistry.ThrowIfError(error, "Unable to size the registry security descriptor.");

        byte[] descriptor = new byte[size];
        error = NativeRegistry.RegGetKeySecurity(
            key,
            NativeRegistry.SecurityInformation,
            descriptor,
            ref size);
        NativeRegistry.ThrowIfError(error, "Unable to read the registry security descriptor.");
        if (size != descriptor.Length)
            Array.Resize(ref descriptor, checked((int)size));
        return Convert.ToBase64String(descriptor);
    }

    private static unsafe IReadOnlyDictionary<string, RawRegistryValue> ReadAllValues(
        SafeRegistryHandle key)
    {
        int error = NativeRegistry.RegQueryInfoKey(
            key,
            nint.Zero,
            nint.Zero,
            nint.Zero,
            out _,
            out _,
            out _,
            out uint valueCount,
            out uint maximumNameLength,
            out uint maximumDataLength,
            out _,
            nint.Zero);
        NativeRegistry.ThrowIfError(error, "Unable to inspect the registry key.");

        var values = new Dictionary<string, RawRegistryValue>(StringComparer.OrdinalIgnoreCase);
        char[] name = new char[checked((int)maximumNameLength + 1)];
        byte[] data = new byte[Math.Max(checked((int)maximumDataLength), 1)];

        for (uint index = 0; index < valueCount; index++)
        {
            uint nameLength = checked((uint)name.Length);
            uint dataLength = checked((uint)data.Length);
            uint type;
            fixed (char* namePointer = name)
            fixed (byte* dataPointer = data)
            {
                error = NativeRegistry.RegEnumValue(
                    key,
                    index,
                    namePointer,
                    ref nameLength,
                    nint.Zero,
                    out type,
                    dataPointer,
                    ref dataLength);
            }
            if (error == NativeRegistry.ErrorNoMoreItems)
                break;
            if (error == NativeRegistry.ErrorMoreData)
                throw new RegistryRepairException(
                    "The registry key changed while its backup was being captured.");
            NativeRegistry.ThrowIfError(error, "Unable to enumerate a registry value.");

            string valueName = new(name, 0, checked((int)nameLength));
            byte[] valueData = data.AsSpan(0, checked((int)dataLength)).ToArray();
            values[valueName] = new RawRegistryValue((RegistryValueType)type, valueData);
        }

        return values;
    }

    private static unsafe IReadOnlyList<string> ReadAllSubKeyNames(
        SafeRegistryHandle key)
    {
        int error = NativeRegistry.RegQueryInfoKey(
            key,
            nint.Zero,
            nint.Zero,
            nint.Zero,
            out uint subKeyCount,
            out uint maximumSubKeyLength,
            out _,
            out _,
            out _,
            out _,
            out _,
            nint.Zero);
        NativeRegistry.ThrowIfError(error, "Unable to inspect registry subkeys.");

        var names = new List<string>(checked((int)subKeyCount));
        char[] name = new char[checked((int)maximumSubKeyLength + 1)];
        for (uint index = 0; index < subKeyCount; index++)
        {
            uint nameLength = checked((uint)name.Length);
            fixed (char* namePointer = name)
            {
                error = NativeRegistry.RegEnumKeyEx(
                    key,
                    index,
                    namePointer,
                    ref nameLength,
                    nint.Zero,
                    nint.Zero,
                    nint.Zero,
                    nint.Zero);
            }
            if (error == NativeRegistry.ErrorNoMoreItems)
                break;
            if (error == NativeRegistry.ErrorMoreData)
                throw new RegistryRepairException(
                    "The registry key changed while its subkeys were being enumerated.");
            NativeRegistry.ThrowIfError(error, "Unable to enumerate a registry subkey.");
            names.Add(new string(name, 0, checked((int)nameLength)));
        }

        return names;
    }
}
