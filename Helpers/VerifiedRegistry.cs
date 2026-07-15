using Microsoft.Win32;
using System;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Ecrit une valeur puis la relit depuis une nouvelle cle. Une optimisation ne peut
    /// donc pas annoncer un succes si Windows n'a pas conserve la valeur demandee.
    /// </summary>
    public static class VerifiedRegistry
    {
        public static void SetDword(RegistryKey root, string subKey, string name, int value)
        {
            using (RegistryKey key = root.CreateSubKey(subKey, writable: true)
                ?? throw new InvalidOperationException($"Cle registre inaccessible : {subKey}"))
            {
                key.SetValue(name, value, RegistryValueKind.DWord);
                key.Flush();
            }

            object? actual = Read(root, subKey, name);
            if (actual == null || Convert.ToInt32(actual) != value)
                throw new InvalidOperationException($"Windows n'a pas conserve {name}={value}.");
        }

        public static void SetString(RegistryKey root, string subKey, string name, string value)
        {
            using (RegistryKey key = root.CreateSubKey(subKey, writable: true)
                ?? throw new InvalidOperationException($"Cle registre inaccessible : {subKey}"))
            {
                key.SetValue(name, value, RegistryValueKind.String);
                key.Flush();
            }

            string actual = Convert.ToString(Read(root, subKey, name)) ?? "";
            if (!actual.Equals(value, StringComparison.Ordinal))
                throw new InvalidOperationException($"Windows n'a pas conserve la valeur {name}.");
        }

        public static void DeleteValue(RegistryKey root, string subKey, string name)
        {
            using (RegistryKey? key = root.OpenSubKey(subKey, writable: true))
            {
                if (key == null) return;
                key.DeleteValue(name, throwOnMissingValue: false);
                key.Flush();
            }

            if (Read(root, subKey, name) != null)
                throw new InvalidOperationException($"Windows n'a pas supprime la valeur {name}.");
        }

        public static bool IsDword(RegistryKey root, string subKey, string name, int expected)
        {
            object? actual = Read(root, subKey, name);
            return actual != null && Convert.ToInt32(actual) == expected;
        }

        public static bool IsMissing(RegistryKey root, string subKey, string name)
            => Read(root, subKey, name) == null;

        private static object? Read(RegistryKey root, string subKey, string name)
        {
            using RegistryKey? key = root.OpenSubKey(subKey, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
    }
}
