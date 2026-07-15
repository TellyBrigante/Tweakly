using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    public sealed record InstalledApplicationEntry(
        string Name,
        string Publisher,
        string Version,
        string InstallLocation,
        string UninstallString);

    /// <summary>
    /// Source unique pour l'inventaire des applications installees. Les pages
    /// transforment ensuite ces donnees vers leur propre modele d'affichage.
    /// </summary>
    public static class InstalledApplicationInventory
    {
        private static readonly string[] MachinePaths =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        private const string UserPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        public static IReadOnlyList<InstalledApplicationEntry> Read()
        {
            var result = new List<InstalledApplicationEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skippedKeys = 0;

            using (RegistryKey machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine,
                       RegistryView.Registry64))
            {
                foreach (string path in MachinePaths)
                    ReadRoot(machine, path, result, seen, ref skippedKeys);
            }

            ReadRoot(Registry.CurrentUser, UserPath, result, seen, ref skippedKeys);

            if (skippedKeys > 0)
                AppLog.Write($"Inventaire applications : {skippedKeys} entrée(s) registre illisible(s) ignorée(s).");

            result.Sort((left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            return result;
        }

        private static void ReadRoot(
            RegistryKey hive,
            string path,
            List<InstalledApplicationEntry> result,
            HashSet<string> seen,
            ref int skippedKeys)
        {
            try
            {
                using RegistryKey? root = hive.OpenSubKey(path, writable: false);
                if (root == null) return;

                foreach (string subKeyName in root.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? key = root.OpenSubKey(subKeyName, writable: false);
                        if (key == null) continue;

                        InstalledApplicationEntry? entry = ReadEntry(key);
                        if (entry != null && seen.Add(entry.Name))
                            result.Add(entry);
                    }
                    catch
                    {
                        skippedKeys++;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"Inventaire applications : lecture de {path}", ex);
            }
        }

        private static InstalledApplicationEntry? ReadEntry(RegistryKey key)
        {
            string name = Convert.ToString(key.GetValue("DisplayName"))?.Trim() ?? "";
            if (name.Length == 0) return null;
            if (key.GetValue("SystemComponent") is int systemComponent && systemComponent == 1)
                return null;
            if (Regex.IsMatch(name, @"^KB\d{6,}", RegexOptions.IgnoreCase)) return null;
            if (name.StartsWith("Security Update", StringComparison.OrdinalIgnoreCase)) return null;
            if (name.StartsWith("Update for", StringComparison.OrdinalIgnoreCase)) return null;
            if (name.StartsWith("Hotfix for", StringComparison.OrdinalIgnoreCase)) return null;

            return new InstalledApplicationEntry(
                name,
                Convert.ToString(key.GetValue("Publisher"))?.Trim() ?? "",
                Convert.ToString(key.GetValue("DisplayVersion"))?.Trim() ?? "",
                Convert.ToString(key.GetValue("InstallLocation"))?.Trim() ?? "",
                Convert.ToString(key.GetValue("QuietUninstallString"))?.Trim()
                    ?? Convert.ToString(key.GetValue("UninstallString"))?.Trim()
                    ?? "");
        }
    }
}
