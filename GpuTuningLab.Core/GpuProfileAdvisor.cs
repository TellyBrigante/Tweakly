namespace GpuTuningLab.Core;

public static class GpuProfileAdvisor
{
    private const double MinimumClockRetentionPercent = 97;
    private const int MinimumAdvisoryPoints = 3;
    private const int MinimumIndependentUnits = 2;
    private const int MinimumIndependentSources = 2;

    public static GpuAdviceStatus BuildInitial(
        TestRun baseline,
        BaselineValidationResult baselineValidation,
        EvaluationPolicy policy,
        IReadOnlyList<PublishedTuningEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(baselineValidation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evidence);

        if (!baselineValidation.Valid)
            return new(false, "La mesure stock doit être valide avant de calculer un profil.", null);

        PublishedTuningEvidence[] sameModel = evidence
            .Where(item => GpuReferenceMatcher.SameModel(item.Model, baseline.Identity.Name))
            .ToArray();
        PublishedTuningEvidence[] usable = sameModel
            .Where(item => PublishedEvidenceValidator.Review(item).EligibleForAdvisory)
            .ToArray();
        int failedPoints = sameModel.Count(IsFailure);
        if (usable.Length == 0)
            return new(false, "Aucune mesure publique exploitable n'est disponible pour ce modèle.", null);

        PublishedTuningEvidence[] coreIsolated = usable.Where(IsCoreIsolated).ToArray();
        if (!HasMinimumCoverage(usable, coreIsolated, out string coverageMessage))
            return new(false, coverageMessage, null);
        PublishedTuningEvidence[] advisoryPool = coreIsolated.Length > 0 ? coreIsolated : usable;

        RunSummary stock = RunAnalyzer.Summarize(baseline, policy);
        double? stockClock = stock.P05CoreClockMhz ?? stock.AverageCoreClockMhz;
        double minimumUsefulClock = stockClock.GetValueOrDefault() * MinimumClockRetentionPercent / 100.0;

        PublishedTuningEvidence[] usefulClock = stockClock.HasValue
            ? advisoryPool.Where(item => item.Tuned.ClockMhz >= minimumUsefulClock).ToArray()
            : advisoryPool;
        PublishedTuningEvidence[] candidates = usefulClock.Length > 0 ? usefulClock : advisoryPool;
        int? stockVoltage = AverageLoadedVoltageMv(baseline);
        if (stockVoltage.HasValue)
        {
            candidates = candidates
                .Where(item => item.Tuned.VoltageMv < stockVoltage.Value)
                .ToArray();
            if (candidates.Length == 0)
                return new(false, "La tension stock mesurée est déjà au niveau ou sous les points publics retenus. Aucun point de départ n'est proposé.", null);
        }
        PublishedTuningEvidence anchor = candidates
            .OrderByDescending(static item => item.Tuned.VoltageMv)
            .ThenByDescending(static item => item.Tuned.ClockMhz)
            .First();

        int publicVoltage = anchor.Tuned.VoltageMv!.Value;
        int publicClock = anchor.Tuned.ClockMhz!.Value;
        int targetVoltage = stockVoltage.HasValue
            ? Math.Max(publicVoltage, RoundDown(stockVoltage.Value - 50, 25))
            : publicVoltage;
        if (stockVoltage.HasValue)
            targetVoltage = Math.Min(targetVoltage, stockVoltage.Value - 5);
        targetVoltage = Math.Min(targetVoltage, 1_200);
        int targetClock = stockClock.HasValue
            ? Math.Min(publicClock, RoundDown((int)Math.Floor(stockClock.Value), 15))
            : publicClock;

        int units = advisoryPool.Select(static item => item.IndependentUnitId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int sources = advisoryPool.Select(static item => item.Source.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        string confidence = coreIsolated.Length == 0
            ? "Préliminaire"
            : units >= 5 && sources >= 3
            ? "Élevée"
            : units >= 3 && sources >= 2
                ? "Modérée"
                : "Préliminaire";

        var profile = new GpuTuningProfile
        {
            Name = $"Point de départ {ShortModelName(baseline.Identity.Name)}",
            Kind = ProfileKind.Undervolt,
            TargetVoltageMv = targetVoltage,
            TargetClockMhz = targetClock,
            MemoryOffsetMhz = 0,
            PowerLimitPercent = 100,
            AppliedBy = "manual-public-evidence-seed",
            VerificationEvidence =
            [
                $"Stock clock measured by Tweakly: {stockClock:0} MHz.",
                $"Public anchor: {publicVoltage} mV at {publicClock} MHz.",
                $"Public evidence: {units} independent unit(s), {sources} independent source(s)."
            ]
        };
        string summary = stockClock.HasValue
            ? $"Le clock stock utile est de {stockClock:0} MHz. Le premier essai reste au-dessus de la zone publique la plus agressive et conserve la mémoire à stock."
            : "Le premier essai utilise le point public le plus prudent et conserve la mémoire à stock.";
        if (coreIsolated.Length == 0)
            summary += " Les mesures publiques disponibles mélangent le réglage du cœur avec la mémoire ou le Power Limit ; ce point reste donc préliminaire.";
        return new(true, summary, new GpuProfileSuggestion
        {
            Profile = profile,
            Confidence = confidence,
            Summary = summary,
            IndependentUnits = units,
            IndependentSources = sources,
            SupportingPoints = advisoryPool.Length,
            ExcludedFailurePoints = failedPoints,
            PublicAnchorVoltageMv = publicVoltage,
            PublicAnchorClockMhz = publicClock,
            StockClockMhz = stockClock,
            SourceUrls = advisoryPool.Select(static item => item.Source.Url)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        });
    }

    public static GpuAdviceStatus BuildNext(
        TestRun baseline,
        TestRun candidate,
        RunSummary summary,
        ProfileComparison? comparison,
        EvaluationPolicy policy,
        IReadOnlyList<PublishedTuningEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evidence);

        if (comparison == null || candidate.Workloads.Any(static workload => !workload.Completed))
            return new(false, "Le test n'est pas complet. Aucun nouveau profil n'est calculé.", null);
        if (summary.Verdict is StabilityVerdict.Rejected or StabilityVerdict.InvalidTelemetry)
            return new(false, "Le profil n'est pas exploitable. Reviens au dernier profil stable.", null);
        if (!candidate.Profile.TargetVoltageMv.HasValue || !candidate.Profile.TargetClockMhz.HasValue)
            return new(false, "Le profil mesuré ne contient pas de tension et de fréquence exploitables.", null);

        PublishedTuningEvidence[] sameModel = evidence
            .Where(item => GpuReferenceMatcher.SameModel(item.Model, baseline.Identity.Name))
            .ToArray();
        PublishedTuningEvidence[] usable = sameModel
            .Where(item => PublishedEvidenceValidator.Review(item).EligibleForAdvisory)
            .ToArray();
        if (usable.Length == 0)
            return new(false, "Aucune base publique exploitable n'est disponible pour calculer le palier suivant.", null);
        PublishedTuningEvidence[] coreIsolated = usable.Where(IsCoreIsolated).ToArray();
        if (!HasMinimumCoverage(usable, coreIsolated, out string coverageMessage))
            return new(false, coverageMessage, null);

        int currentVoltage = candidate.Profile.TargetVoltageMv.Value;
        int currentClock = candidate.Profile.TargetClockMhz.Value;
        int nextVoltage = currentVoltage;
        int nextClock = currentClock;
        string summaryText;

        if (!comparison.MeetsPerformanceFloor)
        {
            nextVoltage = Math.Min(1_200, currentVoltage + policy.VoltageStepMv);
            summaryText = $"La performance conserve {comparison.PerformanceIndex:0.0} % du stock. Le prochain essai remonte la tension de {policy.VoltageStepMv} mV sans changer le clock.";
        }
        else if (summary.ThermalLimitTimePercent > 0 || summary.PowerLimitTimePercent > 20)
        {
            nextClock = Math.Max(300, currentClock - 30);
            summaryText = "Le GPU reste trop souvent limité. Le prochain essai baisse le clock de 30 MHz sans réduire la tension.";
        }
        else if (comparison.PerformanceIndex >= 99
                 && comparison.EfficiencyIndex >= 103
                 && comparison.TemperatureDeltaC <= 0)
        {
            int publicFloor = usable.Min(static item => item.Tuned.VoltageMv!.Value);
            nextVoltage = Math.Max(publicFloor, currentVoltage - policy.VoltageStepMv);
            if (nextVoltage == currentVoltage)
                return new(false, "Le profil atteint déjà le plus bas palier positif conservé dans la base publique. Valide-le plus longtemps avant toute autre baisse.", null);
            summaryText = $"Le profil garde {comparison.PerformanceIndex:0.0} % des performances, améliore le rendement de {comparison.EfficiencyIndex - 100:+0.0;-0.0;0.0} % et ne chauffe pas plus. Le prochain essai baisse la tension de {currentVoltage - nextVoltage} mV.";
        }
        else
        {
            return new(false, "Le résultat est équilibré, mais il ne justifie pas un palier plus agressif. Garde ces valeurs pour une validation plus longue.", null);
        }

        int units = usable.Select(static item => item.IndependentUnitId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int sources = usable.Select(static item => item.Source.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var profile = new GpuTuningProfile
        {
            Name = $"Essai {nextVoltage} mV",
            Kind = ProfileKind.Undervolt,
            TargetVoltageMv = nextVoltage,
            TargetClockMhz = nextClock,
            MemoryOffsetMhz = candidate.Profile.MemoryOffsetMhz ?? 0,
            PowerLimitPercent = candidate.Profile.PowerLimitPercent ?? 100,
            AppliedBy = "manual-measured-iteration",
            VerificationEvidence =
            [
                $"Measured performance retention: {comparison.PerformanceIndex:0.0}%.",
                $"Measured efficiency index: {comparison.EfficiencyIndex:0.0}%.",
                $"Measured temperature delta: {comparison.TemperatureDeltaC:+0.0;-0.0;0.0} C."
            ]
        };
        return new(true, summaryText, new GpuProfileSuggestion
        {
            Profile = profile,
            Confidence = "Mesurée",
            Summary = summaryText,
            IndependentUnits = units,
            IndependentSources = sources,
            SupportingPoints = usable.Length,
            ExcludedFailurePoints = sameModel.Count(IsFailure),
            PublicAnchorVoltageMv = usable.Max(static item => item.Tuned.VoltageMv),
            PublicAnchorClockMhz = usable.Max(static item => item.Tuned.ClockMhz),
            StockClockMhz = RunAnalyzer.Summarize(baseline, policy).P05CoreClockMhz,
            SourceUrls = usable.Select(static item => item.Source.Url)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        });
    }

    private static int? AverageLoadedVoltageMv(TestRun run)
    {
        double[] values = run.Samples
            .Where(static sample => sample.GpuUtilizationPercent >= 50 && sample.VoltageV > 0)
            .Select(static sample => sample.VoltageV!.Value * 1_000)
            .ToArray();
        return values.Length == 0 ? null : (int)Math.Round(values.Average() / 5.0) * 5;
    }

    private static bool IsFailure(PublishedTuningEvidence item)
        => string.Equals(item.Outcome, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(item.Outcome, "failed-later", StringComparison.OrdinalIgnoreCase)
           || string.Equals(item.Outcome, "rejected", StringComparison.OrdinalIgnoreCase);

    private static bool IsCoreIsolated(PublishedTuningEvidence item)
        => string.Equals(item.Method, "vf-curve", StringComparison.OrdinalIgnoreCase)
           && item.Tuned.MemoryOffsetMhz == 0
           && item.Tuned.PowerLimitPercent.HasValue
           && Math.Abs(item.Tuned.PowerLimitPercent.Value - 100) <= 0.5;

    private static bool HasMinimumCoverage(
        IReadOnlyList<PublishedTuningEvidence> usable,
        IReadOnlyList<PublishedTuningEvidence> coreIsolated,
        out string message)
    {
        int units = usable.Select(static item => item.IndependentUnitId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int sources = usable.Select(static item => item.Source.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        bool enough = usable.Count >= MinimumAdvisoryPoints
            && units >= MinimumIndependentUnits
            && sources >= MinimumIndependentSources
            && coreIsolated.Count > 0;
        message = enough
            ? ""
            : $"Base publique insuffisante pour conseiller ce modèle : {usable.Count} point(s) exploitable(s), {units} carte(s), {sources} source(s) et {coreIsolated.Count} point(s) cœur isolé(s).";
        return enough;
    }

    private static int RoundDown(int value, int step) => Math.Max(step, value / step * step);

    private static string ShortModelName(string value)
        => value.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
}
