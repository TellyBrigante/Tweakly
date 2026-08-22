namespace GpuTuningLab.Core;

public sealed record UndervoltTransitionAssessment(
    bool Allowed,
    IReadOnlyList<string> BlockingReasons);

public static class UndervoltProtocol
{
    public const int ConfirmationDurationMinutes = 20;
    public const int ConfirmationWorkloadSeconds = 240;
    public const int LongValidationDurationMinutes = 60;
    public const int LongValidationWorkloadSeconds = 720;
    public const int MaximumClockStepMhz = 30;

    public static IReadOnlyList<string> ValidateCandidate(GpuTuningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var reasons = new List<string>();
        if (profile.Kind != ProfileKind.Undervolt)
            reasons.Add("The Tweakly undervolt protocol only accepts Undervolt profiles.");
        if (profile.TargetVoltageMv == null || profile.TargetClockMhz == null)
            reasons.Add("Target voltage and target clock must both be explicit.");
        if ((profile.CoreOffsetMhz ?? 0) != 0)
            reasons.Add("A separate core offset cannot be mixed with a voltage/frequency point.");
        if ((profile.MemoryOffsetMhz ?? 0) != 0)
            reasons.Add("GPU memory must remain at stock while validating a core undervolt.");
        if (!profile.PowerLimitPercent.HasValue
            || Math.Abs(profile.PowerLimitPercent.Value - 100) > 0.01)
            reasons.Add("Power Limit must remain at 100 % while validating a core undervolt.");

        return reasons.ToArray();
    }

    public static bool CanCalculateNextProfile(StabilityVerdict verdict)
        => verdict is StabilityVerdict.ShortPass or StabilityVerdict.Validated;

    public static UndervoltTransitionAssessment AssessTransition(
        TestRun baseline,
        GpuTuningProfile requested,
        IReadOnlyList<TestRun> history,
        EvaluationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(policy);

        var reasons = new List<string>(ValidateCandidate(requested));
        int? stockVoltageMv = AverageLoadedVoltageMv(baseline);
        RunSummary stockSummary = RunAnalyzer.Summarize(baseline, policy);
        int? stockClockMhz = stockSummary.P05CoreClockMhz.HasValue
            ? (int)Math.Floor(stockSummary.P05CoreClockMhz.Value)
            : null;

        if (!stockVoltageMv.HasValue)
            reasons.Add("The loaded stock voltage is unavailable; a safe voltage step cannot be proven.");
        if (!stockClockMhz.HasValue)
            reasons.Add("The loaded stock clock is unavailable; the requested frequency cannot be bounded.");
        if (requested.TargetVoltageMv is int requestedVoltage && stockVoltageMv is int stockVoltage)
        {
            if (requestedVoltage >= stockVoltage)
                reasons.Add($"Requested voltage {requestedVoltage} mV is not below measured stock voltage {stockVoltage} mV.");
        }
        if (requested.TargetClockMhz is int requestedClock && stockClockMhz is int stockClock
            && requestedClock > stockClock)
        {
            reasons.Add($"Requested clock {requestedClock} MHz exceeds the measured sustained stock floor {stockClock} MHz.");
        }

        TestRun[] candidates = history
            .Where(static run => run.Profile.Kind != ProfileKind.Stock)
            .Where(run => GpuIdentityCompatibility.SameMeasurementEnvironment(baseline.Identity, run.Identity))
            .Where(run => SamePackage(baseline, run))
            .OrderBy(static run => run.StartedAt)
            .ToArray();
        TestRun? latest = candidates.LastOrDefault();
        if (latest == null)
        {
            if (requested.TargetVoltageMv is int initialVoltage && stockVoltageMv is int initialStockVoltage
                && initialStockVoltage - initialVoltage > policy.VoltageStepMv)
            {
                reasons.Add(
                    $"The first voltage step is {initialStockVoltage - initialVoltage} mV; " +
                    $"the protocol maximum is {policy.VoltageStepMv} mV.");
            }
            return Result(reasons);
        }

        IReadOnlyList<string> previousShapeErrors = ValidateCandidate(latest.Profile);
        if (previousShapeErrors.Count > 0)
        {
            reasons.Add(
                "The latest stored profile predates or violates the core-undervolt protocol. " +
                "Return the GPU to stock and rebuild the stock reference before continuing.");
            return Result(reasons);
        }

        RunSummary latestSummary = RunAnalyzer.Summarize(latest, policy);
        ProfileApplicationAssessment latestApplication =
            ProfileApplicationVerifier.Assess(baseline, latest, policy);
        bool sameProfile = SameTuningValues(requested, latest.Profile);

        if (latestSummary.Verdict is StabilityVerdict.Rejected or StabilityVerdict.InvalidTelemetry)
        {
            TestRun? lastValidated = candidates
                .Take(candidates.Length - 1)
                .Reverse()
                .FirstOrDefault(run => IsValidatedRecoveryPoint(baseline, run, policy));
            if (lastValidated == null)
            {
                reasons.Add(
                    "The latest undervolt failed and no long-validated recovery profile exists. " +
                    "Return to stock and rebuild the stock reference before another undervolt.");
            }
            else if (!SameTuningValues(requested, lastValidated.Profile))
            {
                reasons.Add(
                    "The latest undervolt failed. Only the exact last long-validated profile may be measured as recovery.");
            }
            return Result(reasons);
        }

        if (!latestApplication.Verified || !CanCalculateNextProfile(latestSummary.Verdict))
        {
            if (!sameProfile)
            {
                reasons.Add(
                    "The latest profile is still exploratory or was not observed correctly. " +
                    "Repeat the exact same voltage and frequency before changing a value.");
            }
            return Result(reasons);
        }

        if (sameProfile)
            return Result(reasons);

        int voltageDelta = requested.TargetVoltageMv!.Value - latest.Profile.TargetVoltageMv!.Value;
        int clockDelta = requested.TargetClockMhz!.Value - latest.Profile.TargetClockMhz!.Value;
        if (Math.Abs(voltageDelta) > policy.VoltageStepMv)
        {
            reasons.Add(
                $"Voltage changed by {Math.Abs(voltageDelta)} mV; " +
                $"the protocol maximum is {policy.VoltageStepMv} mV.");
        }
        if (clockDelta > 0)
            reasons.Add("The undervolt protocol does not allow increasing core frequency above the measured profile.");
        if (Math.Abs(clockDelta) > MaximumClockStepMhz)
        {
            reasons.Add(
                $"Core clock changed by {Math.Abs(clockDelta)} MHz; " +
                $"the protocol maximum is {MaximumClockStepMhz} MHz.");
        }
        if (voltageDelta != 0 && clockDelta != 0)
            reasons.Add("Voltage and core frequency cannot change in the same protocol step.");

        if (voltageDelta < 0)
        {
            ProfileComparison comparison = ProfileEvaluator.Compare(baseline, latest, policy);
            bool lowerStepProven = comparison.MeetsPerformanceFloor
                                   && comparison.PerformanceIndex >= 99
                                   && comparison.EfficiencyIndex >= 103
                                   && comparison.TemperatureDeltaC.HasValue
                                   && comparison.TemperatureDeltaC <= 0
                                   && latestSummary.ThermalLimitTimePercent <= 0
                                   && latestSummary.PowerLimitTimePercent <= 20;
            if (!lowerStepProven)
            {
                reasons.Add(
                    "A lower-voltage step requires at least 99 % performance retention, " +
                    "3 % efficiency gain, no temperature regression and no thermal limiting.");
            }
        }

        return Result(reasons);
    }

    private static bool IsValidatedRecoveryPoint(
        TestRun baseline,
        TestRun candidate,
        EvaluationPolicy policy)
    {
        try
        {
            RunSummary summary = RunAnalyzer.Summarize(candidate, policy);
            if (summary.Verdict != StabilityVerdict.Validated)
                return false;
            if (!ProfileApplicationVerifier.Assess(baseline, candidate, policy).Verified)
                return false;
            return ProfileEvaluator.Compare(baseline, candidate, policy).MeetsPerformanceFloor;
        }
        catch
        {
            return false;
        }
    }

    private static bool SamePackage(TestRun left, TestRun right)
        => !string.IsNullOrWhiteSpace(left.WorkloadPackageFingerprint)
           && string.Equals(
               left.WorkloadPackageFingerprint,
               right.WorkloadPackageFingerprint,
               StringComparison.OrdinalIgnoreCase);

    private static bool SameTuningValues(GpuTuningProfile left, GpuTuningProfile right)
        => left.TargetVoltageMv == right.TargetVoltageMv
           && left.TargetClockMhz == right.TargetClockMhz
           && (left.CoreOffsetMhz ?? 0) == (right.CoreOffsetMhz ?? 0)
           && (left.MemoryOffsetMhz ?? 0) == (right.MemoryOffsetMhz ?? 0)
           && left.PowerLimitPercent.HasValue
           && right.PowerLimitPercent.HasValue
           && Math.Abs(left.PowerLimitPercent.Value - right.PowerLimitPercent.Value) <= 0.01;

    private static int? AverageLoadedVoltageMv(TestRun run)
    {
        double[] values = run.Samples
            .Where(static sample => sample.GpuUtilizationPercent >= 50 && sample.VoltageV > 0)
            .Select(static sample => sample.VoltageV!.Value * 1_000)
            .ToArray();
        return values.Length == 0
            ? null
            : (int)Math.Round(values.Average() / 5.0) * 5;
    }

    private static UndervoltTransitionAssessment Result(List<string> reasons)
    {
        string[] distinct = reasons
            .Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new UndervoltTransitionAssessment(distinct.Length == 0, distinct);
    }
}