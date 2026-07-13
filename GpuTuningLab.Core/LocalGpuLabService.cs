using System.Globalization;

namespace GpuTuningLab.Core;

public sealed record GpuLabReadiness(
    bool ReadyForBaseline,
    bool ReadyForProfile,
    GpuIdentity? Identity,
    GpuTelemetrySample? LatestSample,
    WorkloadPackageValidation Package,
    StockStateAssessment? StockState,
    GpuWorkloadPreflightResult? Preflight,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> ProfileBlockingReasons,
    IReadOnlyList<string> Warnings);

public sealed record GpuBaselineProgress(
    int CompletedSteps,
    int TotalSteps,
    int SuiteIndex,
    int SuiteCount,
    string WorkloadName,
    bool WorkloadCompleted);

public sealed record GpuBaselineExecutionResult(
    Guid BatchId,
    IReadOnlyList<TestRun> Runs,
    BaselineValidationResult Validation,
    string SessionPath);

public sealed record GpuBaselineStatus(
    bool Exists,
    Guid? BatchId,
    DateTimeOffset? StartedAt,
    int CompletedSuites,
    BaselineValidationResult? Validation);

public sealed record GpuProfileMeasurementResult(
    TestRun Run,
    RunSummary Summary,
    ProfileComparison? Comparison,
    Recommendation Recommendation,
    string SessionPath,
    GpuProfileSuggestion? NextSuggestion = null,
    string NextSuggestionMessage = "");

public static class LocalGpuWorkloadDefinitions
{
    public static ExternalWorkloadDefinition[] CreateSuite(
        int seconds,
        string d3d11Executable,
        string rayTracingExecutable)
    {
        if (seconds is < 2 or > 120) throw new ArgumentOutOfRangeException(nameof(seconds));
        return
        [
            CreateD3D11("compute", seconds, d3d11Executable),
            CreateD3D11("graphics", seconds, d3d11Executable),
            CreateRayTracing(seconds, rayTracingExecutable),
            CreateD3D11("vram", seconds, d3d11Executable),
            CreateD3D11("transient", seconds, d3d11Executable)
        ];
    }

    public static ExternalWorkloadDefinition CreateD3D11(string mode, int seconds, string executable)
    {
        WorkloadKind kind = mode switch
        {
            "compute" => WorkloadKind.Compute,
            "graphics" => WorkloadKind.Graphics,
            "vram" => WorkloadKind.Vram,
            "transient" => WorkloadKind.Transient,
            _ => throw new ArgumentException("Mode must be compute, graphics, vram or transient.", nameof(mode))
        };
        return new ExternalWorkloadDefinition
        {
            Name = $"GpuTuningLab D3D11 {mode}",
            Version = "prototype-1",
            Kind = kind,
            ExecutablePath = Path.GetFullPath(executable),
            Arguments = ["--mode", mode, "--seconds", seconds.ToString(CultureInfo.InvariantCulture)],
            Timeout = TimeSpan.FromSeconds(seconds + 15),
            ScorePattern = @"Final score:\s*([0-9]+(?:[.,][0-9]+)?)",
            DurationPattern = @"Measured duration:\s*([0-9]+(?:[.,][0-9]+)?)\s*s",
            ScoreUnit = mode switch
            {
                "vram" => "GiB/s",
                "graphics" => "Mpx/s",
                _ => "G element-iterations/s"
            }
        };
    }

    public static ExternalWorkloadDefinition CreateRayTracing(int seconds, string executable) => new()
    {
        Name = "GpuTuningLab DirectX 12 ray tracing",
        Version = "microsoft-simple-lighting-tweakly-1",
        Kind = WorkloadKind.RayTracing,
        ExecutablePath = Path.GetFullPath(executable),
        Arguments = ["--seconds", seconds.ToString(CultureInfo.InvariantCulture), "--warmup", "2"],
        Timeout = TimeSpan.FromSeconds(seconds + 20),
        ScorePattern = @"Final score:\s*([0-9]+(?:[.,][0-9]+)?)",
        DurationPattern = @"elapsed:\s*([0-9]+(?:[.,][0-9]+)?)\s*s",
        ScoreUnit = "M primary-rays/s"
    };
}

public sealed class LocalGpuLabService
{
    private readonly EvaluationPolicy _policy;

    public LocalGpuLabService(EvaluationPolicy? policy = null)
    {
        _policy = policy ?? new EvaluationPolicy { SamplingIntervalMs = 200 };
    }

    public async Task<GpuLabReadiness> InspectAsync(
        string workloadPackageRoot,
        CancellationToken cancellationToken = default)
    {
        WorkloadPackageValidation package = await WorkloadPackageValidator.ValidateAsync(
            workloadPackageRoot,
            cancellationToken).ConfigureAwait(false);
        var profileBlocking = new List<string>(package.Errors);
        var warnings = new List<string>();
        GpuIdentity? identity = null;
        GpuTelemetrySample? latest = null;
        StockStateAssessment? stock = null;
        GpuWorkloadPreflightResult? preflight = null;

        try
        {
            using var nvapi = new NvApiTelemetryEnricher();
            var telemetry = new NvidiaSmiTelemetrySource(nvapi);
            TelemetryCapture capture = await telemetry.CaptureAsync(
                TimeSpan.FromMilliseconds(1_200),
                400,
                cancellationToken).ConfigureAwait(false);
            identity = capture.Identity;
            latest = capture.Samples.LastOrDefault();
            stock = StockStateVerifier.Assess(capture.Samples);
            if (!GpuTuningCompatibility.IsSupported(identity))
                profileBlocking.Add($"GPU model is not supported. Required: {GpuTuningCompatibility.SupportedFamilies}.");
            warnings.AddRange(stock.Warnings);
            warnings.AddRange(capture.Warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            profileBlocking.Add("NVIDIA telemetry is unavailable: " + ex.Message);
        }

        try
        {
            preflight = await GpuWorkloadPreflight.CheckAsync(null, cancellationToken).ConfigureAwait(false);
            if (!preflight.Allowed) profileBlocking.Add(preflight.Reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            profileBlocking.Add("GPU preflight is unavailable: " + ex.Message);
        }

        string[] distinctProfileBlocking = profileBlocking.Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct().ToArray();
        string[] distinctBlocking = distinctProfileBlocking
            .Concat(stock?.BlockingReasons ?? [])
            .Distinct()
            .ToArray();
        return new GpuLabReadiness(
            package.Valid && identity != null && stock?.ObservableStateMatchesStock == true
                          && preflight?.Allowed == true && distinctBlocking.Length == 0,
            package.Valid && identity != null && preflight?.Allowed == true
                          && distinctProfileBlocking.Length == 0,
            identity,
            latest,
            package,
            stock,
            preflight,
            distinctBlocking,
            distinctProfileBlocking,
            warnings.Where(static item => !string.IsNullOrWhiteSpace(item)).Distinct().ToArray());
    }

    public async Task<GpuBaselineExecutionResult> RunStockBaselineAsync(
        string workloadPackageRoot,
        string sessionPath,
        bool stockResetConfirmed,
        int workloadSeconds = 60,
        IProgress<GpuBaselineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!stockResetConfirmed)
            throw new InvalidOperationException("A manual stock reset must be confirmed before the baseline.");
        if (workloadSeconds < _policy.MinimumBaselineWorkloadSeconds || workloadSeconds > 120)
            throw new ArgumentOutOfRangeException(nameof(workloadSeconds));

        GpuLabReadiness readiness = await InspectAsync(workloadPackageRoot, cancellationToken).ConfigureAwait(false);
        if (!readiness.ReadyForBaseline)
            throw new InvalidOperationException(string.Join(" | ", readiness.BlockingReasons));

        ExternalWorkloadDefinition[] definitions = LocalGpuWorkloadDefinitions.CreateSuite(
            workloadSeconds,
            readiness.Package.D3D11WorkloadPath,
            readiness.Package.RayTracingWorkloadPath);
        const int suiteCount = 3;
        int totalSteps = suiteCount * definitions.Length;
        Guid batchId = Guid.NewGuid();
        var profile = new GpuTuningProfile
        {
            Name = "Stock baseline",
            Kind = ProfileKind.Stock,
            AppliedBy = "manual-confirmed-stock",
            VerificationEvidence =
            [
                "User explicitly confirmed a manual stock reset.",
                "Requested and enforced power limits matched the default power limit.",
                "Observed memory clock did not exceed the reported stock maximum clock."
            ]
        };
        LabSession session = await LabStore.LoadAsync(sessionPath, cancellationToken).ConfigureAwait(false);
        var allRuns = session.Runs.ToList();
        var batchRuns = new List<TestRun>(suiteCount);

        using var nvapi = new NvApiTelemetryEnricher();
        var orchestrator = new GpuTestOrchestrator(
            new NvidiaSmiTelemetrySource(nvapi),
            new ExternalWorkloadRunner(new NvidiaGpuContaminationMonitor()),
            new WevtutilEvidenceCollector(),
            _policy);

        for (int suiteIndex = 0; suiteIndex < suiteCount; suiteIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GpuWorkloadPreflightResult preflight = await GpuWorkloadPreflight.CheckAsync(
                null,
                cancellationToken).ConfigureAwait(false);
            if (!preflight.Allowed) throw new InvalidOperationException(preflight.Reason);

            var suiteProgress = new InlineProgress<WorkloadSuiteProgress>(item =>
                progress?.Report(new GpuBaselineProgress(
                    suiteIndex * definitions.Length + item.WorkloadIndex,
                    totalSteps,
                    suiteIndex + 1,
                    suiteCount,
                    item.WorkloadName,
                    item.Completed)));
            TestRun run = await orchestrator.RunSuiteAsync(
                profile,
                definitions,
                cancellationToken,
                suiteProgress).ConfigureAwait(false);
            run = run with { BatchId = batchId };
            batchRuns.Add(run);
            allRuns.Add(run);
            await LabStore.SaveAsync(
                sessionPath,
                session with { Runs = allRuns.ToArray() },
                cancellationToken).ConfigureAwait(false);
            if (run.Workloads.Any(static workload => !workload.Completed)) break;
        }

        BaselineValidationResult validation = BaselineValidator.Validate(batchRuns, _policy);
        return new GpuBaselineExecutionResult(batchId, batchRuns, validation, sessionPath);
    }

    public async Task<GpuBaselineStatus> GetLatestBaselineStatusAsync(
        string sessionPath,
        CancellationToken cancellationToken = default)
    {
        LabSession session = await LabStore.LoadAsync(sessionPath, cancellationToken).ConfigureAwait(false);
        TestRun[] runs = BaselineRunSelector.LatestValidOrLatest(session, _policy);
        if (runs.Length == 0) return new(false, null, null, 0, null);
        return new(true, runs[0].BatchId, runs[0].StartedAt, runs.Length, BaselineValidator.Validate(runs, _policy));
    }

    public async Task<GpuAdviceStatus> GetInitialProfileAdviceAsync(
        string sessionPath,
        string evidencePath,
        GpuIdentity? currentIdentity = null,
        CancellationToken cancellationToken = default)
    {
        LabSession session = await LabStore.LoadAsync(sessionPath, cancellationToken).ConfigureAwait(false);
        TestRun[] baselineRuns = BaselineRunSelector.LatestValidOrLatest(session, _policy);
        BaselineValidationResult validation = BaselineValidator.Validate(baselineRuns, _policy);
        if (!validation.Valid || baselineRuns.Length == 0)
            return new(false, "La mesure stock doit être valide avant de calculer un profil.", null);

        PublishedTuningEvidence[] evidence = await GpuEvidenceStore.LoadPublishedAsync(
            evidencePath,
            cancellationToken).ConfigureAwait(false);
        TestRun baseline = BaselineConsolidator.Consolidate(baselineRuns, _policy);
        if (currentIdentity != null
            && !GpuIdentityCompatibility.SameMeasurementEnvironment(baseline.Identity, currentIdentity))
            return new(false, "Le pilote, le VBIOS ou la carte ne correspond plus à la mesure stock. Refais la référence.", null);
        return GpuProfileAdvisor.BuildInitial(baseline, validation, _policy, evidence);
    }

    public async Task<GpuProfileMeasurementResult> RunProfileMeasurementAsync(
        string workloadPackageRoot,
        string sessionPath,
        GpuTuningProfile profile,
        int workloadSeconds = 60,
        IProgress<GpuBaselineProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? evidencePath = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Kind == ProfileKind.Stock)
            throw new ArgumentException("A candidate measurement cannot use the Stock profile kind.", nameof(profile));
        if (profile.TargetVoltageMv is not (>= 600 and <= 1_200))
            throw new ArgumentOutOfRangeException(nameof(profile), "Target voltage must be between 600 mV and 1 200 mV.");
        if (profile.TargetClockMhz is not (>= 300 and <= 4_000))
            throw new ArgumentOutOfRangeException(nameof(profile), "Target clock must be between 300 MHz and 4 000 MHz.");
        if (workloadSeconds < _policy.MinimumBaselineWorkloadSeconds || workloadSeconds > 120)
            throw new ArgumentOutOfRangeException(nameof(workloadSeconds));

        GpuLabReadiness readiness = await InspectAsync(workloadPackageRoot, cancellationToken).ConfigureAwait(false);
        if (!readiness.ReadyForProfile)
            throw new InvalidOperationException(string.Join(" | ", readiness.ProfileBlockingReasons));

        LabSession session = await LabStore.LoadAsync(sessionPath, cancellationToken).ConfigureAwait(false);
        TestRun[] baselineRuns = BaselineRunSelector.LatestValidOrLatest(session, _policy);
        BaselineValidationResult baselineValidation = BaselineValidator.Validate(baselineRuns, _policy);
        if (!baselineValidation.Valid)
            throw new InvalidOperationException("A valid stock baseline is required: " + string.Join(" | ", baselineValidation.Reasons));
        TestRun baseline = BaselineConsolidator.Consolidate(baselineRuns, _policy);
        if (readiness.Identity == null
            || !GpuIdentityCompatibility.SameMeasurementEnvironment(baseline.Identity, readiness.Identity))
            throw new InvalidOperationException(
                "The valid stock baseline was measured with a different GPU, VBIOS, or driver. Measure stock again.");

        ExternalWorkloadDefinition[] definitions = LocalGpuWorkloadDefinitions.CreateSuite(
            workloadSeconds,
            readiness.Package.D3D11WorkloadPath,
            readiness.Package.RayTracingWorkloadPath);
        using var nvapi = new NvApiTelemetryEnricher();
        var orchestrator = new GpuTestOrchestrator(
            new NvidiaSmiTelemetrySource(nvapi),
            new ExternalWorkloadRunner(new NvidiaGpuContaminationMonitor()),
            new WevtutilEvidenceCollector(),
            _policy);
        var suiteProgress = new InlineProgress<WorkloadSuiteProgress>(item =>
            progress?.Report(new GpuBaselineProgress(
                item.WorkloadIndex,
                definitions.Length,
                1,
                1,
                item.WorkloadName,
                item.Completed)));
        TestRun run = await orchestrator.RunSuiteAsync(
            profile,
            definitions,
            cancellationToken,
            suiteProgress).ConfigureAwait(false);
        run = run with { BatchId = Guid.NewGuid() };
        await LabStore.SaveAsync(
            sessionPath,
            session with { Runs = session.Runs.Append(run).ToArray() },
            cancellationToken).ConfigureAwait(false);

        RunSummary summary = RunAnalyzer.Summarize(run, _policy);
        ProfileComparison? comparison = run.Workloads.All(static workload => workload.Completed)
            ? ProfileEvaluator.Compare(baseline, run, _policy)
            : null;
        Recommendation recommendation = RecommendationEngine.Recommend(
            baseline,
            run,
            _policy,
            trustedSearchEnvelopeAvailable: false);
        GpuAdviceStatus? nextAdvice = null;
        if (!string.IsNullOrWhiteSpace(evidencePath) && File.Exists(evidencePath))
        {
            PublishedTuningEvidence[] evidence = await GpuEvidenceStore.LoadPublishedAsync(
                evidencePath,
                cancellationToken).ConfigureAwait(false);
            nextAdvice = GpuProfileAdvisor.BuildNext(
                baseline,
                run,
                summary,
                comparison,
                _policy,
                evidence);
        }
        return new GpuProfileMeasurementResult(
            run,
            summary,
            comparison,
            recommendation,
            sessionPath,
            nextAdvice?.Suggestion,
            nextAdvice?.Message ?? "");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public static class BaselineRunSelector
{
    public static TestRun[] LatestValidOrLatest(LabSession session, EvaluationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(policy);
        TestRun[][] groups = session.Runs
            .Where(static run => run.Profile.Kind == ProfileKind.Stock && run.BatchId.HasValue)
            .GroupBy(static run => run.BatchId!.Value)
            .OrderByDescending(static group => group.Max(run => run.StartedAt))
            .Select(static group => group.OrderBy(run => run.StartedAt).ToArray())
            .ToArray();
        return groups.FirstOrDefault(group => BaselineValidator.Validate(group, policy).Valid)
               ?? groups.FirstOrDefault()
               ?? [];
    }
}
