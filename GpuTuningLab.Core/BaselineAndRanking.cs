namespace GpuTuningLab.Core;

public static class BaselineValidator
{
    public static BaselineValidationResult Validate(
        IReadOnlyList<TestRun> runs,
        EvaluationPolicy policy,
        string? expectedPackageFingerprint = null)
    {
        var reasons = new List<string>();
        if (runs.Count != 3) reasons.Add($"{runs.Count} stock runs; exactly 3 required.");
        if (runs.Any(static run => run.Profile.Kind != ProfileKind.Stock))
            reasons.Add("Every baseline run must use the Stock profile kind.");
        if (runs.Any(static run => !string.Equals(
                run.Profile.AppliedBy,
                "manual-confirmed-stock",
                StringComparison.OrdinalIgnoreCase)))
            reasons.Add("Every baseline run needs an explicit manual stock-reset confirmation.");
        if (runs.Any(static run => string.IsNullOrWhiteSpace(run.WorkloadPackageFingerprint)))
            reasons.Add("The stock baseline predates workload package fingerprinting and must be measured again.");
        if (runs.Select(static run => run.WorkloadPackageFingerprint ?? "")
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            reasons.Add("The stock runs were measured with different workload packages.");
        if (!string.IsNullOrWhiteSpace(expectedPackageFingerprint)
            && runs.Any(run => !string.Equals(
                run.WorkloadPackageFingerprint,
                expectedPackageFingerprint,
                StringComparison.OrdinalIgnoreCase)))
            reasons.Add("The stock baseline was measured with a different workload package.");
        WorkloadKind[] expectedKinds = policy.RequiredValidationWorkloads
            .Append(WorkloadKind.Compute)
            .Distinct()
            .Order()
            .ToArray();
        foreach (TestRun run in runs)
        {
            WorkloadKind[] actualKinds = run.Workloads.Select(static workload => workload.Kind)
                .Order()
                .ToArray();
            if (!actualKinds.SequenceEqual(expectedKinds))
                reasons.Add("Every stock run must contain compute, graphics, ray tracing, VRAM and transient exactly once.");
            if (run.Workloads.Any(workload =>
                    workload.Duration.TotalSeconds < policy.MinimumBaselineWorkloadSeconds))
                reasons.Add($"Every baseline workload must run for at least {policy.MinimumBaselineWorkloadSeconds} s.");
            foreach (WorkloadResult workload in run.Workloads.Where(static workload =>
                         !workload.Completed || !double.IsFinite(workload.Score) || workload.Score <= 0))
                reasons.Add($"{workload.Name} did not produce a complete positive finite score: {workload.FailureReason}");
            if (run.WorkloadWindows.Count != run.Workloads.Count
                || run.WorkloadWindows.Any(static window =>
                    window.EndedAt <= window.StartedAt || window.SampleCount < 2))
                reasons.Add("Every stock workload needs one valid telemetry window.");
        }
        foreach (TestRun run in runs)
        {
            StockStateAssessment stock = StockStateVerifier.Assess(run.Samples);
            foreach (string reason in stock.BlockingReasons)
                reasons.Add("Observable stock-state check failed: " + reason);
        }
        if (runs.Count > 0)
        {
            GpuIdentity first = runs[0].Identity;
            if (runs.Any(run => !GpuIdentityCompatibility.SameMeasurementEnvironment(first, run.Identity)))
                reasons.Add("GPU identity, subsystem, VBIOS or driver changed between stock runs.");
        }

        var summaries = runs.Select(run => RunAnalyzer.Summarize(run, policy)).ToArray();
        if (summaries.Any(static summary => summary.Verdict is StabilityVerdict.Rejected or StabilityVerdict.InvalidTelemetry))
            reasons.Add("At least one stock run is unstable or has invalid telemetry.");
        double cv = MaximumPerWorkloadVariation(runs, reasons);
        if (cv > policy.MaximumBenchmarkVariancePercent)
            reasons.Add($"Stock score variation is {cv:0.00} %; maximum is {policy.MaximumBenchmarkVariancePercent:0.00} %.");

        return new BaselineValidationResult(reasons.Count == 0, cv, reasons);
    }

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
        TestRun representative = RepresentativeRun(runs);
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
            Identity = representative.Identity,
            Profile = new GpuTuningProfile
            {
                Name = "Consolidated stock baseline",
                Kind = ProfileKind.Stock,
                AppliedBy = "manual-confirmed-stock",
                VerificationEvidence = first.Profile.VerificationEvidence
            },
            WorkloadPackageFingerprint = first.WorkloadPackageFingerprint,
            Samples = representative.Samples,
            Workloads = workloads,
            WorkloadWindows = representative.WorkloadWindows,
            StabilityEvents = runs.SelectMany(static run => run.StabilityEvents).Distinct().ToArray(),
            Notes = $"Scores consolidated from {runs.Count} stock runs; telemetry from representative run {representative.Id}."
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

    private static TestRun RepresentativeRun(IReadOnlyList<TestRun> runs)
    {
        Dictionary<string, double> averageScores = runs
            .SelectMany(static run => run.Workloads)
            .GroupBy(Key)
            .ToDictionary(static group => group.Key, static group => group.Average(item => item.Score));
        return runs.MinBy(run => run.Workloads.Average(workload =>
            Math.Abs(workload.Score / averageScores[Key(workload)] - 1)))!;
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
        bool thermalComparable = left.TemperatureDeltaC.HasValue && right.TemperatureDeltaC.HasValue;
        bool noWorse = left.PerformanceIndex >= right.PerformanceIndex
                       && left.EfficiencyIndex >= right.EfficiencyIndex
                       && (!thermalComparable || left.TemperatureDeltaC <= right.TemperatureDeltaC);
        bool better = left.PerformanceIndex > right.PerformanceIndex
                      || left.EfficiencyIndex > right.EfficiencyIndex
                      || (thermalComparable && left.TemperatureDeltaC < right.TemperatureDeltaC);
        return noWorse && better;
    }
}
