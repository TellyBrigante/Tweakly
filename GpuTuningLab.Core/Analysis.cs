namespace GpuTuningLab.Core;

public static class RunAnalyzer
{
    public static RunSummary Summarize(TestRun run, EvaluationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(policy);

        var ordered = run.Samples.OrderBy(static sample => sample.Timestamp).ToArray();
        (TimeSpan duration, double expectedSamples) = MeasureTelemetrySpan(
            ordered,
            policy.SamplingIntervalMs);
        double coverage = Math.Clamp(ordered.Length / expectedSamples * 100, 0, 100);

        var reasons = new List<string>();
        StabilityVerdict verdict = GetVerdict(run, policy, coverage, reasons);
        GpuTelemetrySample[] active = ordered.Where(IsUnderLoad).ToArray();
        GpuTelemetrySample[] measured = active.Length >= 2 ? active : ordered;
        double[] temperatures = Values(measured, static sample => sample.TemperatureC);
        double[] powers = Values(measured, static sample => sample.PowerAverageW ?? sample.PowerInstantW);
        double[] clocks = Values(measured, static sample => sample.CoreClockMhz);

        double powerLimitPercent = Ratio(active, static sample =>
            sample.ClockEventReasons.HasFlag(NvidiaClockEventReasons.SoftwarePowerCap));
        double thermalLimitPercent = Ratio(active, static sample =>
            sample.ClockEventReasons.HasFlag(NvidiaClockEventReasons.SoftwareThermalSlowdown)
            || sample.ClockEventReasons.HasFlag(NvidiaClockEventReasons.HardwareThermalSlowdown));

        if (powerLimitPercent > 0) reasons.Add($"Power limit active during {powerLimitPercent:0.0} % of samples.");
        if (thermalLimitPercent > 0) reasons.Add($"Thermal limit active during {thermalLimitPercent:0.0} % of samples.");

        return new RunSummary
        {
            RunId = run.Id,
            Verdict = verdict,
            TelemetryDuration = duration,
            TelemetryCoveragePercent = coverage,
            AverageTemperatureC = Average(temperatures),
            P95TemperatureC = Percentile(temperatures, 0.95),
            MaxTemperatureC = Max(temperatures),
            AveragePowerW = Average(powers),
            P95PowerW = Percentile(powers, 0.95),
            MaxPowerW = Max(powers),
            AverageCoreClockMhz = Average(clocks),
            P05CoreClockMhz = Percentile(clocks, 0.05),
            EnergyWh = IntegrateEnergyWh(ordered, policy.SamplingIntervalMs),
            PowerLimitTimePercent = powerLimitPercent,
            ThermalLimitTimePercent = thermalLimitPercent,
            Reasons = reasons
        };
    }

    private static StabilityVerdict GetVerdict(
        TestRun run,
        EvaluationPolicy policy,
        double coverage,
        List<string> reasons)
    {
        var critical = run.StabilityEvents.FirstOrDefault(static item => item.Kind is
            StabilityEventKind.DriverReset or StabilityEventKind.Tdr or
            StabilityEventKind.ApplicationCrash or StabilityEventKind.Artifact or StabilityEventKind.Whea);
        if (critical != null)
        {
            reasons.Add($"Rejected: {critical.Kind} - {critical.Evidence}");
            return StabilityVerdict.Rejected;
        }

        if (run.Samples.Count < 2 || coverage < policy.MinimumTelemetryCoveragePercent)
        {
            reasons.Add($"Telemetry coverage is {coverage:0.0} %; minimum is {policy.MinimumTelemetryCoveragePercent:0.0} %.");
            return StabilityVerdict.InvalidTelemetry;
        }

        if (run.Workloads.Count == 0)
        {
            reasons.Add("Telemetry captured without a scored workload.");
            return StabilityVerdict.TelemetryOnly;
        }

        if (run.Workloads.Any(static result => !result.Completed))
        {
            string details = string.Join(" | ", run.Workloads
                .Where(static result => !result.Completed)
                .Select(static result => string.IsNullOrWhiteSpace(result.FailureReason)
                    ? $"{result.Name} did not complete."
                    : $"{result.Name}: {result.FailureReason}"));
            reasons.Add(details);
            return StabilityVerdict.Rejected;
        }

        if (run.Workloads.Any(result => result.ScoreVariancePercent > policy.MaximumBenchmarkVariancePercent))
        {
            reasons.Add("Benchmark variance exceeds the configured limit.");
            return StabilityVerdict.Exploratory;
        }

        double workloadMinutes = run.Workloads.Sum(static result => result.Duration.TotalMinutes);
        if (workloadMinutes < policy.ShortValidationMinutes)
        {
            reasons.Add("The profile only passed an exploratory test.");
            return StabilityVerdict.Exploratory;
        }

        bool completeSuite = policy.RequiredValidationWorkloads
            .All(kind => run.Workloads.Any(result => result.Kind == kind));
        if (completeSuite && workloadMinutes >= policy.LongValidationMinutes)
        {
            reasons.Add("Long validation suite completed.");
            return StabilityVerdict.Validated;
        }

        reasons.Add("Short validation passed; long mixed-workload validation is still required.");
        return StabilityVerdict.ShortPass;
    }

    private static double[] Values(IEnumerable<GpuTelemetrySample> samples, Func<GpuTelemetrySample, double?> selector)
        => samples.Select(selector).Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();

    private static double? Average(double[] values) => values.Length == 0 ? null : values.Average();
    private static double? Max(double[] values) => values.Length == 0 ? null : values.Max();

    private static double? Percentile(double[] values, double percentile)
    {
        if (values.Length == 0) return null;
        double[] sorted = values.Order().ToArray();
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double Ratio(IEnumerable<GpuTelemetrySample> samples, Func<GpuTelemetrySample, bool> predicate)
    {
        var list = samples.ToArray();
        return list.Length == 0 ? 0 : list.Count(predicate) * 100.0 / list.Length;
    }

    private static bool IsUnderLoad(GpuTelemetrySample sample)
        => sample.GpuUtilizationPercent >= 50 || sample.MemoryUtilizationPercent >= 50;

    private static (TimeSpan Duration, double ExpectedSamples) MeasureTelemetrySpan(
        IReadOnlyList<GpuTelemetrySample> samples,
        int intervalMs)
    {
        if (samples.Count == 0) return (TimeSpan.Zero, 1);
        double durationMs = 0;
        double expectedSamples = 1;
        double maximumContinuousGapMs = intervalMs * 3.0;
        for (int index = 1; index < samples.Count; index++)
        {
            double gapMs = Math.Max(0, (samples[index].Timestamp - samples[index - 1].Timestamp).TotalMilliseconds);
            if (gapMs > maximumContinuousGapMs)
            {
                expectedSamples++;
                continue;
            }

            durationMs += gapMs;
            expectedSamples += gapMs / intervalMs;
        }
        return (TimeSpan.FromMilliseconds(durationMs), expectedSamples);
    }

    private static double IntegrateEnergyWh(IReadOnlyList<GpuTelemetrySample> samples, int intervalMs)
    {
        double wattSeconds = 0;
        double maximumContinuousGapSeconds = intervalMs * 3.0 / 1000.0;
        for (int i = 1; i < samples.Count; i++)
        {
            double? previous = samples[i - 1].PowerAverageW ?? samples[i - 1].PowerInstantW;
            double? current = samples[i].PowerAverageW ?? samples[i].PowerInstantW;
            if (!previous.HasValue || !current.HasValue) continue;
            double seconds = Math.Max(0, (samples[i].Timestamp - samples[i - 1].Timestamp).TotalSeconds);
            if (seconds > maximumContinuousGapSeconds) continue;
            wattSeconds += (previous.Value + current.Value) / 2.0 * seconds;
        }
        return wattSeconds / 3600.0;
    }
}

public static class ProfileEvaluator
{
    public static ProfileComparison Compare(
        TestRun baseline,
        TestRun candidate,
        EvaluationPolicy policy)
    {
        if (!GpuIdentityCompatibility.SameMeasurementEnvironment(baseline.Identity, candidate.Identity))
            throw new InvalidOperationException("Baseline and candidate GPU identities do not match.");

        var baselineSummary = RunAnalyzer.Summarize(baseline, policy);
        var candidateSummary = RunAnalyzer.Summarize(candidate, policy);
        double performanceIndex = CommonPerformanceIndex(baseline, candidate);
        double baselinePower = baselineSummary.AveragePowerW
            ?? throw new InvalidOperationException("Baseline power is missing.");
        double candidatePower = candidateSummary.AveragePowerW
            ?? throw new InvalidOperationException("Candidate power is missing.");
        if (baselinePower <= 0 || candidatePower <= 0)
            throw new InvalidOperationException("Measured power must be greater than 0 W.");
        double baselineTemperature = baselineSummary.P95TemperatureC
            ?? throw new InvalidOperationException("Baseline temperature is missing.");
        double candidateTemperature = candidateSummary.P95TemperatureC
            ?? throw new InvalidOperationException("Candidate temperature is missing.");
        double powerIndex = candidatePower / baselinePower * 100;
        double efficiencyIndex = performanceIndex / powerIndex * 100;
        double temperatureDelta = candidateTemperature - baselineTemperature;

        double performanceGain = performanceIndex - 100;
        double efficiencyGain = efficiencyIndex - 100;
        double thermalGain = -temperatureDelta;
        double balanced = 100
            + performanceGain * policy.PerformanceWeight
            + efficiencyGain * policy.EfficiencyWeight
            + thermalGain * policy.ThermalWeight;
        if (candidateSummary.Verdict is StabilityVerdict.Rejected or StabilityVerdict.InvalidTelemetry)
            balanced = 0;

        return new ProfileComparison
        {
            BaselineRunId = baseline.Id,
            CandidateRunId = candidate.Id,
            PerformanceIndex = performanceIndex,
            PowerIndex = powerIndex,
            EfficiencyIndex = efficiencyIndex,
            TemperatureDeltaC = temperatureDelta,
            BalancedScore = balanced,
            CandidateVerdict = candidateSummary.Verdict,
            MeetsPerformanceFloor = performanceIndex >= policy.MinimumPerformanceRetentionPercent
        };
    }

    private static double CommonPerformanceIndex(TestRun baseline, TestRun candidate)
    {
        string[] baselineKeys = baseline.Workloads.Select(Key).Distinct().Order().ToArray();
        string[] candidateKeys = candidate.Workloads.Select(Key).Distinct().Order().ToArray();
        if (baselineKeys.Length == 0 || !baselineKeys.SequenceEqual(candidateKeys))
            throw new InvalidOperationException("Baseline and candidate workload sets do not match exactly.");
        if (baseline.Workloads.Any(static workload => !workload.Completed || workload.Score <= 0)
            || candidate.Workloads.Any(static workload => !workload.Completed || workload.Score <= 0))
            throw new InvalidOperationException("Every compared workload must be complete with a positive score.");

        var baselineByKey = baseline.Workloads
            .GroupBy(Key)
            .ToDictionary(static group => group.Key, static group => group.Average(item => item.Score));
        var ratios = candidate.Workloads
            .Where(result => baselineByKey.ContainsKey(Key(result)))
            .Select(result => result.Score / baselineByKey[Key(result)] * 100)
            .ToArray();
        if (ratios.Length == 0)
            throw new InvalidOperationException("No common scored workload exists between stock and candidate runs.");
        return ratios.Average();
    }

    private static string Key(WorkloadResult result)
        => $"{result.Kind}|{result.Name}|{result.Version}|{result.ScoreUnit}";
}

public static class GpuIdentityCompatibility
{
    public static bool SameMeasurementEnvironment(GpuIdentity left, GpuIdentity right)
        => left.Uuid.Equals(right.Uuid, StringComparison.OrdinalIgnoreCase)
           && left.DeviceId.Equals(right.DeviceId, StringComparison.OrdinalIgnoreCase)
           && left.SubsystemId.Equals(right.SubsystemId, StringComparison.OrdinalIgnoreCase)
           && left.VbiosVersion.Equals(right.VbiosVersion, StringComparison.OrdinalIgnoreCase)
           && left.DriverVersion.Equals(right.DriverVersion, StringComparison.OrdinalIgnoreCase);
}

public static class RecommendationEngine
{
    public static Recommendation Recommend(
        TestRun? baseline,
        TestRun candidate,
        EvaluationPolicy policy,
        bool trustedSearchEnvelopeAvailable)
    {
        if (baseline == null)
            return new(RecommendationKind.MeasureStock, "A repeatable stock baseline is required first.", 100);

        var summary = RunAnalyzer.Summarize(candidate, policy);
        if (summary.Verdict == StabilityVerdict.Rejected)
            return new(RecommendationKind.RestoreLastStable, "The profile produced direct instability evidence.", 100);
        if (summary.Verdict == StabilityVerdict.InvalidTelemetry)
            return new(RecommendationKind.RepeatRun, "Telemetry coverage is not sufficient to judge this profile.", 100);

        var comparison = ProfileEvaluator.Compare(baseline, candidate, policy);
        if (!comparison.MeetsPerformanceFloor)
            return new(RecommendationKind.IncreaseVoltageOrReduceClock,
                $"Performance retention is {comparison.PerformanceIndex:0.0} %, below the {policy.MinimumPerformanceRetentionPercent:0.0} % floor.", 95);
        if (summary.ThermalLimitTimePercent > 0 || summary.PowerLimitTimePercent > 20)
            return new(RecommendationKind.ReducePowerOrVoltage,
                "The profile spends too much time against a thermal or power limiter.", 90);
        if (summary.Verdict is StabilityVerdict.TelemetryOnly or StabilityVerdict.Exploratory or StabilityVerdict.ShortPass)
            return new(RecommendationKind.ValidateLonger,
                "The result is promising but has not completed the long mixed-workload suite.", 100);
        if (!trustedSearchEnvelopeAvailable)
            return new(RecommendationKind.KeepProfile,
                "The profile is validated, but no trusted model-specific envelope exists for a lower-voltage step.", 100);
        if (comparison.EfficiencyIndex > 103 && comparison.TemperatureDeltaC <= 0)
            return new(RecommendationKind.ExploreLowerVoltage,
                "Efficiency improved without a temperature regression; test one smaller voltage step.", 80, -policy.VoltageStepMv);
        return new(RecommendationKind.KeepProfile, "The validated profile is balanced; no safer next step is justified.", 95);
    }
}
