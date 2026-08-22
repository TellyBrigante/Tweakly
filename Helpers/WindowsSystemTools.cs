using System;
using System.IO;

namespace Optimisation_Tool.Helpers
{
    internal static class WindowsSystemTools
    {
        public static string PathFor(string executableName)
        {
            string name = System.IO.Path.GetFileName(executableName ?? "");
            if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, executableName, StringComparison.Ordinal))
                throw new ArgumentException("Le nom de l'outil Windows n'est pas valide.", nameof(executableName));
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name += ".exe";

            string path = System.IO.Path.Combine(Environment.SystemDirectory, name);
            if (!File.Exists(path))
                throw new FileNotFoundException("Outil système Windows introuvable.", path);
            return path;
        }
    }
}
