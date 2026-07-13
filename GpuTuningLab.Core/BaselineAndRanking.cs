namespace GpuTuningLab.Core;

public static class BaselineValidator
{
    public static BaselineValidationResult Validate(
        IReadOnlyList<TestRun> runs,
        EvaluationPolicy policy)
    {
        var reasons = new List<string>();
        if (runs.Count < 3) reasons.Add($"{runs.Count} stock runs; 3 required.");
        if (runs.Any(static run => run.Profile.Kind != ProfileKind.Stock))
            reasons.Add("Every baseline run must use the Stock profile kind.");
        if (runs.Any(static run => !run.Profile.AppliedBy.Equals("manual-confirmed-stock", StringComparison.OrdinalIgnoreCase)))
            reasons.Add("Every baseline run needs an explicit manual stock-reset confirmation.");
        if (runs.Any(run => run.Workloads.Any(workload =>
                workload.Duration.TotalSeconds < policy.MinimumBaselineWorkloadSeconds)))
            reasons.Add($"Every baseline workload must run for at least {policy.MinimumBaselineWorkloadSeconds} s.");
        foreach (WorkloadResult workload in runs.SelectMany(static run => run.Workloads)
                     .Where(static workload => !workload.Completed))
            reasons.Add($"{workload.Name} did not complete: {workload.FailureReason}");
        foreach (TestRun run in runs)
        {
            StockStateAssessment stock = StockStateVerifier.Assess(run.Samples);
            foreach (string reason in stock.BlockingReasons)
                reasons.Add("Observable stock-state check failed: " + reason);
        }
        if (runs.Count > 0)
        {
            GpuIdentity first = runs[0].Identity;
            if (runs.Any(run => !SameHardware(first, run.Identity)))
                reasons.Add("GPU identity, subsystem or VBIOS changed between stock runs.");
        }

        var summaries = runs.Select(run => RunAnalyzer.Summarize(run, policy)).ToArray();
        if (summaries.Any(static summary => summary.Verdict is StabilityVerdict.Rejected or StabilityVerdict.InvalidTelemetry))
            reasons.Add("At least one stock run is unstable or has invalid telemetry.");
        double cv = MaximumPerWorkloadVariation(runs, reasons);
        if (cv > policy.MaximumBenchmarkVariancePercent)
            reasons.Add($"Stock score variation is {cv:0.00} %; maximum is {policy.MaximumBenchmarkVariancePercent:0.00} %.");

        return new BaselineValidationResult(reasons.Count == 0, cv, reasons);
    }

    private static bool SameHardware(GpuIdentity left, GpuIdentity right)
        => left.Uuid.Equals(right.Uuid, StringComparison.OrdinalIgnoreCase)
           && left.DeviceId.Equals(right.DeviceId, StringComparison.OrdinalIgnoreCase)
           && left.SubsystemId.Equals(right.SubsystemId, StringComparison.OrdinalIgnoreCase)
           && left.VbiosVersion.Equals(right.VbiosVersion, StringComparison.OrdinalIgnoreCase);

    private static double MaximumPerWorkloadVariation(IReadOnlyList<TestRun> runs, List<string> reasons)
    {
        if (runs.Count == 0 || runs.Any(static run => run.Workloads.Count == 0))
        {
            reasons.Add("Every stock run must contain scored workloads.");
            return double.PositiveInfinity;
        }

        string[] expectedKeys = runs[0].Workloads.Select(Key).Distinct().Order().ToArray();
        if (runs.Any(run => !run.Workloads.Select(Key).Distinct().Order().SequenceEqual(expectedKeys)))
        {
            reasons.Add("Every stock run must use the exact same workload set and score units.");
            return double.PositiveInfinity;
        }

        return expectedKeys.Max(key => CoefficientOfVariation(runs
            .Select(run => run.Workloads.Where(item => Key(item) == key).Average(static item => item.Score))
            .ToArray()));
    }

    private static string Key(WorkloadResult result)
        => $"{result.Kind}|{result.Name}|{result.Version}|{result.ScoreUnit}";

    private static double CoefficientOfVariation(IReadOnlyCollection<double> values)
    {
        if (values.Count < 2) return 0;
        double mean = values.Average();
        if (mean == 0) return double.PositiveInfinity;
        double variance = values.Sum(value => Math.Pow(value - mean, 2)) / (values.Count - 1);
        return Math.Sqrt(variance) / mean * 100;
    }
}

public static class BaselineConsolidator
{
    public static TestRun Consolidate(IReadOnlyList<TestRun> runs, EvaluationPolicy policy)
    {
        BaselineValidationResult validation = BaselineValidator.Validate(runs, policy);
        if (!validation.Valid)
            throw new InvalidOperationException(
                "Stock baseline is not valid: " + string.Join(" | ", validation.Reasons));

        TestRun first = runs[0];
        WorkloadResult[] workloads = first.Workloads.Select(reference =>
        {
            WorkloadResult[] matching = runs
                .SelectMany(static run => run.Workloads)
                .Where(workload => Key(workload) == Key(reference))
                .ToArray();
            return reference with
            {
                Score = matching.Average(static workload => workload.Score),
                Duration = TimeSpan.FromTicks((long)matching.Average(static workload => workload.Duration.Ticks)),
                ScoreVariancePercent = CoefficientOfVariation(matching.Select(static workload => workload.Score).ToArray())
            };
        }).ToArray();

        return new TestRun
        {
            BatchId = first.BatchId,
            StartedAt = runs.Min(static run => run.StartedAt),
            Identity = first.Identity,
            Profile = new GpuTuningProfile
            {
                Name = "Consolidated stock baseline",
                Kind = ProfileKind.Stock,
                AppliedBy = "manual-confirmed-stock",
                VerificationEvidence = first.Profile.VerificationEvidence
            },
            Samples = RebaseSamples(runs, policy.SamplingIntervalMs),
            Workloads = workloads,
            WorkloadWindows = RebaseWindows(runs, policy.SamplingIntervalMs),
            StabilityEvents = runs.SelectMany(static run => run.StabilityEvents).Distinct().ToArray(),
            Notes = $"Consolidated from {runs.Count} stock runs."
        };
    }

    private static string Key(WorkloadResult result)
        => $"{result.Kind}|{result.Name}|{result.Version}|{result.ScoreUnit}";

    private static double CoefficientOfVariation(IReadOnlyCollection<double> values)
    {
        if (values.Count < 2) return 0;
        double mean = values.Average();
        if (mean == 0) return double.PositiveInfinity;
        double variance = values.Sum(value => Math.Pow(value - mean, 2)) / (values.Count - 1);
        return Math.Sqrt(variance) / mean * 100;
    }

    private static IReadOnlyList<GpuTelemetrySample> RebaseSamples(
        IReadOnlyList<TestRun> runs,
        int intervalMs)
    {
        var result = new List<GpuTelemetrySample>();
        DateTimeOffset cursor = runs.Min(static run => run.StartedAt);
        foreach (TestRun run in runs.OrderBy(static run => run.StartedAt))
        {
            GpuTelemetrySample[] samples = run.Samples.OrderBy(static sample => sample.Timestamp).ToArray();
            if (samples.Length == 0) continue;
            DateTimeOffset sourceStart = samples[0].Timestamp;
            result.AddRange(samples.Select(sample => sample with
            {
                Timestamp = cursor + (sample.Timestamp - sourceStart)
            }));
            cursor = result[^1].Timestamp.AddMilliseconds(intervalMs);
        }
        return result;
    }

    private static IReadOnlyList<WorkloadTelemetryWindow> RebaseWindows(
        IReadOnlyList<TestRun> runs,
        int intervalMs)
    {
        var result = new List<WorkloadTelemetryWindow>();
        DateTimeOffset cursor = runs.Min(static run => run.StartedAt);
        foreach (TestRun run in runs.OrderBy(static run => run.StartedAt))
        {
            WorkloadTelemetryWindow[] windows = run.WorkloadWindows
                .OrderBy(static window => window.StartedAt)
                .ToArray();
            if (windows.Length == 0) continue;
            DateTimeOffset sourceStart = windows[0].StartedAt;
            result.AddRange(windows.Select(window => window with
            {
                StartedAt = cursor + (window.StartedAt - sourceStart),
                EndedAt = cursor + (window.EndedAt - sourceStart)
            }));
            cursor = result[^1].EndedAt.AddMilliseconds(intervalMs);
        }
        return result;
    }
}

public static class ProfileRanker
{
    public static IReadOnlyList<ProfileRankingRow> Rank(
        TestRun baseline,
        IReadOnlyList<TestRun> candidates,
        EvaluationPolicy policy)
    {
        var raw = candidates.Select(run => new
        {
            Run = run,
            Summary = RunAnalyzer.Summarize(run, policy),
            Comparison = ProfileEvaluator.Compare(baseline, run, policy)
        }).ToArray();

        var rows = raw.Select(item => new
        {
            item.Run,
            item.Summary,
            item.Comparison,
            Pareto = item.Summary.Verdict is not (StabilityVerdict.Rejected or StabilityVerdict.InvalidTelemetry)
                     && !raw.Any(other => other.Run.Id != item.Run.Id && Dominates(other.Comparison, item.Comparison))
        })
        .OrderByDescending(static item => item.Pareto)
        .ThenByDescending(static item => item.Comparison.BalancedScore)
        .ThenByDescending(static item => item.Comparison.EfficiencyIndex)
        .ToArray();

        return rows.Select((item, index) => new ProfileRankingRow(
            item.Run,
            item.Summary,
            item.Comparison,
            item.Pareto,
            index + 1)).ToArray();
    }

    private static bool Dominates(ProfileComparison left, ProfileComparison right)
    {
        if (left.CandidateVerdict is StabilityVerdict.Rejected or StabilityVerdict.InvalidTelemetry) return false;
        bool noWorse = left.PerformanceIndex >= right.PerformanceIndex
                       && left.EfficiencyIndex >= right.EfficiencyIndex
                       && left.TemperatureDeltaC <= right.TemperatureDeltaC;
        bool better = left.PerformanceIndex > right.PerformanceIndex
                      || left.EfficiencyIndex > right.EfficiencyIndex
                      || left.TemperatureDeltaC < right.TemperatureDeltaC;
        return noWorse && better;
    }
}
