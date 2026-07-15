using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    internal static class WingetCli
    {
        private static readonly Regex PackageIdPattern = new(
            @"^[A-Za-z0-9][A-Za-z0-9._+-]{2,127}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static string UserExecutablePath
        {
            get
            {
                string alias = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WindowsApps", "winget.exe");
                return File.Exists(alias) ? alias : "winget.exe";
            }
        }

        public static bool IsValidPackageId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string id = value.Trim();
            if (!PackageIdPattern.IsMatch(id) || !id.Contains('.', StringComparison.Ordinal)) return false;
            if (id.StartsWith("ARP", StringComparison.OrdinalIgnoreCase)) return false;
            string firstSegment = id[..id.IndexOf('.')];
            return firstSegment.Any(char.IsLetter);
        }
    }
}
