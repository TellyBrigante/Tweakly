using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuTuningLab.Core;

public static class LabStore
{
    private static readonly JsonSerializerOptions Options = new(LabJsonContext.Default.Options)
    {
        WriteIndented = true
    };

    public static async Task SaveAsync(string path, LabSession session, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(session);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + ".tmp";
        string backupPath = fullPath + ".bak";

        await using (var stream = new FileStream(
            temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, session, Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(fullPath))
        {
            try { File.Replace(temporaryPath, fullPath, backupPath, ignoreMetadataErrors: true); }
            catch (PlatformNotSupportedException)
            {
                File.Copy(fullPath, backupPath, overwrite: true);
                File.Move(temporaryPath, fullPath, overwrite: true);
            }
        }
        else
        {
            File.Move(temporaryPath, fullPath);
        }
    }

    public static async Task<LabSession> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        foreach (string candidate in new[] { fullPath, fullPath + ".tmp", fullPath + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                await using var stream = File.OpenRead(candidate);
                var session = await JsonSerializer.DeserializeAsync(stream, LabJsonContext.Default.LabSession, cancellationToken)
                    .ConfigureAwait(false);
                if (session != null) return session;
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return new LabSession();
    }
}

public static class EvaluationPolicyStore
{
    public static EvaluationPolicy Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        return JsonSerializer.Deserialize(stream, PolicyJsonContext.Default.EvaluationPolicy)
               ?? throw new JsonException("The GPU evaluation policy is empty.");
    }
}

public static class GpuEvidenceStore
{
    public static async Task<PublishedTuningEvidence[]> LoadPublishedAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        return await JsonSerializer.DeserializeAsync(
                   stream,
                   LabJsonContext.Default.PublishedTuningEvidenceArray,
                   cancellationToken).ConfigureAwait(false)
               ?? [];
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(EvaluationPolicy))]
internal partial class PolicyJsonContext : JsonSerializerContext;

public static class ReferenceCatalogValidator
{
    private static readonly HashSet<string> ConfidenceLevels =
        new(["catalogOnly", "observed", "reviewed", "validated"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Validate(IReadOnlyList<GpuReferenceEntry> entries)
    {
        var errors = new List<string>();
        foreach (var duplicate in entries.GroupBy(
                     static entry => GpuReferenceMatcher.NormalizeModelName(entry.Model),
                     StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1))
            errors.Add($"Duplicate model: {duplicate.Key}");

        foreach (var entry in entries)
        {
            if (entry.Series is not (3000 or 4000 or 5000))
                errors.Add($"Unsupported series for {entry.Model}: {entry.Series}");
            if (!ConfidenceLevels.Contains(entry.Confidence))
                errors.Add($"Unknown confidence level for {entry.Model}: {entry.Confidence}");

            bool trusted = entry.Confidence.Equals("reviewed", StringComparison.OrdinalIgnoreCase)
                || entry.Confidence.Equals("validated", StringComparison.OrdinalIgnoreCase);
            int publishers = entry.Sources.Select(static source => source.Publisher)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (trusted && (entry.SearchEnvelope == null || publishers < 3))
                errors.Add($"{entry.Model} needs a complete envelope and three independent publishers.");

            if (entry.SearchEnvelope != null
                && entry.SearchEnvelope.MinimumVoltageMv >= entry.SearchEnvelope.MaximumVoltageMv)
                errors.Add($"Invalid voltage envelope for {entry.Model}.");
            if (entry.SearchEnvelope != null
                && entry.SearchEnvelope.MinimumClockMhz >= entry.SearchEnvelope.MaximumClockMhz)
                errors.Add($"Invalid clock envelope for {entry.Model}.");
            if (entry.SearchEnvelope?.VoltageStepMv is int step && step is < 5 or > 50)
                errors.Add($"Invalid voltage step for {entry.Model}: {step} mV.");

            foreach (EvidenceSource source in entry.Sources)
            {
                if (!Uri.TryCreate(source.Url, UriKind.Absolute, out Uri? uri)
                    || uri.Scheme is not ("http" or "https"))
                    errors.Add($"Invalid source URL for {entry.Model}: {source.Url}");
                if (string.IsNullOrWhiteSpace(source.Publisher))
                    errors.Add($"Source publisher missing for {entry.Model}.");
            }
        }

        foreach (int series in new[] { 3000, 4000, 5000 })
            if (!entries.Any(entry => entry.Series == series)) errors.Add($"Series {series} is missing.");
        return errors;
    }
}
