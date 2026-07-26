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
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, session, Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
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
        string[] existing = new[] { fullPath, fullPath + ".tmp", fullPath + ".bak" }
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        if (existing.Length == 0) return new LabSession();

        var failures = new List<string>();
        foreach (string candidate in existing)
        {
            try
            {
                await using var stream = new FileStream(
                    candidate,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var session = await JsonSerializer.DeserializeAsync(stream, LabJsonContext.Default.LabSession, cancellationToken)
                    .ConfigureAwait(false);
                if (session == null)
                {
                    failures.Add($"{Path.GetFileName(candidate)}: empty JSON document.");
                    continue;
                }

                IReadOnlyList<string> errors = LabSessionValidator.Validate(session);
                if (errors.Count == 0) return session;
                failures.Add($"{Path.GetFileName(candidate)}: {string.Join(" | ", errors)}");
            }
            catch (JsonException ex)
            {
                failures.Add($"{Path.GetFileName(candidate)}: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    $"GPU tuning history could not be read from {candidate}. " +
                    "No older copy was loaded to avoid overwriting newer data.",
                    ex);
            }
        }

        throw new InvalidDataException(
            "No valid GPU tuning history could be loaded. Existing files were preserved. " +
            string.Join(" || ", failures));
    }
}

public static class EvaluationPolicyStore
{
    public static EvaluationPolicy Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        EvaluationPolicy policy = JsonSerializer.Deserialize(stream, PolicyJsonContext.Default.EvaluationPolicy)
                                  ?? throw new JsonException("The GPU evaluation policy is empty.");
        IReadOnlyList<string> errors = EvaluationPolicyValidator.Validate(policy);
        if (errors.Count > 0)
            throw new InvalidDataException("Invalid GPU evaluation policy: " + string.Join(" | ", errors));
        return policy;
    }
}

public static class EvaluationPolicyValidator
{
    public static IReadOnlyList<string> Validate(EvaluationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var errors = new List<string>();
        InRange(policy.SamplingIntervalMs, 100, 2_000, nameof(policy.SamplingIntervalMs), errors);
        InRange(policy.MinimumTelemetryCoveragePercent, 50, 100, nameof(policy.MinimumTelemetryCoveragePercent), errors);
        InRange(policy.MinimumRequiredMetricCoveragePercent, 50, 100, nameof(policy.MinimumRequiredMetricCoveragePercent), errors);
        InRange(policy.MinimumPerformanceRetentionPercent, 50, 110, nameof(policy.MinimumPerformanceRetentionPercent), errors);
        InRange(policy.MinimumIndividualWorkloadRetentionPercent, 50, 110, nameof(policy.MinimumIndividualWorkloadRetentionPercent), errors);
        InRange(policy.MaximumBenchmarkVariancePercent, 0.1, 20, nameof(policy.MaximumBenchmarkVariancePercent), errors);
        InRange(policy.MaximumStartingTemperatureDeltaC, 0, 20, nameof(policy.MaximumStartingTemperatureDeltaC), errors);
        InRange(policy.MinimumBaselineWorkloadSeconds, 10, 120, nameof(policy.MinimumBaselineWorkloadSeconds), errors);
        InRange(policy.VoltageStepMv, 5, 100, nameof(policy.VoltageStepMv), errors);
        InRange(policy.ProfileVoltageToleranceMv, 5, 100, nameof(policy.ProfileVoltageToleranceMv), errors);
        InRange(policy.ProfileClockToleranceMhz, 10, 300, nameof(policy.ProfileClockToleranceMhz), errors);
        InRange(policy.ProfileMemoryOffsetToleranceMhz, 10, 500, nameof(policy.ProfileMemoryOffsetToleranceMhz), errors);
        InRange(policy.ProfilePowerLimitTolerancePercent, 0.1, 10, nameof(policy.ProfilePowerLimitTolerancePercent), errors);
        if (policy.RequiredValidationWorkloads == null || policy.RequiredValidationWorkloads.Length == 0)
            errors.Add("RequiredValidationWorkloads must not be empty.");
        else if (policy.RequiredValidationWorkloads.Any(static kind => !Enum.IsDefined(kind)))
            errors.Add("RequiredValidationWorkloads contains an unknown workload.");
        double weight = policy.PerformanceWeight + policy.EfficiencyWeight + policy.ThermalWeight;
        if (!double.IsFinite(weight) || Math.Abs(weight - 1) > 0.0001)
            errors.Add("Performance, efficiency and thermal weights must total 1.0.");
        return errors;
    }

    private static void InRange(double value, double minimum, double maximum, string name, List<string> errors)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            errors.Add($"{name} must be between {minimum} and {maximum}.");
    }
}

public static class LabSessionValidator
{
    public const int CurrentSchemaVersion = 2;

    public static IReadOnlyList<string> Validate(LabSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var errors = new List<string>();
        if (session.SchemaVersion is < 1 or > CurrentSchemaVersion)
            errors.Add($"Unsupported schema version {session.SchemaVersion}.");
        if (session.Runs == null)
        {
            errors.Add("Runs collection is missing.");
            return errors;
        }

        int index = 0;
        foreach (TestRun? run in session.Runs)
        {
            if (run == null)
            {
                errors.Add($"Run {index} is null.");
                index++;
                continue;
            }
            if (run.Identity == null || run.Profile == null)
            {
                errors.Add($"Run {index} is missing its GPU identity or profile.");
                index++;
                continue;
            }
            if (run.Samples == null || run.Workloads == null
                                    || run.WorkloadWindows == null || run.StabilityEvents == null)
            {
                errors.Add($"Run {index} has a missing collection.");
                index++;
                continue;
            }
            if (!Enum.IsDefined(run.Profile.Kind))
                errors.Add($"Run {index} has an unknown profile kind.");
            if (run.Workloads?.Any(static workload =>
                    workload == null
                    || !Enum.IsDefined(workload.Kind)
                    || !double.IsFinite(workload.Score)
                    || workload.Duration < TimeSpan.Zero) == true)
                errors.Add($"Run {index} contains an invalid workload result.");
            if (run.Samples?.Any(static sample =>
                    sample == null || sample.Timestamp == default) == true)
                errors.Add($"Run {index} contains an invalid telemetry sample.");
            index++;
        }
        return errors;
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
