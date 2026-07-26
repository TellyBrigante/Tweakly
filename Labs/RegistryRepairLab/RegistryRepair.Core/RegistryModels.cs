using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RegistryRepair.Core;

public enum RegistryHiveId
{
    CurrentUser,
    LocalMachine,
}

public enum RegistryViewId
{
    Registry32,
    Registry64,
}

public enum RegistryValueType : uint
{
    None = 0,
    String = 1,
    ExpandString = 2,
    Binary = 3,
    DWord = 4,
    MultiString = 7,
    QWord = 11,
}

public sealed record RegistryAddress(
    RegistryHiveId Hive,
    RegistryViewId View,
    string KeyPath,
    string ValueName)
{
    public RegistryAddress Normalize()
    {
        string path = KeyPath.Trim().Trim('\\');
        if (path.Length == 0)
            throw new ArgumentException("The registry key path cannot be empty.", nameof(KeyPath));

        return this with { KeyPath = path };
    }

    public override string ToString() => $"{Hive}:{View}\\{KeyPath}\\{ValueName}";
}

public sealed record RawRegistryValue(RegistryValueType Type, byte[] Data)
{
    public RawRegistryValue DeepCopy() => new(Type, (byte[])Data.Clone());

    public static RawRegistryValue DWord(int value)
    {
        byte[] data = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(data, value);
        return new RawRegistryValue(RegistryValueType.DWord, data);
    }

    public static RawRegistryValue QWord(long value)
    {
        byte[] data = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(data, value);
        return new RawRegistryValue(RegistryValueType.QWord, data);
    }

    public static RawRegistryValue String(string value, bool expandable = false)
    {
        byte[] data = Encoding.Unicode.GetBytes(value + '\0');
        return new RawRegistryValue(
            expandable ? RegistryValueType.ExpandString : RegistryValueType.String,
            data);
    }

    public bool ContentEquals(RawRegistryValue? other) =>
        other is not null && Type == other.Type && Data.AsSpan().SequenceEqual(other.Data);
}

public sealed record RegistrySnapshot(
    bool KeyExists,
    bool ValueExists,
    RawRegistryValue? Value,
    string? SecurityDescriptorSddl)
{
    public RegistrySnapshot DeepCopy() => new(
        KeyExists,
        ValueExists,
        Value?.DeepCopy(),
        SecurityDescriptorSddl);

    public bool ContentEquals(RegistrySnapshot? other, bool includeSecurity = true)
    {
        if (other is null || KeyExists != other.KeyExists || ValueExists != other.ValueExists)
            return false;

        if (includeSecurity && !string.Equals(
                SecurityDescriptorSddl,
                other.SecurityDescriptorSddl,
                StringComparison.Ordinal))
            return false;

        if (!ValueExists)
            return true;

        return Value?.ContentEquals(other.Value) == true;
    }

    public string Fingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([(byte)(KeyExists ? 1 : 0), (byte)(ValueExists ? 1 : 0)]);
        if (Value is not null)
        {
            hash.AppendData(BitConverter.GetBytes((uint)Value.Type));
            hash.AppendData(Value.Data);
        }

        hash.AppendData(Encoding.UTF8.GetBytes(SecurityDescriptorSddl ?? string.Empty));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

public sealed record RegistryKeySnapshot(
    bool KeyExists,
    IReadOnlyDictionary<string, RawRegistryValue> Values,
    string? SecurityDescriptorSddl)
{
    public RegistryKeySnapshot DeepCopy() => new(
        KeyExists,
        Values.ToDictionary(
            item => item.Key,
            item => item.Value.DeepCopy(),
            StringComparer.OrdinalIgnoreCase),
        SecurityDescriptorSddl);

    public RegistrySnapshot ForValue(string valueName)
    {
        bool found = Values.TryGetValue(valueName, out RawRegistryValue? value);
        return new RegistrySnapshot(
            KeyExists,
            found,
            value?.DeepCopy(),
            SecurityDescriptorSddl);
    }

    public RegistryKeySnapshot WithValue(string valueName, RawRegistryValue value)
    {
        var updated = Values.ToDictionary(
            item => item.Key,
            item => item.Value.DeepCopy(),
            StringComparer.OrdinalIgnoreCase);
        updated[valueName] = value.DeepCopy();
        return new RegistryKeySnapshot(KeyExists, updated, SecurityDescriptorSddl);
    }

    public bool ContentEquals(RegistryKeySnapshot? other)
    {
        if (other is null ||
            KeyExists != other.KeyExists ||
            !string.Equals(SecurityDescriptorSddl, other.SecurityDescriptorSddl, StringComparison.Ordinal) ||
            Values.Count != other.Values.Count)
            return false;

        foreach ((string name, RawRegistryValue value) in Values)
        {
            if (!other.Values.TryGetValue(name, out RawRegistryValue? otherValue) ||
                !value.ContentEquals(otherValue))
                return false;
        }

        return true;
    }
}

public sealed record RegistryReadResult(
    bool Success,
    RegistrySnapshot? Snapshot,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static RegistryReadResult FromSnapshot(RegistrySnapshot snapshot) =>
        new(true, snapshot.DeepCopy(), null, null);

    public static RegistryReadResult Failure(string code, string message) =>
        new(false, null, code, message);
}

public sealed record RegistryKeyReadResult(
    bool Success,
    RegistryKeySnapshot? Snapshot,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static RegistryKeyReadResult FromSnapshot(RegistryKeySnapshot snapshot) =>
        new(true, snapshot.DeepCopy(), null, null);

    public static RegistryKeyReadResult Failure(string code, string message) =>
        new(false, null, code, message);
}

public sealed record RegistrySubKeyReadResult(
    bool Success,
    bool KeyExists,
    IReadOnlyList<string> SubKeyNames,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static RegistrySubKeyReadResult FromNames(
        bool keyExists,
        IEnumerable<string> names) =>
        new(true, keyExists, names.ToArray(), null, null);

    public static RegistrySubKeyReadResult Failure(string code, string message) =>
        new(false, false, Array.Empty<string>(), code, message);
}

public sealed record WindowsIdentity(
    int Build,
    string Edition,
    bool Is64BitOperatingSystem);
