using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Optimisation_Tool.Helpers;

public static class UpdateTransferPolicy
{
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
}

public static class UpdatePackageValidator
{
    public static async Task VerifySha256Async(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(expectedSha256))
            throw new InvalidDataException(
                "Cette mise à jour ne publie pas de hash d'intégrité (SHA-256). " +
                "Installation refusée par sécurité.");

        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(
            await sha.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Échec de la vérification d'intégrité du téléchargement (SHA-256 différent). " +
                "Mise à jour annulée par sécurité.");
    }

    public static string ExtractAndFindSource(string zipPath, string extractDirectory)
    {
        ZipFile.ExtractToDirectory(zipPath, extractDirectory);
        return FindExeDirectory(extractDirectory)
            ?? throw new InvalidDataException("Tweakly.exe introuvable dans l'archive.");
    }

    public static string BuildUpdaterScript(string sourceDirectory, string installDirectory, string exePath) =>
        "@echo off\r\n" +
        ":wait\r\n" +
        "tasklist /fi \"imagename eq Tweakly.exe\" 2>nul | find /i \"Tweakly.exe\" >nul\r\n" +
        "if not errorlevel 1 (\r\n" +
        "  timeout /t 1 /nobreak >nul\r\n" +
        "  goto wait\r\n" +
        ")\r\n" +
        "timeout /t 1 /nobreak >nul\r\n" +
        $"robocopy \"{sourceDirectory}\" \"{installDirectory}\" /E /R:10 /W:2 /NFL /NDL /NJH /NJS /NP >nul\r\n" +
        $"start \"\" \"{exePath}\" --after-update\r\n" +
        "del \"%~f0\"\r\n";

    private static string? FindExeDirectory(string root)
    {
        try
        {
            var exe = Directory.GetFiles(root, "Tweakly.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            return exe != null ? Path.GetDirectoryName(exe) : null;
        }
        catch
        {
            return null;
        }
    }
}
