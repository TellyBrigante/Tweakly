using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GpuTuningLab.Core;

public sealed record WorkloadPackageValidation(
    bool Valid,
    string D3D11WorkloadPath,
    string RayTracingWorkloadPath,
    IReadOnlyList<string> Errors)
{
    public string Fingerprint { get; init; } = "";
}

public static class WorkloadPackageValidator
{
    private static readonly string[] RequiredFiles =
    [
        "d3d11/GpuTuningLab.Workload.exe",
        "d3d11/THIRD_PARTY_NOTICES.md",
        "dxr/GpuTuningLab.RayTracingWorkload.exe",
        "dxr/D3D12/D3D12Core.dll",
        "dxr/THIRD_PARTY_NOTICES.md",
        "dxr/D3D12_LICENSE.txt",
        "dxr/D3D12_LICENSE-CODE.txt"
    ];

    public static async Task<WorkloadPackageValidation> ValidateAsync(
        string packageRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        string root = Path.GetFullPath(packageRoot);
        string manifestPath = Path.Combine(root, "manifest.json");
        string d3d11 = Path.Combine(root, "d3d11", "GpuTuningLab.Workload.exe");
        string dxr = Path.Combine(root, "dxr", "GpuTuningLab.RayTracingWorkload.exe");
        var errors = new List<string>();
        if (!File.Exists(manifestPath))
            return new(false, d3d11, dxr, ["Workload manifest is missing."]);

        WorkloadManifestEntry[] entries;
        string fingerprint;
        try
        {
            byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken)
                .ConfigureAwait(false);
            fingerprint = Convert.ToHexString(SHA256.HashData(manifestBytes));
            string json = Encoding.UTF8.GetString(manifestBytes);
            if (json.Length > 0 && json[0] == '\uFEFF')
                json = json[1..];
            entries = JsonSerializer.Deserialize<WorkloadManifestEntry[]>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new(false, d3d11, dxr, [$"Workload manifest cannot be read: {ex.Message}"]);
        }

        var manifestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WorkloadManifestEntry? entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry == null)
            {
                errors.Add("Workload manifest contains a null entry.");
                continue;
            }
            string relative = Normalize(entry.Path);
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/').Contains(".."))
            {
                errors.Add($"Unsafe manifest path: {entry.Path}");
                continue;
            }
            if (entry.Bytes <= 0)
            {
                errors.Add($"Invalid workload size in manifest: {relative}");
                continue;
            }
            if (!IsSha256(entry.Sha256))
            {
                errors.Add($"Invalid SHA-256 in manifest: {relative}");
                continue;
            }
            if (!manifestFiles.Add(relative))
            {
                errors.Add($"Duplicate manifest path: {relative}");
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add($"Invalid manifest path {relative}: {ex.Message}");
                continue;
            }
            if (!IsInside(root, fullPath))
            {
                errors.Add($"Manifest path leaves the workload directory: {relative}");
                continue;
            }
            if (!File.Exists(fullPath))
            {
                errors.Add($"Workload file is missing: {relative}");
                continue;
            }

            var info = new FileInfo(fullPath);
            if (info.Length != entry.Bytes)
            {
                errors.Add($"Workload size mismatch: {relative}");
                continue;
            }
            await using var file = File.OpenRead(fullPath);
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            if (!actualHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Workload SHA-256 mismatch: {relative}");
        }

        foreach (string required in RequiredFiles)
            if (!manifestFiles.Contains(required)) errors.Add($"Required workload is absent from the manifest: {required}");

        if (Directory.Exists(root))
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Normalize(Path.GetRelativePath(root, file));
                if (!relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                    && !manifestFiles.Contains(relative))
                    errors.Add($"Unmanifested workload file: {relative}");
            }
        }

        return new(errors.Count == 0, d3d11, dxr, errors.Distinct().ToArray())
        {
            Fingerprint = fingerprint
        };
    }

    private static string Normalize(string? path) => (path ?? "").Replace('\\', '/').Trim();

    private static bool IsSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character =>
               character is >= '0' and <= '9'
               or >= 'a' and <= 'f'
               or >= 'A' and <= 'F');

    private static bool IsInside(string root, string path)
    {
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record WorkloadManifestEntry(string? Path, long Bytes, string? Sha256);
}
