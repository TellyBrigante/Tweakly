using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Optimisation_Tool.Helpers
{
    internal static class BundledFileTrust
    {
        private static readonly IReadOnlyDictionary<string, string> Sha256ByName =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PawnIO_setup.exe"] = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032",
                ["PawnIOLib.dll"] = "D71F62627D66983BB9F5B1C269F27BCD8C1B8A46E794377A6330F84C198F4443",
                ["PresentMon.exe"] = "D74183E7AE630F72CD3690BE0373ECBFDC6CBB86578148AAB8FA2A7166068F34",
                ["nvidiaProfileInspector.exe"] = "7D5510DEEAACB50C88A49BBF1D894DAE44C5CE58C00D5A88392346646B14E8F3",
                ["PSFExtractor.exe"] = "27A21585EEB22455AADFE1FB65D35D1DE2AE6C62AFF4F77BEB9F40573832024E",
            };

        public static IDisposable OpenVerifiedLease(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string dataRoot = Path.GetFullPath(PathLayout.Data)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Le binaire fourni est hors du dossier data de Tweakly.");

            string fileName = Path.GetFileName(fullPath);
            if (!Sha256ByName.TryGetValue(fileName, out string? expected))
                throw new InvalidOperationException($"Aucune empreinte de confiance n'est définie pour {fileName}.");

            EnsureNoReparsePoint(dataRoot.TrimEnd(Path.DirectorySeparatorChar), fullPath);
            var stream = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
            try
            {
                string actual = Convert.ToHexString(SHA256.HashData(stream));
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(actual), Convert.FromHexString(expected)))
                    throw new InvalidOperationException(
                        $"{fileName} ne correspond pas à l'empreinte approuvée. Exécution refusée.");
                stream.Position = 0;
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static void EnsureNoReparsePoint(string dataRoot, string filePath)
        {
            var current = new FileInfo(filePath);
            if (!current.Exists)
                throw new FileNotFoundException("Binaire fourni introuvable.", filePath);
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Un lien ou point de jonction est interdit pour un binaire fourni.");

            DirectoryInfo? directory = current.Directory;
            while (directory is not null)
            {
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("Un lien ou point de jonction est interdit dans le chemin du binaire.");
                if (string.Equals(directory.FullName, dataRoot, StringComparison.OrdinalIgnoreCase))
                    return;
                directory = directory.Parent;
            }
            throw new InvalidOperationException("Le chemin du binaire ne rejoint pas le dossier data attendu.");
        }
    }
}
