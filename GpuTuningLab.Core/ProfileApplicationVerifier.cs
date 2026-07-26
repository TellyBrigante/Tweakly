namespace GpuTuningLab.Core;

public static class ProfileApplicationVerifier
{
    private const double LoadedUtilizationPercent = 50;
    private const int MinimumLoadedSamples = 10;

    public static ProfileApplicationAssessment Assess(
        TestRun baseline,
        TestRun candidate,
        EvaluationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);

        var reasons = new List<string>();
        GpuTelemetrySample[] loaded = candidate.Samples
            .Where(static sample => sample.GpuUtilizationPercent >= LoadedUtilizationPercent)
            .ToArray();
        if (loaded.Length < MinimumLoadedSamples)
        {
            reasons.Add(
                $"Only {loaded.Length} loaded telemetry samples are available; {MinimumLoadedSamples} are required.");
        }

        int? observedVoltage = LoadedVoltageMv(loaded);
        if (candidate.Profile.TargetVoltageMv is int targetVoltage)
        {
            if (!observedVoltage.HasValue)
            {
                reasons.Add("Loaded GPU voltage is unavailable, so the applied voltage cannot be verified.");
            }
            else if (Math.Abs(observedVoltage.Value - targetVoltage) > policy.ProfileVoltageToleranceMv)
            {
                reasons.Add(
                    $"The loaded voltage is {observedVoltage.Value} mV, not the declared {targetVoltage} mV " +
                    $"(allowed difference: {policy.ProfileVoltageToleranceMv} mV).");
            }
        }

        int? observedClock = PercentileInt(
            loaded.Select(static sample => sample.CoreClockMhz),
            0.95);
        if (candidate.Profile.TargetClockMhz is int targetClock)
        {
            if (!observedClock.HasValue)
            {
                reasons.Add("Loaded GPU clock is unavailable, so the applied frequency cannot be verified.");
            }
            else if (Math.Abs(observedClock.Value - targetClock) > policy.ProfileClockToleranceMhz)
            {
                reasons.Add(
                    $"The loaded GPU clock is {observedClock.Value} MHz, not the declared {targetClock} MHz " +
                    $"(allowed difference: {policy.ProfileClockToleranceMhz} MHz).");
            }
        }

        GpuTelemetrySample[] loadedBaseline = baseline.Samples
            .Where(static sample => sample.GpuUtilizationPercent >= LoadedUtilizationPercent)
            .ToArray();
        int? baselineMemory = MedianInt(loadedBaseline.Select(static sample => sample.MemoryClockMhz));
        int? candidateMemory = MedianInt(loaded.Select(static sample => sample.MemoryClockMhz));
        int? observedMemoryOffset = baselineMemory.HasValue && candidateMemory.HasValue
            ? candidateMemory.Value - baselineMemory.Value
            : null;
        if (candidate.Profile.MemoryOffsetMhz is int targetMemoryOffset)
        {
            if (!observedMemoryOffset.HasValue)
            {
                reasons.Add("GPU memory clocks are unavailable, so the memory offset cannot be verified.");
            }
            else if (Math.Abs(observedMemoryOffset.Value - targetMemoryOffset)
                     > policy.ProfileMemoryOffsetToleranceMhz)
            {
                reasons.Add(
                    $"The observed memory offset is {observedMemoryOffset.Value:+0;-0;0} MHz, not the declared " +
                    $"{targetMemoryOffset:+0;-0;0} MHz (allowed difference: {policy.ProfileMemoryOffsetToleranceMhz} MHz).");
            }
        }

        double? observedPowerLimit = Median(loaded
            .Where(static sample => sample.RequestedPowerLimitW > 0 && sample.DefaultPowerLimitW > 0)
            .Select(static sample =>
                sample.RequestedPowerLimitW!.Value / sample.DefaultPowerLimitW!.Value * 100));
        if (candidate.Profile.PowerLimitPercent is double targetPowerLimit)
        {
            if (!observedPowerLimit.HasValue)
            {
                reasons.Add("GPU power-limit telemetry is unavailable, so the Power Limit cannot be verified.");
            }
            else if (Math.Abs(observedPowerLimit.Value - targetPowerLimit)
                     > policy.ProfilePowerLimitTolerancePercent)
            {
                reasons.Add(
                    $"The observed Power Limit is {observedPowerLimit.Value:0.0} %, not the declared " +
                    $"{targetPowerLimit:0.0} % (allowed difference: {policy.ProfilePowerLimitTolerancePercent:0.0} %).");
            }
        }

        return new ProfileApplicationAssessment
        {
            Verified = reasons.Count == 0,
            BlockingReasons = reasons.Distinct().ToArray(),
            ObservedVoltageMv = observedVoltage,
            ObservedClockMhz = observedClock,
            ObservedMemoryOffsetMhz = observedMemoryOffset,
            ObservedPowerLimitPercent = observedPowerLimit
        };
    }

    private static int? LoadedVoltageMv(IReadOnlyList<GpuTelemetrySample> loaded)
    {
        double? maximumClock = loaded
            .Select(static sample => sample.CoreClockMhz)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .DefaultIfEmpty()
            .Max();
        if (!maximumClock.HasValue || maximumClock <= 0) return null;

        double[] nearPeakVoltages = loaded
            .Where(sample => sample.CoreClockMhz >= maximumClock.Value - 60 && sample.VoltageV > 0)
            .Select(static sample => sample.VoltageV!.Value * 1_000)
            .ToArray();
        double? median = Median(nearPeakVoltages);
        return median.HasValue ? (int)Math.Round(median.Value / 5.0) * 5 : null;
    }

    private static int? MedianInt(IEnumerable<double?> values)
    {
        double? median = Median(values
            .Where(static value => value.HasValue && double.IsFinite(value.Value))
            .Select(static value => value!.Value));
        return median.HasValue ? (int)Math.Round(median.Value) : null;
    }

    private static int? PercentileInt(IEnumerable<double?> values, double percentile)
    {
        double[] sorted = values
            .Where(static value => value.HasValue && double.IsFinite(value.Value))
            .Select(static value => value!.Value)
            .Order()
            .ToArray();
        if (sorted.Length == 0) return null;
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        double result = lower == upper
            ? sorted[lower]
            : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
        return (int)Math.Round(result / 5.0) * 5;
    }

    private static double? Median(IEnumerable<double> values)
    {
        double[] sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) return null;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }
}
