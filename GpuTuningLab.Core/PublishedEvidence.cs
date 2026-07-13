namespace GpuTuningLab.Core;

public static class PublishedEvidenceValidator
{
    private const double MinimumPerformanceRetentionPercent = 97;
    private const double EquivalentPerformancePercent = 99;
    private const double MinimumValidationSeconds = 600;
    private static readonly HashSet<string> Outcomes = new(
        ["reported-stable", "short-pass", "failed", "failed-later", "rejected"],
        StringComparer.OrdinalIgnoreCase);

    public static PublishedEvidenceReview Review(PublishedTuningEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var errors = new List<string>();
        var eligibility = new List<string>();
        if (string.IsNullOrWhiteSpace(evidence.EvidenceId)) errors.Add("Evidence ID is missing.");
        if (string.IsNullOrWhiteSpace(evidence.IndependentUnitId)) errors.Add("Independent unit ID is missing.");
        if (string.IsNullOrWhiteSpace(evidence.Model)) errors.Add("GPU model is missing.");
        if (string.IsNullOrWhiteSpace(evidence.BoardModel)) errors.Add("Board model is missing.");
        if (!evidence.FormFactor.Equals("desktop", StringComparison.OrdinalIgnoreCase))
            errors.Add("Only desktop evidence is accepted in this catalog.");
        if (!Uri.TryCreate(evidence.Source.Url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
            errors.Add("Source URL is invalid.");
        if (string.IsNullOrWhiteSpace(evidence.Source.Publisher)) errors.Add("Source publisher is missing.");
        if (string.IsNullOrWhiteSpace(evidence.Benchmark)) errors.Add("Benchmark protocol is missing.");
        string outcome = string.IsNullOrWhiteSpace(evidence.Outcome) ? "reported-stable" : evidence.Outcome;
        if (!Outcomes.Contains(outcome)) errors.Add($"Unknown evidence outcome: {outcome}");

        double? performanceIndex = Ratio(evidence.Tuned.Score, evidence.Stock.Score);
        double? powerIndex = Ratio(evidence.Tuned.PowerW, evidence.Stock.PowerW);
        double? efficiencyIndex = performanceIndex.HasValue && powerIndex > 0
            ? performanceIndex.Value / powerIndex.Value * 100
            : null;
        bool comparableScore = performanceIndex.HasValue
            && !string.IsNullOrWhiteSpace(evidence.Stock.ScoreUnit)
            && evidence.Stock.ScoreUnit.Equals(evidence.Tuned.ScoreUnit, StringComparison.OrdinalIgnoreCase);
        bool longEnough = evidence.DurationSeconds >= MinimumValidationSeconds;
        bool thermalPair = evidence.Stock.TemperatureC.HasValue && evidence.Tuned.TemperatureC.HasValue;
        bool improvesThermals = thermalPair
            && evidence.Tuned.TemperatureC!.Value < evidence.Stock.TemperatureC!.Value;
        bool maintainsPerformance = performanceIndex >= EquivalentPerformancePercent;
        bool shortTermStability = evidence.StablePasses >= 3 || longEnough;
        bool longTermStability = evidence.LongTermStabilityReported;
        bool exactBoard = !evidence.BoardModel.Contains("not reported", StringComparison.OrdinalIgnoreCase);

        bool voltageMethod = string.Equals(evidence.Method, "vf-curve", StringComparison.OrdinalIgnoreCase);
        bool powerMethod = string.Equals(evidence.Method, "power-limit", StringComparison.OrdinalIgnoreCase);
        bool mixedMethod = string.Equals(evidence.Method, "mixed", StringComparison.OrdinalIgnoreCase);
        if (!voltageMethod && !powerMethod && !mixedMethod) errors.Add($"Unknown tuning method: {evidence.Method}");
        if (voltageMethod && (!evidence.Tuned.VoltageMv.HasValue || !evidence.Tuned.ClockMhz.HasValue))
            errors.Add("Voltage/frequency evidence needs an explicit tuned voltage and clock.");
        if (powerMethod && !evidence.Tuned.PowerLimitPercent.HasValue)
            errors.Add("Power-limit evidence needs an explicit percentage.");
        if (mixedMethod && (!evidence.Tuned.VoltageMv.HasValue
                            || !evidence.Tuned.ClockMhz.HasValue
                            || !evidence.Tuned.PowerLimitPercent.HasValue))
            errors.Add("Mixed evidence needs voltage, clock and power-limit values.");

        if (!comparableScore) eligibility.Add("Comparable stock and tuned scores are missing.");
        if (!powerIndex.HasValue) eligibility.Add("Comparable stock and tuned power values are missing.");
        if (!thermalPair) eligibility.Add("Comparable stock and tuned temperatures are missing.");
        if (!exactBoard) eligibility.Add("Exact board model was not reported.");
        if (!longEnough) eligibility.Add($"Validation shorter than {MinimumValidationSeconds:0} s or not reported.");
        if (performanceIndex.HasValue && performanceIndex < MinimumPerformanceRetentionPercent)
            eligibility.Add($"Performance retention is {performanceIndex:0.0} %, below {MinimumPerformanceRetentionPercent:0.0} %.");

        bool eligibleCommon = errors.Count == 0
            && eligibility.Count == 0
            && comparableScore
            && powerIndex.HasValue
            && thermalPair
            && exactBoard
            && longEnough
            && performanceIndex >= MinimumPerformanceRetentionPercent;
        bool voltageEligible = eligibleCommon && voltageMethod;
        bool powerEligible = eligibleCommon && powerMethod;
        bool positiveOutcome = string.Equals(outcome, "reported-stable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcome, "short-pass", StringComparison.OrdinalIgnoreCase);
        bool advisoryEvidence = comparableScore
            || evidence.DurationSeconds >= MinimumValidationSeconds
            || evidence.StablePasses >= 3
            || string.Equals(evidence.Source.EvidenceType, "direct-review", StringComparison.OrdinalIgnoreCase);
        bool advisoryEligible = errors.Count == 0
            && positiveOutcome
            && (voltageMethod || mixedMethod)
            && evidence.Tuned.VoltageMv.HasValue
            && evidence.Tuned.ClockMhz.HasValue
            && (!performanceIndex.HasValue || performanceIndex >= MinimumPerformanceRetentionPercent)
            && advisoryEvidence;

        return new PublishedEvidenceReview(
            evidence.EvidenceId,
            errors.Count == 0,
            advisoryEligible,
            voltageEligible,
            powerEligible,
            performanceIndex,
            powerIndex,
            efficiencyIndex,
            improvesThermals,
            maintainsPerformance,
            shortTermStability,
            longTermStability,
            errors.Distinct().ToArray(),
            eligibility.Distinct().ToArray());
    }

    public static IReadOnlyList<string> ValidateRegistry(IReadOnlyList<PublishedTuningEvidence> entries)
    {
        var errors = entries
            .GroupBy(static item => item.EvidenceId, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => $"Duplicate evidence ID: {group.Key}")
            .ToList();
        foreach (PublishedTuningEvidence entry in entries)
        {
            PublishedEvidenceReview review = Review(entry);
            if (!review.Valid)
                errors.AddRange(review.ValidationErrors.Select(reason => $"{entry.EvidenceId}: {reason}"));
        }
        return errors;
    }

    private static double? Ratio(double? candidate, double? baseline)
        => candidate > 0 && baseline > 0 ? candidate.Value / baseline.Value * 100 : null;
}
