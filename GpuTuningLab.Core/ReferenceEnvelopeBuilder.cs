namespace GpuTuningLab.Core;

public static class ReferenceEnvelopeBuilder
{
    public static ReferenceBuildResult Build(
        string model,
        IReadOnlyList<GpuTuningObservation> observations,
        ReferenceBuildPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(policy);

        var reasons = new List<string>();
        var usable = observations
            .Where(item => GpuReferenceMatcher.SameModel(item.Model, model))
            .Where(IsUsable)
            .Where(item => item.Summary.TelemetryCoveragePercent >= policy.MinimumTelemetryCoveragePercent)
            .Where(item => item.BenchmarkVariancePercent <= policy.MaximumBenchmarkVariancePercent)
            .Where(item => item.PerformanceIndex >= policy.MinimumPerformanceRetentionPercent)
            .ToArray();
        var candidates = usable
            .GroupBy(static item => item.IndependentUnitId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static item => item.EfficiencyIndex ?? item.PerformanceIndex)
                .ThenByDescending(static item => item.PerformanceIndex)
                .First())
            .ToArray();
        int units = candidates.Length;
        int publishers = candidates.Select(static item => item.Source.Publisher)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        string[] formFactors = candidates.Select(static item => item.FormFactor)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (formFactors.Length > 1)
            reasons.Add("Desktop and mobile observations cannot share one envelope.");

        if (units < policy.MinimumIndependentUnits)
            reasons.Add($"{units} independent units; {policy.MinimumIndependentUnits} required.");
        if (publishers < policy.MinimumIndependentPublishers)
            reasons.Add($"{publishers} independent publishers; {policy.MinimumIndependentPublishers} required.");

        int[] voltages = candidates.Select(static item => item.Profile.TargetVoltageMv)
            .Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        int[] clocks = candidates.Select(static item => item.Profile.TargetClockMhz)
            .Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        if (voltages.Length < policy.MinimumIndependentUnits)
            reasons.Add("Too few validated target-voltage observations.");
        if (clocks.Length < policy.MinimumIndependentUnits)
            reasons.Add("Too few validated target-clock observations.");

        bool eligible = reasons.Count == 0;
        SearchEnvelope? envelope = eligible
            ? new SearchEnvelope
            {
                MinimumVoltageMv = Quantile(voltages, 0.10),
                MaximumVoltageMv = Quantile(voltages, 0.90),
                MinimumClockMhz = Quantile(clocks, 0.10),
                MaximumClockMhz = Quantile(clocks, 0.90),
                MaximumMemoryOffsetMhz = ConservativeMemoryOffset(candidates),
                VoltageStepMv = 25
            }
            : null;

        return new ReferenceBuildResult(
            model,
            eligible,
            eligible ? "observed" : "catalogOnly",
            envelope,
            candidates.Length,
            units,
            publishers,
            reasons);
    }

    private static bool IsUsable(GpuTuningObservation item)
        => item.Summary.Verdict == StabilityVerdict.Validated
           && item.Profile.Kind is ProfileKind.Undervolt or ProfileKind.Mixed
           && item.Profile.TargetVoltageMv.HasValue
           && item.Profile.TargetClockMhz.HasValue
           && !string.IsNullOrWhiteSpace(item.ProtocolVersion)
           && !string.IsNullOrWhiteSpace(item.VbiosVersion)
           && !string.IsNullOrWhiteSpace(item.Source.Url);

    private static int? ConservativeMemoryOffset(IEnumerable<GpuTuningObservation> observations)
    {
        int[] values = observations.Select(static item => item.Profile.MemoryOffsetMhz)
            .Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return values.Length == 0 ? null : Quantile(values, 0.25);
    }

    private static int Quantile(IReadOnlyCollection<int> values, double quantile)
    {
        int[] sorted = values.Order().ToArray();
        double position = (sorted.Length - 1) * quantile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        double result = lower == upper
            ? sorted[lower]
            : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
        return (int)Math.Round(result / 5.0, MidpointRounding.AwayFromZero) * 5;
    }
}
