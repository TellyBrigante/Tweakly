using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RegistryRepair.Windows;

internal static partial class NativeRegistry
{
    internal const int ErrorSuccess = 0;
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorMoreData = 234;
    internal const int ErrorNoMoreItems = 259;

    internal const int KeyQueryValue = 0x0001;
    internal const int KeySetValue = 0x0002;
    internal const int KeyCreateSubKey = 0x0004;
    internal const int KeyEnumerateSubKeys = 0x0008;
    internal const int KeyReadControl = 0x00020000;
    internal const int KeyWow64_64Key = 0x0100;
    internal const int KeyWow64_32Key = 0x0200;
    internal const int KeyRead = KeyReadControl | KeyQueryValue | KeyEnumerateSubKeys;
    internal const int KeyWrite = KeyReadControl | KeySetValue | KeyCreateSubKey;

    internal const int OwnerSecurityInformation = 0x00000001;
    internal const int GroupSecurityInformation = 0x00000002;
    internal const int DaclSecurityInformation = 0x00000004;
    internal const int SecurityInformation =
        OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation;

    internal static readonly nint HkeyCurrentUser = new(unchecked((int)0x80000001));
    internal static readonly nint HkeyLocalMachine = new(unchecked((int)0x80000002));

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegOpenKeyEx(
        nint hKey,
        string subKey,
        uint options,
        int desiredAccess,
        out SafeRegistryHandle result);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegCreateKeyEx(
        nint hKey,
        string subKey,
        uint reserved,
        string? keyClass,
        uint options,
        int desiredAccess,
        nint securityAttributes,
        out SafeRegistryHandle result,
        out uint disposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegQueryValueEx(
        SafeRegistryHandle hKey,
        string valueName,
        nint reserved,
        out uint type,
        byte[]? data,
        ref uint dataSize);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    internal static partial int RegQueryInfoKey(
        SafeRegistryHandle hKey,
        nint keyClass,
        nint keyClassLength,
        nint reserved,
        out uint subKeyCount,
        out uint maximumSubKeyLength,
        out uint maximumClassLength,
        out uint valueCount,
        out uint maximumValueNameLength,
        out uint maximumValueDataLength,
        out uint securityDescriptorLength,
        nint lastWriteTime);

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumValueW")]
    internal static unsafe partial int RegEnumValue(
        SafeRegistryHandle hKey,
        uint index,
        char* valueName,
        ref uint valueNameLength,
        nint reserved,
        out uint type,
        byte* data,
        ref uint dataLength);

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumKeyExW")]
    internal static unsafe partial int RegEnumKeyEx(
        SafeRegistryHandle hKey,
        uint index,
        char* name,
        ref uint nameLength,
        nint reserved,
        nint keyClass,
        nint keyClassLength,
        nint lastWriteTime);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegSetValueEx(
        SafeRegistryHandle hKey,
        string valueName,
        uint reserved,
        uint type,
        byte[] data,
        uint dataSize);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteValueW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegDeleteValue(
        SafeRegistryHandle hKey,
        string valueName);

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetKeySecurity")]
    internal static partial int RegGetKeySecurity(
        SafeRegistryHandle hKey,
        int securityInformation,
        byte[]? securityDescriptor,
        ref uint descriptorSize);

    [LibraryImport("advapi32.dll", EntryPoint = "RegLoadAppKeyW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegLoadAppKey(
        string file,
        out SafeRegistryHandle result,
        int desiredAccess,
        uint options,
        uint reserved);

    internal static void ThrowIfError(int error, string operation)
    {
        if (error == ErrorSuccess)
            return;
        throw new Win32Exception(error, operation);
    }
}
