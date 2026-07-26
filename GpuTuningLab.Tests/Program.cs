using System.Text.Json;
using System.Security.Cryptography;
using GpuTuningLab.Core;

if (args is ["--session-smoke", string sessionPath])
{
    try
    {
        LabSession session = await LabStore.LoadAsync(sessionPath);
        int fingerprinted = session.Runs.Count(static run =>
            !string.IsNullOrWhiteSpace(run.WorkloadPackageFingerprint));
        Console.WriteLine(
            $"OK session | schema={session.SchemaVersion} | runs={session.Runs.Count} | " +
            $"fingerprinted={fingerprinted} | legacy={session.Runs.Count - fingerprinted}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("FAIL session: " + ex.Message);
        return 1;
    }
}

var tests = new List<(string Name, Func<Task> Run)>
{
    ("Parse NVIDIA CSV", () => Run(ParseNvidiaCsv)),
    ("Parse NVIDIA N/A", () => Run(ParseNvidiaNa)),
    ("Throttle bit masks", () => Run(ThrottleMasks)),
    ("Detect competing GPU workload", () => Run(DetectCompetingGpuWorkload)),
    ("Ignore host GPU process", () => Run(IgnoreHostGpuProcess)),
    ("Ignore Windows desktop compositor", () => Run(IgnoreWindowsDesktopCompositor)),
    ("Classify sustained GPU contamination", () => Run(ClassifySustainedGpuContamination)),
    ("Load text enum evaluation policy", () => Run(LoadTextEnumEvaluationPolicy)),
    ("Detect competing GPU video workload", () => Run(DetectCompetingGpuVideoWorkload)),
    ("Sanitize NVIDIA process name", () => Run(SanitizeNvidiaProcessName)),
    ("Telemetry aggregation", () => Run(TelemetryAggregation)),
    ("Reject telemetry gaps across workload windows", () => Run(RejectTelemetryGaps)),
    ("Reject missing required telemetry metric", () => Run(RejectMissingRequiredMetric)),
    ("Exclude idle telemetry from load metrics", () => Run(ExcludeIdleTelemetryFromLoadMetrics)),
    ("Verify observable stock state", () => Run(VerifyObservableStockState)),
    ("Reject direct instability", () => Run(RejectInstability)),
    ("Reward efficient profile", () => Run(RewardEfficiency)),
    ("Reject performance regression", () => Run(RejectPerformanceRegression)),
    ("Reject incomplete profile comparison", () => Run(RejectIncompleteProfileComparison)),
    ("Reject different GPU comparison", () => Run(RejectDifferentGpuComparison)),
    ("Reject different driver comparison", () => Run(RejectDifferentDriverComparison)),
    ("Reject different workload package comparison", () => Run(RejectDifferentWorkloadPackageComparison)),
    ("Reject one weak workload despite good average", () => Run(RejectWeakIndividualWorkload)),
    ("Do not compare temperature from different starting state", () => Run(RejectUnfairThermalComparison)),
    ("Verify the profile actually applied under load", () => Run(VerifyAppliedProfile)),
    ("Parse Windows stability evidence", () => Run(ParseWindowsEvidence)),
    ("Filter unrelated application crash", () => Run(FilterUnrelatedCrash)),
    ("Filter unrelated WHEA", () => Run(FilterUnrelatedWhea)),
    ("Parse external workload score", () => Run(ParseWorkloadScore)),
    ("Parse ray tracing workload score", () => Run(ParseRayTracingWorkloadScore)),
    ("Parse measured workload duration", () => Run(ParseMeasuredWorkloadDuration)),
    ("Parse PresentMon frametimes", ParsePresentMonFrames),
    ("Parse VRAM validation pass", () => Run(ParseVramPass)),
    ("Parse VRAM validation error", () => Run(ParseVramError)),
    ("Reject insufficient reference data", () => Run(RejectInsufficientReferenceData)),
    ("Build conservative reference envelope", () => Run(BuildReferenceEnvelope)),
    ("Reject duplicate reference units", () => Run(RejectDuplicateReferenceUnits)),
    ("Normalize NVIDIA model name", () => Run(NormalizeNvidiaModelName)),
    ("Classify supported GPU models", () => Run(ClassifySupportedGpuModels)),
    ("Match GPU variant by VRAM", () => Run(MatchGpuVariantByVram)),
    ("Block mismatched reference VRAM", () => Run(BlockMismatchedReferenceVram)),
    ("Validate repeatable stock baseline", () => Run(ValidateStockBaseline)),
    ("Reject stock baseline across driver change", () => Run(RejectBaselineDriverChange)),
    ("Reject noisy stock baseline", () => Run(RejectNoisyBaseline)),
    ("Reject short stock baseline", () => Run(RejectShortBaseline)),
    ("Reject contaminated stock baseline", () => Run(RejectContaminatedBaseline)),
    ("Consolidate stock baseline", () => Run(ConsolidateStockBaseline)),
    ("Keep latest valid stock baseline", () => Run(KeepLatestValidStockBaseline)),
    ("Reject outdated stock workload package", () => Run(RejectOutdatedStockWorkloadPackage)),
    ("Rank Pareto profiles", () => Run(RankParetoProfiles)),
    ("Allow bounded profile", () => Run(AllowBoundedProfile)),
    ("Block unsafe profile", () => Run(BlockUnsafeProfile)),
    ("Run external workload safely", RunExternalWorkload),
    ("Reject contaminated external workload", RejectContaminatedExternalWorkload),
    ("Cancel external workload safely", CancelExternalWorkload),
    ("Validate workload package hashes", ValidateWorkloadPackageHashes),
    ("Validate shipped workload package", ValidateShippedWorkloadPackage),
    ("Atomic session roundtrip", AtomicSessionRoundtrip),
    ("Recover newest valid temporary session", RecoverNewestTemporarySession),
    ("Preserve corrupt session instead of resetting it", PreserveCorruptSession),
    ("Reject malformed workload manifest safely", RejectMalformedWorkloadManifest),
    ("Load legacy session with null package fingerprints", LoadLegacyNullFingerprintSession),
    ("Reload latest compatible profile result", ReloadLatestCompatibleProfileResult),
    ("Keep latest result when public advice is corrupt", KeepLatestResultWhenAdviceIsCorrupt),
    ("Reference catalog rules", ReferenceCatalogRules),
    ("Published evidence registry rules", PublishedEvidenceRegistryRules),
    ("Classify public evidence quality dimensions", ClassifyEvidenceQualityDimensions),
    ("Build personalized public-evidence seed", BuildPersonalizedEvidenceSeed),
    ("Block sparse mixed evidence", BlockSparseMixedEvidence),
    ("Block every under-documented GPU model", BlockEveryUnderDocumentedModel),
    ("Do not suggest stock-or-higher voltage", RejectNonUndervoltSeed),
    ("Build measured next profile", BuildMeasuredNextProfile),
    ("Isolate memory before raising voltage", IsolateMemoryBeforeRaisingVoltage)
};

int passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"OK  {test.Name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}
Console.WriteLine($"GpuTuningLab tests: {passed}/{tests.Count} OK");
return passed == tests.Count ? 0 : 1;

static Task Run(Action action)
{
    action();
    return Task.CompletedTask;
}

static void ParseNvidiaCsv()
{
    const string line = "2026/07/11 05:05:10.990, NVIDIA GeForce RTX 4070 SUPER, GPU-test, 00000000:01:00.0, 0x278310DE, 0x413B1458, 591.86, 95.04.69.00.e7, P0, 41, 36.40, 36.12, 220.00, 220.00, 220.00, 100.00, 275.00, 2505, 11501, 3105, 10501, 99, 45, 1911, 12282, 30, 0x0000000000000005";
    True(NvidiaSmiCsv.TryParse(line, out var parsed, out string error), error);
    Equal("NVIDIA GeForce RTX 4070 SUPER", parsed.Identity.Name);
    Near(36.4, parsed.Sample.PowerAverageW!.Value, 0.001);
    True(parsed.Sample.ClockEventReasons.HasFlag(NvidiaClockEventReasons.GpuIdle), "GPU idle bit missing.");
    True(parsed.Sample.ClockEventReasons.HasFlag(NvidiaClockEventReasons.SoftwarePowerCap), "Power cap bit missing.");
}

static void ParseNvidiaNa()
{
    const string line = "2026/07/11 05:05:10.990, NVIDIA GPU, GPU-test, 01:00.0, dev, sub, drv, bios, P8, N/A, N/A, N/A, N/A, N/A, N/A, N/A, N/A, N/A, N/A, N/A, N/A, N/A, N/A, N/A, N/A, N/A, N/A";
    True(NvidiaSmiCsv.TryParse(line, out var parsed, out string error), error);
    True(parsed.Sample.TemperatureC == null, "N/A temperature must remain null.");
    True(parsed.Sample.ClockEventReasons == NvidiaClockEventReasons.None, "N/A mask must be none.");
}

static void ThrottleMasks()
{
    var mask = NvidiaClockEventReasons.SoftwarePowerCap | NvidiaClockEventReasons.HardwareThermalSlowdown;
    True(mask.HasFlag(NvidiaClockEventReasons.SoftwarePowerCap), "Power cap missing.");
    True(mask.HasFlag(NvidiaClockEventReasons.HardwareThermalSlowdown), "Thermal bit missing.");
    True(!mask.HasFlag(NvidiaClockEventReasons.GpuIdle), "Idle bit must not be inferred.");
}

static void DetectCompetingGpuWorkload()
{
    const string output = """
        # gpu         pid   type     sm    mem    enc    dec    jpg    ofa     fb   ccpm    command
            0       1234   C+G      -      -      -      -      -      -      0      0    explorer.exe
            0       5678   C+G     56     10      -      -      -      -      0      0    game.exe
        """;
    IReadOnlyList<ActiveGpuProcess> processes = GpuWorkloadPreflight.Parse(output);
    Equal(2, processes.Count);
    Near(56, processes[1].ComputePercent!.Value, 0.001);
    Equal("game.exe", processes[1].Name);
}

static void IgnoreHostGpuProcess()
{
    var host = new ActiveGpuProcess(15540, "Tweakly.exe", 17, 3);
    var browser = new ActiveGpuProcess(4820, "brave.exe", 29, 0, null, 5);
    var workload = new ActiveGpuProcess(9000, "GpuTuningLab.Workload.exe", 98, 12);

    ActiveGpuProcess[] busy = GpuWorkloadPreflight.SelectContaminatingProcesses(
        [host, browser, workload],
        new HashSet<int> { workload.ProcessId },
        host.ProcessId);

    Equal(1, busy.Length);
    Equal(browser.ProcessId, busy[0].ProcessId);
    Equal("brave.exe", busy[0].Name);
}

static void IgnoreWindowsDesktopCompositor()
{
    var compositor = new ActiveGpuProcess(2092, "dwm.exe", 86, 0);

    ActiveGpuProcess[] busy = GpuWorkloadPreflight.SelectContaminatingProcesses(
        [compositor],
        allowedProcessIds: null,
        hostProcessId: 15540);

    Equal(0, busy.Length);
    True(!GpuContaminationPolicy.IsSignificant(compositor, 7, 7),
        "Le compositeur Windows ne doit pas invalider le workload qu'il compose.");
}

static void DetectCompetingGpuVideoWorkload()
{
    const string output = """
        # gpu         pid   type     sm    mem    enc    dec    jpg    ofa     fb   ccpm    command
            0       1234   C+G      0      0      -      2      -      -      0      0    browser.exe
        """;
    ActiveGpuProcess process = GpuWorkloadPreflight.Parse(output).Single();
    Near(2, process.DecoderPercent!.Value, 0.001);
    True(GpuWorkloadPreflight.IsContaminating(process),
        "Active video decoding must block a controlled GPU measurement.");
    True(!GpuWorkloadPreflight.IsContaminating(process with { DecoderPercent = 0 }),
        "Zero-percent video activity must not block a controlled GPU measurement.");
}

static void SanitizeNvidiaProcessName()
{
    Equal("PathOfExileSteam", GpuWorkloadPreflight.SanitizeReportedProcessName("PathOfExileSteamP¨¤¹\\"));
    Equal("unknown-process", GpuWorkloadPreflight.SanitizeReportedProcessName("é"));
}

static void ClassifySustainedGpuContamination()
{
    var shellSpike = new ActiveGpuProcess(1, "explorer.exe", 12, 0);
    var highShellSpike = new ActiveGpuProcess(5, "explorer.exe", 21, 0);
    var browserSpike = new ActiveGpuProcess(2, "browser.exe", 31, 1);
    var repeatedModerate = new ActiveGpuProcess(3, "browser.exe", 7, 1);
    var videoDecode = new ActiveGpuProcess(4, "browser.exe", 0, 0, 0, 1);

    True(!GpuContaminationPolicy.IsSignificant(shellSpike, 1, 1),
        "One moderate shell spike must not invalidate a long workload.");
    True(!GpuContaminationPolicy.IsSignificant(shellSpike, 4, 1),
        "Separated moderate Windows shell spikes must not accumulate into a false rejection.");
    True(!GpuContaminationPolicy.IsSignificant(shellSpike, 2, 2),
        "Two seconds of moderate Windows shell activity must not invalidate the workload.");
    True(!GpuContaminationPolicy.IsSignificant(shellSpike, 4, 4),
        "Short sustained Windows shell activity must not invalidate the workload.");
    True(GpuContaminationPolicy.IsSignificant(shellSpike, 5, 5),
        "Five seconds of sustained Windows shell GPU activity must invalidate the workload.");
    True(GpuContaminationPolicy.IsSignificant(highShellSpike, 1, 1),
        "One high Windows shell GPU spike must invalidate the workload.");
    True(GpuContaminationPolicy.IsSignificant(browserSpike, 1, 1),
        "One high GPU spike must invalidate the workload.");
    True(GpuContaminationPolicy.IsSignificant(repeatedModerate, 2, 2),
        "Two consecutive moderate observations must invalidate the workload.");
    True(GpuContaminationPolicy.IsSignificant(videoDecode, 1, 1),
        "An active video engine must invalidate the workload.");
}

static void LoadTextEnumEvaluationPolicy()
{
    string path = Path.Combine(Path.GetTempPath(), $"gpu-policy-{Guid.NewGuid():N}.json");
    try
    {
        var source = new EvaluationPolicy
        {
            SamplingIntervalMs = 750,
            RequiredValidationWorkloads =
                [WorkloadKind.Graphics, WorkloadKind.RayTracing, WorkloadKind.Vram, WorkloadKind.Transient]
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(source, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }));
        EvaluationPolicy policy = EvaluationPolicyStore.Load(path);
        Equal(750, policy.SamplingIntervalMs);
        Equal(4, policy.RequiredValidationWorkloads.Length);
        Equal(WorkloadKind.Graphics, policy.RequiredValidationWorkloads[0]);
        Equal(WorkloadKind.Transient, policy.RequiredValidationWorkloads[3]);
    }
    finally
    {
        File.Delete(path);
    }
}

static void TelemetryAggregation()
{
    var run = MakeRun(1000, 200, 80, minutes: 2);
    var summary = RunAnalyzer.Summarize(run, Policy());
    Equal(StabilityVerdict.ShortPass, summary.Verdict);
    Near(200, summary.AveragePowerW!.Value, 0.1);
    Near(80, summary.AverageTemperatureC!.Value, 0.1);
    Near(8.3333, summary.EnergyWh, 0.05);
}

static void RejectTelemetryGaps()
{
    TestRun source = MakeRun(1000, 200, 80, minutes: 2);
    TestRun interrupted = source with
    {
        Samples = source.Samples
            .Where((_, index) => index < 100 || index >= source.Samples.Count - 100)
            .ToArray()
    };
    RunSummary summary = RunAnalyzer.Summarize(interrupted, Policy());
    Equal(StabilityVerdict.InvalidTelemetry, summary.Verdict);
    True(summary.TelemetryCoveragePercent < 95, "A long missing interval must reduce telemetry coverage.");
}

static void RejectMissingRequiredMetric()
{
    TestRun source = MakeRun(1000, 200, 80, minutes: 2);
    TestRun missingPower = source with
    {
        Samples = source.Samples.Select(static sample => sample with
        {
            PowerAverageW = null,
            PowerInstantW = null
        }).ToArray()
    };
    RunSummary summary = RunAnalyzer.Summarize(missingPower, Policy());
    Equal(StabilityVerdict.InvalidTelemetry, summary.Verdict);
    True(summary.Reasons.Any(static reason =>
            reason.Contains("power", StringComparison.OrdinalIgnoreCase)),
        string.Join(" | ", summary.Reasons));
}

static void ExcludeIdleTelemetryFromLoadMetrics()
{
    DateTimeOffset start = DateTimeOffset.Now;
    GpuTelemetrySample Sample(int milliseconds, double utilization, double power, double temperature,
        NvidiaClockEventReasons reasons) => new()
        {
            Timestamp = start.AddMilliseconds(milliseconds),
            GpuUtilizationPercent = utilization,
            MemoryUtilizationPercent = 0,
            PowerAverageW = power,
            TemperatureC = temperature,
            CoreClockMhz = utilization >= 50 ? 2500 : 300,
            ClockEventReasons = reasons
        };
    TestRun run = MakeRun(1000, 100, 60, minutes: 1) with
    {
        Samples =
        [
            Sample(0, 5, 20, 40, NvidiaClockEventReasons.SoftwareThermalSlowdown),
            Sample(500, 99, 100, 60, NvidiaClockEventReasons.SoftwarePowerCap),
            Sample(1000, 99, 100, 60, NvidiaClockEventReasons.None),
            Sample(1500, 5, 20, 40, NvidiaClockEventReasons.SoftwareThermalSlowdown)
        ]
    };

    RunSummary summary = RunAnalyzer.Summarize(run, Policy());
    Near(100, summary.AveragePowerW!.Value, 0.001);
    Near(60, summary.AverageTemperatureC!.Value, 0.001);
    Near(50, summary.PowerLimitTimePercent, 0.001);
    Near(0, summary.ThermalLimitTimePercent, 0.001);
}

static void VerifyObservableStockState()
{
    var stock = new GpuTelemetrySample
    {
        Timestamp = DateTimeOffset.Now,
        RequestedPowerLimitW = 220,
        EnforcedPowerLimitW = 220,
        DefaultPowerLimitW = 220,
        MemoryClockMhz = 10501,
        MaxMemoryClockMhz = 10501
    };
    True(StockStateVerifier.Assess([stock]).ObservableStateMatchesStock,
        "Matching observable limits should pass.");
    True(!StockStateVerifier.Assess([stock with { MemoryClockMhz = 11501 }]).ObservableStateMatchesStock,
        "A memory overclock must block a stock baseline.");
    True(!StockStateVerifier.Assess([stock with { EnforcedPowerLimitW = 198 }]).ObservableStateMatchesStock,
        "A non-default power limit must block a stock baseline.");
}

static void RejectInstability()
{
    var run = MakeRun(1000, 200, 80, minutes: 2) with
    {
        StabilityEvents = [new StabilityEvent
        {
            Timestamp = DateTimeOffset.Now,
            Kind = StabilityEventKind.Tdr,
            Evidence = "Display 4101"
        }]
    };
    Equal(StabilityVerdict.Rejected, RunAnalyzer.Summarize(run, Policy()).Verdict);
    Equal(RecommendationKind.RestoreLastStable,
        RecommendationEngine.Recommend(MakeRun(1000, 200, 80, 2), run, Policy(), false).Kind);
}

static void RewardEfficiency()
{
    var baseline = MakeRun(1000, 200, 80, 2);
    var candidate = MakeRun(985, 155, 70, 2, ProfileKind.Undervolt);
    var comparison = ProfileEvaluator.Compare(baseline, candidate, Policy());
    True(comparison.MeetsPerformanceFloor, "Performance floor should pass.");
    True(comparison.EfficiencyIndex > 125, "Efficiency gain should be substantial.");
    True(comparison.BalancedScore > 100, "Efficient candidate should beat stock.");
}

static void RejectPerformanceRegression()
{
    var policy = Policy();
    var baseline = MakeRun(1000, 200, 80, 2);
    var candidate = MakeRun(900, 130, 65, 2, ProfileKind.Undervolt);
    Equal(RecommendationKind.IncreaseVoltageOrReduceClock,
        RecommendationEngine.Recommend(baseline, candidate, policy, true).Kind);
}

static void RejectIncompleteProfileComparison()
{
    TestRun baseline = MakeRun(1000, 200, 80, 2);
    TestRun candidate = MakeRun(990, 160, 70, 2, ProfileKind.Undervolt) with
    {
        Workloads = MakeRun(990, 160, 70, 2, ProfileKind.Undervolt).Workloads.Skip(1).ToArray()
    };
    Throws<InvalidOperationException>(() => ProfileEvaluator.Compare(baseline, candidate, Policy()));
}

static void RejectDifferentGpuComparison()
{
    TestRun baseline = MakeRun(1000, 200, 80, 2);
    TestRun candidate = MakeRun(990, 160, 70, 2, ProfileKind.Undervolt) with
    {
        Identity = TestGpu("other-vbios")
    };
    Throws<InvalidOperationException>(() => ProfileEvaluator.Compare(baseline, candidate, Policy()));
}

static void RejectDifferentDriverComparison()
{
    TestRun baseline = MakeRun(1000, 200, 80, 2);
    TestRun candidate = MakeRun(990, 160, 70, 2, ProfileKind.Undervolt) with
    {
        Identity = baseline.Identity with { DriverVersion = "other-driver" }
    };
    Throws<InvalidOperationException>(() => ProfileEvaluator.Compare(baseline, candidate, Policy()));
}

static void RejectDifferentWorkloadPackageComparison()
{
    TestRun baseline = MakeRun(1000, 200, 80, 2);
    TestRun candidate = MakeRun(990, 160, 70, 2, ProfileKind.Undervolt) with
    {
        WorkloadPackageFingerprint = "test-package-v2"
    };
    Throws<InvalidOperationException>(() => ProfileEvaluator.Compare(baseline, candidate, Policy()));
}

static void RejectWeakIndividualWorkload()
{
    TestRun baseline = MakeRun(100, 200, 70, 2);
    TestRun candidateSource = MakeRun(103, 160, 70, 2, ProfileKind.Undervolt);
    TestRun candidate = candidateSource with
    {
        Workloads = candidateSource.Workloads.Select(static workload =>
            workload.Kind == WorkloadKind.Vram
                ? workload with { Score = 89 }
                : workload).ToArray()
    };
    ProfileComparison comparison = ProfileEvaluator.Compare(baseline, candidate, Policy());
    True(comparison.PerformanceIndex >= Policy().MinimumPerformanceRetentionPercent,
        "The aggregate should pass so the individual floor is what rejects this profile.");
    Near(89, comparison.MinimumWorkloadPerformanceIndex, 0.01);
    True(!comparison.MeetsPerformanceFloor, "One weak workload must reject an otherwise good average.");
    Recommendation recommendation = RecommendationEngine.Recommend(
        baseline,
        candidate,
        Policy(),
        trustedSearchEnvelopeAvailable: false);
    True(recommendation.Reason.Contains("Vram", StringComparison.OrdinalIgnoreCase),
        "The recommendation must identify the individual workload that failed.");
}

static void RejectUnfairThermalComparison()
{
    TestRun baseline = MakeRun(100, 200, 80, 2);
    TestRun candidate = MakeRun(100, 160, 70, 2, ProfileKind.Undervolt);
    ProfileComparison comparison = ProfileEvaluator.Compare(baseline, candidate, Policy());
    True(!comparison.ThermalComparisonReliable,
        "A 10 °C difference at test start must make the thermal comparison unreliable.");
    True(!comparison.TemperatureDeltaC.HasValue,
        "An unreliable thermal delta must not be shown or scored.");
}

static void VerifyAppliedProfile()
{
    TestRun baselineSource = MakeRun(100, 200, 70, 2);
    TestRun baseline = baselineSource with
    {
        Samples = baselineSource.Samples.Select((sample, index) => sample with
        {
            VoltageV = 1.05,
            GpuUtilizationPercent = index < 20 ? 0 : 99,
            MemoryClockMhz = index < 20 ? 405 : 10501,
            RequestedPowerLimitW = 220,
            DefaultPowerLimitW = 220
        }).ToArray()
    };
    TestRun candidateSource = MakeRun(100, 160, 65, 2, ProfileKind.Undervolt);
    TestRun candidate = candidateSource with
    {
        Profile = new GpuTuningProfile
        {
            Name = "Measured profile",
            Kind = ProfileKind.Undervolt,
            TargetVoltageMv = 920,
            TargetClockMhz = 2670,
            MemoryOffsetMhz = 0,
            PowerLimitPercent = 100
        },
        Samples = candidateSource.Samples.Select(static sample => sample with
        {
            VoltageV = 0.92,
            CoreClockMhz = 2670,
            MemoryClockMhz = 10501,
            RequestedPowerLimitW = 220,
            DefaultPowerLimitW = 220
        }).ToArray()
    };
    ProfileApplicationAssessment verified = ProfileApplicationVerifier.Assess(
        baseline,
        candidate,
        Policy());
    True(verified.Verified, string.Join(" | ", verified.BlockingReasons));
    Equal(0, verified.ObservedMemoryOffsetMhz);

    ProfileApplicationAssessment mismatch = ProfileApplicationVerifier.Assess(
        baseline,
        candidate with
        {
            Profile = candidate.Profile with
            {
                TargetVoltageMv = 975,
                MemoryOffsetMhz = 1000
            }
        },
        Policy());
    True(!mismatch.Verified, "Declared values that were not observed must block comparison.");
    True(mismatch.BlockingReasons.Count >= 2, string.Join(" | ", mismatch.BlockingReasons));
}

static void ParseWindowsEvidence()
{
    string xml = EventXml("Display", 4101, "2026-07-11T03:00:00.000Z", "nvlddmkm stopped responding");
    var events = WindowsEventEvidenceParser.Parse(xml);
    Equal(1, events.Count);
    Equal(StabilityEventKind.Tdr, events[0].Kind);
}

static void FilterUnrelatedCrash()
{
    string xml = EventXml("Application Error", 1000, "2026-07-11T03:00:00.000Z", "other.exe");
    Equal(0, WindowsEventEvidenceParser.Parse(xml, "benchmark.exe").Count);
    Equal(1, WindowsEventEvidenceParser.Parse(xml, "other.exe").Count);
}

static void FilterUnrelatedWhea()
{
    string unrelated = EventXml("Microsoft-Windows-WHEA-Logger", 17, "2026-07-11T03:00:00.000Z", "PCI VEN_8086 DEV_1234");
    string nvidia = EventXml("Microsoft-Windows-WHEA-Logger", 17, "2026-07-11T03:00:00.000Z", "PCI VEN_10DE DEV_2783");
    Equal(0, WindowsEventEvidenceParser.Parse(unrelated, gpuDeviceId: "0x278310DE").Count);
    Equal(1, WindowsEventEvidenceParser.Parse(nvidia, gpuDeviceId: "0x278310DE").Count);
}

static void ParseWorkloadScore()
{
    True(ExternalWorkloadRunner.TryParseScore("Final score: 1234,56 points", @"score:\s*([0-9]+[.,]?[0-9]*)", out double score),
        "Score was not parsed.");
    Near(1234.56, score, 0.001);
}

static void ParseRayTracingWorkloadScore()
{
    const string output = "Final score: 8123.456 M primary-rays/s | frames: 3918 | elapsed: 2.001 s";
    True(ExternalWorkloadRunner.TryParseScore(
            output,
            @"Final score:\s*([0-9]+(?:[.,][0-9]+)?)",
            out double score),
        "Ray tracing score was not parsed.");
    Near(8123.456, score, 0.001);
}

static void ParseMeasuredWorkloadDuration()
{
    True(ExternalWorkloadRunner.TryParseDuration(
            "Final score: 10 | elapsed: 30,125 s",
            @"elapsed:\s*([0-9]+(?:[.,][0-9]+)?)\s*s",
            out TimeSpan duration),
        "Measured duration was not parsed.");
    Near(30.125, duration.TotalSeconds, 0.001);
    True(!ExternalWorkloadRunner.TryParseDuration("elapsed: 0 s", @"elapsed:\s*([0-9]+)\s*s", out _),
        "A zero duration must be rejected.");
}

static async Task ParsePresentMonFrames()
{
    string path = Path.Combine(Path.GetTempPath(), $"presentmon-{Guid.NewGuid():N}.csv");
    try
    {
        await File.WriteAllTextAsync(path,
            "Application,ProcessID,PresentMode,TimeInMs,MsBetweenPresents,MsGPUTime\n" +
            "game.exe,42,Hardware: Independent Flip,0,10,8\n" +
            "other.exe,7,Composed: Flip,5,5,2\n" +
            "game.exe,42,Hardware: Independent Flip,10,10,8\n" +
            "game.exe,42,Hardware: Independent Flip,20,20,15\n" +
            "game.exe,42,Hardware: Independent Flip,40,10,8\n");
        var samples = PresentMonCsv.Parse(path, "game.exe");
        Equal(4, samples.Count);
        var summary = PresentMonCsv.Summarize(samples);
        Near(100, summary.MedianFps, 0.01);
        Near(10, summary.MedianFrameTimeMs, 0.01);
        True(summary.P99FrameTimeMs > 19, "P99 should retain the slow frame.");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

static void ParseVramPass()
{
    const string output = """
        Standard 5-minute test of 1: Bus=0x01:00 DevId=0x2783   12GB NVIDIA GeForce RTX 4070 SUPER
          199 iteration. Passed  100.0 seconds  written: 3510.0GB  450.5GB/sec checked: 4095.0GB  410.2GB/sec
        Standard 5-minute test PASSed! Just press Ctrl+C unless you plan long test run.
        """;
    VramValidationSummary result = MemtestVulkanOutputParser.Parse(output);
    Equal(VramValidationStatus.Passed, result.Status);
    Equal("NVIDIA GeForce RTX 4070 SUPER", result.Device);
    Equal(12288, result.MemoryMiB);
    Equal(199, result.Iterations);
    Near(410.2, result.CheckGiBPerSecond, 0.001);
}

static void ParseVramError()
{
    const string output = """
        Standard 5-minute test of 1: Bus=0x01:00 DevId=0x2783   12GB NVIDIA GeForce RTX 4070 SUPER
          12 iteration. Passed  10.0 seconds  written: 120.0GB  12.0GB/sec checked: 180.0GB  18.0GB/sec
        Error found. Mode NEXT_RE_READ, total errors 0x3 out of 0x1000
        """;
    VramValidationSummary result = MemtestVulkanOutputParser.Parse(output);
    Equal(VramValidationStatus.MemoryError, result.Status);
    True(result.FailureReason.Contains("total errors 0x3"), "Memory error details were lost.");
}

static void RejectInsufficientReferenceData()
{
    var result = ReferenceEnvelopeBuilder.Build("GPU", [Observation(0, 900, 2700)], new ReferenceBuildPolicy());
    True(!result.Eligible, "One observation must never create a tuning envelope.");
    True(result.Envelope == null, "Rejected data must not expose an envelope.");
}

static void BuildReferenceEnvelope()
{
    var observations = new[]
    {
        Observation(0, 875, 2670),
        Observation(1, 900, 2700),
        Observation(2, 925, 2730),
        Observation(3, 900, 2715),
        Observation(4, 950, 2745)
    };
    var result = ReferenceEnvelopeBuilder.Build("GPU", observations, new ReferenceBuildPolicy());
    True(result.Eligible, string.Join(" | ", result.RejectionReasons));
    Equal("observed", result.Confidence);
    True(result.Envelope!.MinimumVoltageMv >= 875, "Envelope must not extrapolate below observations.");
    True(result.Envelope.MaximumVoltageMv <= 950, "Envelope must not extrapolate above observations.");
}

static void RejectDuplicateReferenceUnits()
{
    GpuTuningObservation[] observations = Enumerable.Range(0, 5)
        .Select(index => Observation(index, 875 + index * 25, 2670 + index * 15) with
        {
            IndependentUnitId = "same-physical-card"
        })
        .ToArray();
    ReferenceBuildResult result = ReferenceEnvelopeBuilder.Build("GPU", observations, new ReferenceBuildPolicy());
    True(!result.Eligible, "Repeated runs from one physical card must not create an envelope.");
    Equal(1, result.AcceptedObservations);
    Equal(1, result.IndependentUnits);
}

static void NormalizeNvidiaModelName()
{
    Equal("GeForce RTX 4070 SUPER", GpuReferenceMatcher.NormalizeModelName(" NVIDIA   GeForce RTX 4070 SUPER "));
    True(GpuReferenceMatcher.SameModel("GeForce RTX 4070 SUPER", "NVIDIA GeForce RTX 4070 SUPER"),
        "The NVIDIA prefix must not break model matching.");
}

static void ClassifySupportedGpuModels()
{
    True(GpuTuningCompatibility.IsSupportedModelName("NVIDIA GeForce RTX 3080"), "RTX 3080 must be supported.");
    True(GpuTuningCompatibility.IsSupportedModelName("NVIDIA GeForce RTX 4070 SUPER"), "RTX 4070 SUPER must be supported.");
    True(GpuTuningCompatibility.IsSupportedModelName("NVIDIA GeForce RTX 5090"), "RTX 5090 must be supported.");
    True(!GpuTuningCompatibility.IsSupportedModelName("NVIDIA GeForce RTX 4080 Laptop GPU"), "Laptop GPU must be blocked.");
    True(!GpuTuningCompatibility.IsSupportedModelName("NVIDIA GeForce RTX 2080 Ti"), "RTX 2000 must be blocked.");
    True(!GpuTuningCompatibility.IsSupportedModelName("NVIDIA GeForce GTX 1080 Ti"), "GTX must be blocked.");
    True(!GpuTuningCompatibility.IsSupportedModelName("NVIDIA RTX A4000"), "Professional RTX must be blocked.");
}

static void MatchGpuVariantByVram()
{
    var identity = new GpuIdentity("NVIDIA GeForce RTX 3060", "uuid", "bus", "dev", "sub", "driver", "vbios");
    GpuReferenceEntry[] entries =
    [
        CatalogEntry("GeForce RTX 3060 8GB", 8192),
        CatalogEntry("GeForce RTX 3060 12GB", 12288)
    ];
    Equal("GeForce RTX 3060 12GB", GpuReferenceMatcher.Find(identity, 12282, entries)!.Model);
    True(GpuReferenceMatcher.Find(identity, 6144, entries) == null,
        "An incompatible VRAM capacity must not fall back to another variant.");
    True(GpuReferenceMatcher.Find(identity, null, entries) == null,
        "A VRAM variant must not be guessed when VRAM is unavailable.");
}

static void BlockMismatchedReferenceVram()
{
    SafetyGateResult result = ProfileSafetyGate.Check(
        TestGpu("bios-1"),
        TestGpu("bios-1"),
        TestProfile(875, 2700, 90),
        lastStable: null,
        currentVoltageMv: 900,
        TrustedReference() with { VramMiB = 8192 },
        new BaselineValidationResult(true, 0.5, []),
        PowerCapability() with { VramTotalMiB = 12288 });
    True(!result.Allowed, "A reference for another VRAM variant must be blocked.");
    True(result.BlockingReasons.Any(static reason => reason.Contains("VRAM", StringComparison.Ordinal)),
        "The VRAM mismatch reason is missing.");
}

static GpuReferenceEntry CatalogEntry(string model, int vramMiB) => new()
{
    Model = model,
    Series = 3000,
    Architecture = "Ampere",
    FormFactor = "desktop",
    Confidence = "catalogOnly",
    VramMiB = vramMiB
};

static GpuTuningObservation Observation(int index, int voltageMv, int clockMhz) => new()
{
    ObservationId = $"obs-{index}",
    IndependentUnitId = $"unit-{index}",
    Model = "GPU",
    FormFactor = "desktop",
    BoardPartner = "Partner",
    VbiosVersion = "1",
    DriverVersion = "1",
    CoolingClass = "air",
    ProtocolVersion = "gpu-lab-1",
    Profile = new GpuTuningProfile
    {
        Name = $"UV-{index}",
        Kind = ProfileKind.Undervolt,
        TargetVoltageMv = voltageMv,
        TargetClockMhz = clockMhz
    },
    Summary = new RunSummary
    {
        RunId = Guid.NewGuid(),
        Verdict = StabilityVerdict.Validated,
        TelemetryDuration = TimeSpan.FromHours(1),
        TelemetryCoveragePercent = 99,
        EnergyWh = 150,
        PowerLimitTimePercent = 0,
        ThermalLimitTimePercent = 0
    },
    PerformanceIndex = 99,
    BenchmarkVariancePercent = 1,
    Source = new EvidenceSource
    {
        Url = $"https://example.test/{index}",
        Publisher = $"publisher-{index % 3}",
        RetrievedOn = new DateOnly(2026, 7, 11),
        EvidenceType = "raw-run"
    }
};

static void ValidateStockBaseline()
{
    var result = BaselineValidator.Validate(
        [MakeRun(1000, 200, 80, 2), MakeRun(1005, 201, 80, 2), MakeRun(995, 199, 80, 2)],
        Policy());
    True(result.Valid, string.Join(" | ", result.Reasons));
    True(result.ScoreCoefficientOfVariationPercent < 1, "Stock variation should be below 1 %.");
}

static void RejectBaselineDriverChange()
{
    TestRun first = MakeRun(1000, 200, 80, 2);
    TestRun second = MakeRun(1001, 200, 80, 2);
    TestRun third = MakeRun(999, 200, 80, 2) with
    {
        Identity = first.Identity with { DriverVersion = "different-driver" }
    };
    BaselineValidationResult result = BaselineValidator.Validate([first, second, third], Policy());
    True(!result.Valid, "A stock baseline spanning two drivers must be rejected.");
    True(result.Reasons.Any(static reason =>
            reason.Contains("driver changed", StringComparison.OrdinalIgnoreCase)),
        string.Join(" | ", result.Reasons));
}

static void RejectNoisyBaseline()
{
    var result = BaselineValidator.Validate(
        [MakeRun(1000, 200, 80, 2), MakeRun(1100, 201, 80, 2), MakeRun(900, 199, 80, 2)],
        Policy());
    True(!result.Valid, "A noisy baseline must be rejected.");
}

static void RejectShortBaseline()
{
    var policy = Policy() with { MinimumBaselineWorkloadSeconds = 31 };
    var result = BaselineValidator.Validate(
        [MakeRun(1000, 200, 80, 2), MakeRun(1001, 200, 80, 2), MakeRun(999, 200, 80, 2)],
        policy);
    True(!result.Valid, "A baseline with workloads shorter than the policy must be rejected.");
}

static void RejectContaminatedBaseline()
{
    TestRun contaminated = MakeRun(1000, 200, 80, 2) with
    {
        Workloads = MakeRun(1000, 200, 80, 2).Workloads.Select((workload, index) => index == 0
            ? workload with
            {
                Completed = false,
                FailureReason = "Concurrent GPU workload detected during measurement: browser.exe"
            }
            : workload).ToArray()
    };
    BaselineValidationResult result = BaselineValidator.Validate(
        [contaminated, MakeRun(1000, 200, 80, 2), MakeRun(1000, 200, 80, 2)],
        Policy());
    True(!result.Valid, "A contaminated baseline must be rejected.");
    True(result.Reasons.Any(static reason => reason.Contains("browser.exe", StringComparison.OrdinalIgnoreCase)),
        string.Join(" | ", result.Reasons));
}

static void ConsolidateStockBaseline()
{
    TestRun result = BaselineConsolidator.Consolidate(
        [MakeRun(990, 200, 80, 2), MakeRun(1000, 200, 80, 2), MakeRun(1010, 200, 80, 2)],
        Policy());
    True(result.Workloads.All(workload => Math.Abs(workload.Score - 1000) < 0.001),
        "Consolidated scores must be workload-by-workload averages.");
    True(result.Workloads.All(workload => workload.ScoreVariancePercent > 0),
        "Consolidated workloads must retain stock variation.");
    Equal("manual-confirmed-stock", result.Profile.AppliedBy);
    Equal(5, result.WorkloadWindows.Count);
    Equal(result.Samples.Count, result.WorkloadWindows.Sum(static window => window.SampleCount) - 4);
}

static void KeepLatestValidStockBaseline()
{
    Guid validBatch = Guid.NewGuid();
    Guid interruptedBatch = Guid.NewGuid();
    DateTimeOffset validTime = DateTimeOffset.Now.AddMinutes(-20);
    TestRun[] validRuns =
    [
        MakeRun(1000, 200, 80, 2) with { BatchId = validBatch, StartedAt = validTime },
        MakeRun(1002, 200, 80, 2) with { BatchId = validBatch, StartedAt = validTime.AddMinutes(3) },
        MakeRun(998, 200, 80, 2) with { BatchId = validBatch, StartedAt = validTime.AddMinutes(6) }
    ];
    TestRun interrupted = MakeRun(1000, 200, 80, 2) with
    {
        BatchId = interruptedBatch,
        StartedAt = DateTimeOffset.Now
    };
    TestRun[] selected = BaselineRunSelector.LatestValidOrLatest(
        new LabSession { Runs = validRuns.Append(interrupted).ToArray() },
        Policy());
    Equal(3, selected.Length);
    True(selected.All(run => run.BatchId == validBatch),
        "An interrupted stock retry must not replace the latest valid baseline.");
}

static void RejectOutdatedStockWorkloadPackage()
{
    TestRun[] runs =
    [
        MakeRun(1000, 200, 80, 2),
        MakeRun(1002, 200, 80, 2),
        MakeRun(998, 200, 80, 2)
    ];
    BaselineValidationResult result = BaselineValidator.Validate(
        runs,
        Policy(),
        "test-package-v2");
    True(!result.Valid, "A stock baseline from another workload package must be rejected.");
    True(result.Reasons.Any(static reason =>
            reason.Contains("different workload package", StringComparison.OrdinalIgnoreCase)),
        string.Join(" | ", result.Reasons));

    TestRun[] legacyRuns = runs
        .Select(static run => run with { WorkloadPackageFingerprint = null })
        .ToArray();
    BaselineValidationResult legacy = BaselineValidator.Validate(
        legacyRuns,
        Policy(),
        "test-package-v1");
    True(!legacy.Valid, "A legacy stock baseline without a package fingerprint must be rejected.");
    True(legacy.Reasons.Any(static reason =>
            reason.Contains("predates workload package fingerprinting", StringComparison.OrdinalIgnoreCase)),
        string.Join(" | ", legacy.Reasons));
}

static async Task ValidateWorkloadPackageHashes()
{
    string root = Path.Combine(Path.GetTempPath(), "gpu-lab-package-" + Guid.NewGuid().ToString("N"));
    try
    {
        string[] relativeFiles =
        [
            "d3d11/GpuTuningLab.Workload.exe",
            "d3d11/THIRD_PARTY_NOTICES.md",
            "dxr/GpuTuningLab.RayTracingWorkload.exe",
            "dxr/D3D12/D3D12Core.dll",
            "dxr/THIRD_PARTY_NOTICES.md",
            "dxr/D3D12_LICENSE.txt",
            "dxr/D3D12_LICENSE-CODE.txt"
        ];
        foreach (string relative in relativeFiles)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "verified-" + relative);
        }

        var manifest = relativeFiles.Select(relative =>
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            byte[] bytes = File.ReadAllBytes(path);
            return new
            {
                path = relative,
                bytes = bytes.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            };
        }).ToArray();
        await File.WriteAllTextAsync(
            Path.Combine(root, "manifest.json"),
            JsonSerializer.Serialize(manifest));

        WorkloadPackageValidation valid = await WorkloadPackageValidator.ValidateAsync(root);
        True(valid.Valid, string.Join(" | ", valid.Errors));
        True(!string.IsNullOrWhiteSpace(valid.Fingerprint), "A valid package needs a fingerprint.");

        await File.AppendAllTextAsync(Path.Combine(root, "d3d11", "GpuTuningLab.Workload.exe"), "tampered");
        WorkloadPackageValidation altered = await WorkloadPackageValidator.ValidateAsync(root);
        True(!altered.Valid, "A modified workload package must be rejected.");
        True(altered.Errors.Any(static error => error.Contains("mismatch", StringComparison.OrdinalIgnoreCase)),
            string.Join(" | ", altered.Errors));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static async Task ValidateShippedWorkloadPackage()
{
    string root = Path.Combine(AppContext.BaseDirectory, "data", "tools", "gpu-tuning");
    WorkloadPackageValidation validation = await WorkloadPackageValidator.ValidateAsync(root);
    True(validation.Valid, string.Join(" | ", validation.Errors));
}

static void RankParetoProfiles()
{
    var baseline = MakeRun(1000, 200, 80, 2);
    var efficient = MakeRun(985, 155, 70, 2, ProfileKind.Undervolt);
    var dominated = MakeRun(970, 180, 78, 2, ProfileKind.Undervolt);
    var fast = MakeRun(1010, 230, 85, 2, ProfileKind.Overclock);
    var rows = ProfileRanker.Rank(baseline, [dominated, fast, efficient], Policy());
    True(rows.Single(row => row.Run.Id == efficient.Id).ParetoEfficient, "Efficient profile should be Pareto-efficient.");
    True(!rows.Single(row => row.Run.Id == dominated.Id).ParetoEfficient,
        "Dominated profile must be marked. " + string.Join(" | ", rows.Select(row =>
            $"{row.Run.Profile.Kind}: perf {row.Comparison.PerformanceIndex:0.0}, " +
            $"eff {row.Comparison.EfficiencyIndex:0.0}, temp {row.Comparison.TemperatureDeltaC:0.0}, " +
            $"pareto {row.ParetoEfficient}")));
}

static async Task RunExternalWorkload()
{
    string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    var execution = await new ExternalWorkloadRunner().RunAsync(new ExternalWorkloadDefinition
    {
        Name = "Fake workload",
        Version = "1",
        Kind = WorkloadKind.Graphics,
        ExecutablePath = cmd,
        Arguments = ["/d", "/c", "echo Measured duration: 2.5 s ^& echo Final score: 1234.5"],
        Timeout = TimeSpan.FromSeconds(5),
        ScorePattern = @"score:\s*([0-9]+[.,]?[0-9]*)",
        DurationPattern = @"Measured duration:\s*([0-9]+[.,]?[0-9]*)\s*s",
        ScoreUnit = "points"
    });
    True(execution.Result.Completed, execution.StandardError);
    Near(1234.5, execution.Result.Score, 0.001);
    Near(2.5, execution.Result.Duration.TotalSeconds, 0.001);
}

static async Task CancelExternalWorkload()
{
    string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await new ExternalWorkloadRunner().RunAsync(new ExternalWorkloadDefinition
        {
            Name = "Cancellable workload",
            Version = "1",
            Kind = WorkloadKind.Graphics,
            ExecutablePath = cmd,
            Arguments = ["/d", "/c", "ping 127.0.0.1 -n 30 >nul"],
            Timeout = TimeSpan.FromSeconds(30),
            ScorePattern = @"score:\s*([0-9]+)",
            ScoreUnit = "points"
        }, cancellation.Token);
        throw new InvalidOperationException("Cancellation was ignored.");
    }
    catch (OperationCanceledException)
    {
        stopwatch.Stop();
        True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "Cancelled process did not stop promptly.");
    }
}

static async Task RejectContaminatedExternalWorkload()
{
    string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    var runner = new ExternalWorkloadRunner(new FixedGpuContaminationMonitor());
    WorkloadExecution execution = await runner.RunAsync(new ExternalWorkloadDefinition
    {
        Name = "Contaminated workload",
        Version = "1",
        Kind = WorkloadKind.Graphics,
        ExecutablePath = cmd,
        Arguments = ["/d", "/c", "echo Final score: 1234.5"],
        Timeout = TimeSpan.FromSeconds(5),
        ScorePattern = @"score:\s*([0-9]+[.,]?[0-9]*)",
        ScoreUnit = "points"
    });

    True(!execution.Result.Completed, "A contaminated workload must be rejected.");
    True(execution.Result.FailureReason.Contains("browser.exe", StringComparison.OrdinalIgnoreCase),
        execution.Result.FailureReason);
}

static void AllowBoundedProfile()
{
    var gpu = TestGpu("bios-1");
    var result = ProfileSafetyGate.Check(
        gpu,
        gpu,
        TestProfile(875, 2700, 90),
        lastStable: null,
        currentVoltageMv: 900,
        TrustedReference(),
        new BaselineValidationResult(true, 0.5, []),
        PowerCapability());
    True(result.Allowed, string.Join(" | ", result.BlockingReasons));
}

static void BlockUnsafeProfile()
{
    var result = ProfileSafetyGate.Check(
        TestGpu("bios-1"),
        TestGpu("bios-2"),
        TestProfile(800, 2900, 200),
        lastStable: null,
        currentVoltageMv: 900,
        TrustedReference(),
        new BaselineValidationResult(false, 5, ["noisy"]),
        PowerCapability());
    True(!result.Allowed, "Unsafe profile must be blocked.");
    True(result.BlockingReasons.Count >= 4, "Independent safety failures should all be reported.");
}

static GpuIdentity TestGpu(string vbios) =>
    new("GPU", "uuid", "bus", "dev", "sub", "driver", vbios);

static GpuTuningProfile TestProfile(int voltageMv, int clockMhz, double powerPercent) => new()
{
    Name = "candidate",
    Kind = ProfileKind.Undervolt,
    TargetVoltageMv = voltageMv,
    TargetClockMhz = clockMhz,
    PowerLimitPercent = powerPercent
};

static GpuReferenceEntry TrustedReference() => new()
{
    Model = "GPU",
    Series = 4000,
    Architecture = "Ada",
    FormFactor = "desktop",
    Confidence = "reviewed",
    SearchEnvelope = new SearchEnvelope
    {
        MinimumVoltageMv = 850,
        MaximumVoltageMv = 950,
        MinimumClockMhz = 2600,
        MaximumClockMhz = 2800,
        MaximumMemoryOffsetMhz = 1000,
        VoltageStepMv = 25
    },
    Sources =
    [
        new EvidenceSource { Url = "https://a", Publisher = "a", RetrievedOn = new DateOnly(2026, 7, 11), EvidenceType = "run" },
        new EvidenceSource { Url = "https://b", Publisher = "b", RetrievedOn = new DateOnly(2026, 7, 11), EvidenceType = "run" }
    ]
};

static GpuTelemetrySample PowerCapability() => new()
{
    Timestamp = DateTimeOffset.Now,
    DefaultPowerLimitW = 220,
    MinPowerLimitW = 100,
    MaxPowerLimitW = 275
};

static string EventXml(string provider, int id, string timestamp, string payload) =>
    $"""
    <Events xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
      <Event>
        <System>
          <Provider Name="{provider}" />
          <EventID>{id}</EventID>
          <TimeCreated SystemTime="{timestamp}" />
        </System>
        <EventData><Data>{payload}</Data></EventData>
      </Event>
    </Events>
    """;

static async Task AtomicSessionRoundtrip()
{
    string root = Path.Combine(Path.GetTempPath(), "GpuTuningLabTests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "session.json");
    try
    {
        var session = new LabSession { Runs = [MakeRun(1000, 200, 80, 2)] };
        await LabStore.SaveAsync(path, session);
        var loaded = await LabStore.LoadAsync(path);
        Equal(1, loaded.Runs.Count);
        Equal(loaded.Runs[0].Workloads.Count, loaded.Runs[0].WorkloadWindows.Count);
        Equal("test-package-v1", loaded.Runs[0].WorkloadPackageFingerprint);
        await LabStore.SaveAsync(path, loaded);
        True(File.Exists(path + ".bak"), "Second save must preserve a backup.");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static async Task RecoverNewestTemporarySession()
{
    string root = Path.Combine(Path.GetTempPath(), "GpuTuningLabTests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "session.json");
    try
    {
        var original = new LabSession { Runs = [MakeRun(1000, 200, 80, 2)] };
        await LabStore.SaveAsync(path, original);
        var newer = new LabSession
        {
            Runs = [MakeRun(1000, 200, 80, 2), MakeRun(990, 160, 70, 2, ProfileKind.Undervolt)]
        };
        string temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(newer, LabJsonContext.Default.LabSession));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(temporaryPath, DateTime.UtcNow);

        LabSession recovered = await LabStore.LoadAsync(path);
        Equal(2, recovered.Runs.Count);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static async Task PreserveCorruptSession()
{
    string root = Path.Combine(Path.GetTempPath(), "GpuTuningLabTests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "session.json");
    try
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "{broken");
        await File.WriteAllTextAsync(path + ".tmp", "null");
        await File.WriteAllTextAsync(path + ".bak", """{"schemaVersion":99,"runs":[]}""");
        bool failedClosed = false;
        try
        {
            await LabStore.LoadAsync(path);
        }
        catch (InvalidDataException)
        {
            failedClosed = true;
        }
        True(failedClosed, "Existing unreadable history must not become a blank session.");
        Equal("{broken", await File.ReadAllTextAsync(path));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static async Task RejectMalformedWorkloadManifest()
{
    string root = Path.Combine(Path.GetTempPath(), "gpu-lab-package-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "manifest.json"),
            """[{"path":null,"bytes":1,"sha256":null}]""");
        WorkloadPackageValidation validation = await WorkloadPackageValidator.ValidateAsync(root);
        True(!validation.Valid, "A malformed manifest must be rejected without throwing.");
        True(validation.Errors.Any(static error =>
                error.Contains("manifest", StringComparison.OrdinalIgnoreCase)
                || error.Contains("SHA-256", StringComparison.OrdinalIgnoreCase)),
            string.Join(" | ", validation.Errors));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static async Task ReloadLatestCompatibleProfileResult()
{
    string root = Path.Combine(Path.GetTempPath(), "GpuTuningLabTests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "session.json");
    try
    {
        Guid batchId = Guid.NewGuid();
        DateTimeOffset startedAt = DateTimeOffset.Now.AddMinutes(-15);
        TestRun[] baselineRuns =
        [
            MakeRun(1000, 200, 80, 2) with { BatchId = batchId, StartedAt = startedAt },
            MakeRun(1002, 200, 80, 2) with { BatchId = batchId, StartedAt = startedAt.AddMinutes(3) },
            MakeRun(998, 200, 80, 2) with { BatchId = batchId, StartedAt = startedAt.AddMinutes(6) }
        ];
        TestRun candidate = MakeRun(985, 155, 70, 2, ProfileKind.Undervolt) with
        {
            StartedAt = startedAt.AddMinutes(10)
        };
        await LabStore.SaveAsync(
            path,
            new LabSession { Runs = baselineRuns.Append(candidate).ToArray() });

        var service = new LocalGpuLabService(Policy());
        GpuProfileMeasurementResult? restored =
            await service.GetLatestProfileMeasurementResultAsync(
                path,
                null,
                candidate.Identity,
                "test-package-v1");
        True(restored?.Comparison != null, "The latest compatible result must be restored.");
        Equal(candidate.Id, restored!.Run.Id);

        GpuProfileMeasurementResult? incompatible =
            await service.GetLatestProfileMeasurementResultAsync(
                path,
                null,
                candidate.Identity,
                "test-package-v2");
        True(incompatible == null, "A result from another workload package must not be restored.");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static async Task KeepLatestResultWhenAdviceIsCorrupt()
{
    string root = Path.Combine(Path.GetTempPath(), "GpuTuningLabTests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "session.json");
    string evidencePath = Path.Combine(root, "evidence.json");
    try
    {
        Guid batchId = Guid.NewGuid();
        DateTimeOffset startedAt = DateTimeOffset.Now.AddMinutes(-15);
        TestRun[] baselineRuns =
        [
            MakeRun(1000, 200, 80, 2) with { BatchId = batchId, StartedAt = startedAt },
            MakeRun(1002, 200, 80, 2) with { BatchId = batchId, StartedAt = startedAt.AddMinutes(3) },
            MakeRun(998, 200, 80, 2) with { BatchId = batchId, StartedAt = startedAt.AddMinutes(6) }
        ];
        TestRun candidate = MakeRun(985, 155, 70, 2, ProfileKind.Undervolt) with
        {
            StartedAt = startedAt.AddMinutes(10)
        };
        await LabStore.SaveAsync(
            path,
            new LabSession { Runs = baselineRuns.Append(candidate).ToArray() });
        await File.WriteAllTextAsync(evidencePath, "{ invalid JSON");

        var service = new LocalGpuLabService(Policy());
        GpuProfileMeasurementResult? restored =
            await service.GetLatestProfileMeasurementResultAsync(
                path,
                evidencePath,
                candidate.Identity,
                "test-package-v1");

        True(restored?.Comparison != null, "A broken advice catalog must not hide the saved result.");
        Equal(candidate.Id, restored!.Run.Id);
        True(!string.IsNullOrWhiteSpace(restored.NextSuggestionMessage),
            "The optional advice failure should remain observable.");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static async Task LoadLegacyNullFingerprintSession()
{
    string root = Path.Combine(Path.GetTempPath(), "GpuTuningLabTests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "session.json");
    try
    {
        Guid batchId = Guid.NewGuid();
        TestRun[] legacyRuns =
        [
            MakeRun(1000, 200, 80, 2) with
            {
                BatchId = batchId,
                WorkloadPackageFingerprint = null
            },
            MakeRun(1002, 200, 80, 2) with
            {
                BatchId = batchId,
                WorkloadPackageFingerprint = null
            },
            MakeRun(998, 200, 80, 2) with
            {
                BatchId = batchId,
                WorkloadPackageFingerprint = null
            },
            MakeRun(985, 155, 70, 2, ProfileKind.Undervolt) with
            {
                WorkloadPackageFingerprint = null
            }
        ];
        await LabStore.SaveAsync(path, new LabSession { Runs = legacyRuns });

        var service = new LocalGpuLabService(Policy());
        GpuBaselineStatus status = await service.GetLatestBaselineStatusAsync(
            path,
            "test-package-v1");
        True(status.Exists, "The legacy baseline must remain visible.");
        True(status.Validation?.Valid == false, "The legacy baseline must be invalidated without crashing.");
        True(status.Validation!.Reasons.Any(static reason =>
                reason.Contains("predates workload package fingerprinting", StringComparison.OrdinalIgnoreCase)),
            string.Join(" | ", status.Validation.Reasons));

        GpuProfileMeasurementResult? restored =
            await service.GetLatestProfileMeasurementResultAsync(
                path,
                null,
                legacyRuns[0].Identity,
                "test-package-v1");
        True(restored == null, "A legacy profile without a package fingerprint must not be restored.");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static async Task ReferenceCatalogRules()
{
    string path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "data", "gpu-tuning", "gpu_catalog.json"));
    await using var stream = File.OpenRead(path);
    var entries = await JsonSerializer.DeserializeAsync(stream, LabJsonContext.Default.GpuReferenceEntryArray) ?? [];
    Equal(0, ReferenceCatalogValidator.Validate(entries).Count);
    True(entries.Any(entry => entry.Series == 3000), "RTX 3000 missing.");
    True(entries.Any(entry => entry.Series == 4000), "RTX 4000 missing.");
    True(entries.Any(entry => entry.Series == 5000), "RTX 5000 missing.");
    True(entries.Any(entry => entry.Model == "GeForce RTX 5050"), "RTX 5050 missing.");
    True(entries.All(entry => entry.VramMiB > 0), "Every catalog entry needs an explicit VRAM capacity.");
    True(entries.All(entry => entry.Confidence == "catalogOnly"), "No tuning envelope is trusted yet.");
}

static async Task PublishedEvidenceRegistryRules()
{
    string path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "data", "gpu-tuning", "published_tuning_evidence.json"));
    await using var stream = File.OpenRead(path);
    PublishedTuningEvidence[] entries = await JsonSerializer.DeserializeAsync(
        stream,
        LabJsonContext.Default.PublishedTuningEvidenceArray) ?? [];
    Equal(42, entries.Length);
    Equal(0, PublishedEvidenceValidator.ValidateRegistry(entries).Count);
    PublishedEvidenceReview[] reviews = entries.Select(PublishedEvidenceValidator.Review).ToArray();
    Equal(0, reviews.Count(static review => review.EligibleForVoltageEnvelope));
    Equal(0, reviews.Count(static review => review.EligibleForPowerGuidance));
    True(reviews.All(static review => review.Valid), "Published evidence must remain structurally valid.");
    True(reviews.Count(static review => review.EligibleForAdvisory) >= 10,
        "The advisory registry must contain enough usable positive observations.");
    True(entries.Any(static entry => entry.Outcome == "failed-later"),
        "Late instability evidence must remain in the registry.");
    Equal(2, entries.Count(static entry => entry.Model == "GeForce RTX 3080"));
    True(entries.Where(static entry => entry.Model == "GeForce RTX 3080")
        .All(static entry => entry.IndependentUnitId == "hardwareluxx-3080-fe-sample-1"),
        "Two settings measured on one RTX 3080 must remain one independent physical unit.");
}

static async Task ClassifyEvidenceQualityDimensions()
{
    string path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "data", "gpu-tuning", "published_tuning_evidence.json"));
    await using var stream = File.OpenRead(path);
    PublishedTuningEvidence[] entries = await JsonSerializer.DeserializeAsync(
        stream,
        LabJsonContext.Default.PublishedTuningEvidenceArray) ?? [];

    PublishedEvidenceReview completeShort = PublishedEvidenceValidator.Review(entries.Single(
        static entry => entry.EvidenceId == "reddit-5090-fe-900mv-100pct"));
    True(completeShort.ImprovesThermals, "The 5090 comparison must record lower temperature.");
    True(completeShort.MaintainsPerformance, "The 5090 comparison must retain stock performance.");
    True(completeShort.HasShortTermStability, "Twenty stress loops must count as short-term stability.");
    True(!completeShort.HasLongTermStability, "A stress test alone must not become long-term stability.");

    PublishedEvidenceReview longTermOnly = PublishedEvidenceValidator.Review(entries.Single(
        static entry => entry.EvidenceId == "reddit-5070ti-pny-870mv"));
    True(longTermOnly.HasLongTermStability, "The reported daily profile must retain its long-term flag.");
    True(!longTermOnly.ImprovesThermals,
        "A tuned temperature without a comparable stock temperature must not prove a thermal gain.");
    True(!longTermOnly.MaintainsPerformance,
        "A tuned score without a comparable stock score must not prove retained performance.");

    PublishedEvidenceReview preliminary = PublishedEvidenceValidator.Review(entries.Single(
        static entry => entry.EvidenceId == "reddit-5080-msi-expert-900mv"));
    True(preliminary.HasShortTermStability, "Three Steel Nomad passes must remain short-term evidence.");
    True(!preliminary.HasLongTermStability, "Several hours must not be labelled long-term stability.");
    True(!preliminary.ImprovesThermals && !preliminary.MaintainsPerformance,
        "Missing stock data must leave thermal and performance dimensions unproven.");
}

static async Task BuildPersonalizedEvidenceSeed()
{
    string path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "data", "gpu-tuning", "published_tuning_evidence.json"));
    await using var stream = File.OpenRead(path);
    PublishedTuningEvidence[] entries = await JsonSerializer.DeserializeAsync(
        stream,
        LabJsonContext.Default.PublishedTuningEvidenceArray) ?? [];
    TestRun baseline = MakeRun(100, 160, 65, 4) with
    {
        Identity = new GpuIdentity(
            "NVIDIA GeForce RTX 4070 SUPER", "uuid", "bus", "dev", "sub", "driver", "vbios")
    };
    GpuAdviceStatus advice = GpuProfileAdvisor.BuildInitial(
        baseline,
        new BaselineValidationResult(true, 0.2, []),
        Policy(),
        entries);

    True(advice.Available && advice.Suggestion != null, "A 4070 SUPER seed should be available.");
    Equal(1000, advice.Suggestion!.Profile.TargetVoltageMv);
    Equal(2700, advice.Suggestion.Profile.TargetClockMhz);
    Equal(0, advice.Suggestion.Profile.MemoryOffsetMhz);
    Equal(100d, advice.Suggestion.Profile.PowerLimitPercent);
    Equal(1, advice.Suggestion.IndependentUnits);
    Equal(2, advice.Suggestion.ExcludedFailurePoints);
}

static async Task BuildMeasuredNextProfile()
{
    string path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "data", "gpu-tuning", "published_tuning_evidence.json"));
    await using var stream = File.OpenRead(path);
    PublishedTuningEvidence[] entries = await JsonSerializer.DeserializeAsync(
        stream,
        LabJsonContext.Default.PublishedTuningEvidenceArray) ?? [];
    var identity = new GpuIdentity(
        "NVIDIA GeForce RTX 4070 SUPER", "uuid", "bus", "dev", "sub", "driver", "vbios");
    TestRun baseline = MakeRun(100, 160, 65, 4) with { Identity = identity };
    TestRun candidate = MakeRun(100, 125, 55, 4, ProfileKind.Undervolt) with
    {
        Identity = identity,
        Profile = new GpuTuningProfile
        {
            Name = "First seed",
            Kind = ProfileKind.Undervolt,
            TargetVoltageMv = 1000,
            TargetClockMhz = 2700,
            MemoryOffsetMhz = 0,
            PowerLimitPercent = 100
        }
    };
    var comparison = new ProfileComparison
    {
        BaselineRunId = baseline.Id,
        CandidateRunId = candidate.Id,
        PerformanceIndex = 100,
        MinimumWorkloadPerformanceIndex = 100,
        WeakestWorkloadName = "compute",
        PowerIndex = 78.1,
        EfficiencyIndex = 128,
        TemperatureDeltaC = -10,
        ThermalComparisonReliable = true,
        BalancedScore = 113,
        CandidateVerdict = StabilityVerdict.Validated,
        MeetsPerformanceFloor = true
    };
    GpuAdviceStatus advice = GpuProfileAdvisor.BuildNext(
        baseline,
        candidate,
        RunAnalyzer.Summarize(candidate, Policy()),
        comparison,
        Policy(),
        entries);

    True(advice.Available && advice.Suggestion != null, "A measured efficient pass should yield a next step.");
    Equal(975, advice.Suggestion!.Profile.TargetVoltageMv);
    Equal(2700, advice.Suggestion.Profile.TargetClockMhz);
}

static async Task IsolateMemoryBeforeRaisingVoltage()
{
    string path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "data", "gpu-tuning", "published_tuning_evidence.json"));
    await using var stream = File.OpenRead(path);
    PublishedTuningEvidence[] entries = await JsonSerializer.DeserializeAsync(
        stream,
        LabJsonContext.Default.PublishedTuningEvidenceArray) ?? [];
    var identity = new GpuIdentity(
        "NVIDIA GeForce RTX 4070 SUPER", "uuid", "bus", "dev", "sub", "driver", "vbios");
    TestRun baseline = MakeRun(100, 160, 65, 4) with { Identity = identity };
    TestRun candidate = MakeRun(82, 100, 51, 4, ProfileKind.Undervolt) with
    {
        Identity = identity,
        Profile = new GpuTuningProfile
        {
            Name = "Memory offset mixed in",
            Kind = ProfileKind.Undervolt,
            TargetVoltageMv = 920,
            TargetClockMhz = 2670,
            MemoryOffsetMhz = 1000,
            PowerLimitPercent = 100
        }
    };
    candidate = candidate with
    {
        Workloads = candidate.Workloads.Select(item => item.Kind == WorkloadKind.Vram
            ? item with { Score = 52 }
            : item).ToArray()
    };
    var comparison = new ProfileComparison
    {
        BaselineRunId = baseline.Id,
        CandidateRunId = candidate.Id,
        PerformanceIndex = 76.7,
        MinimumWorkloadPerformanceIndex = 52,
        WeakestWorkloadName = "vram",
        PowerIndex = 62.3,
        EfficiencyIndex = 123.2,
        TemperatureDeltaC = -16,
        ThermalComparisonReliable = true,
        BalancedScore = 0,
        CandidateVerdict = StabilityVerdict.Validated,
        MeetsPerformanceFloor = false
    };

    GpuAdviceStatus advice = GpuProfileAdvisor.BuildNext(
        baseline,
        candidate,
        RunAnalyzer.Summarize(candidate, Policy()),
        comparison,
        Policy(),
        entries);

    True(advice.Available && advice.Suggestion != null, "A low-performance mixed profile must yield an isolating retry.");
    Equal(920, advice.Suggestion!.Profile.TargetVoltageMv);
    Equal(2670, advice.Suggestion.Profile.TargetClockMhz);
    Equal(0, advice.Suggestion.Profile.MemoryOffsetMhz);
    True(advice.Message.Contains("mémoire 0 MHz", StringComparison.OrdinalIgnoreCase),
        "The retry message must state the exact memory reset.");
    True(advice.Message.Contains("mémoire vidéo", StringComparison.OrdinalIgnoreCase),
        "The retry message must identify the weakest workload.");
}

static async Task BlockSparseMixedEvidence()
{
    string path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "data", "gpu-tuning", "published_tuning_evidence.json"));
    await using var stream = File.OpenRead(path);
    PublishedTuningEvidence[] entries = await JsonSerializer.DeserializeAsync(
        stream,
        LabJsonContext.Default.PublishedTuningEvidenceArray) ?? [];
    TestRun source = MakeRun(100, 200, 61, 4);
    TestRun baseline = source with
    {
        Identity = new GpuIdentity(
            "NVIDIA GeForce RTX 3060 Ti", "uuid", "bus", "dev", "sub", "driver", "vbios"),
        Samples = source.Samples.Select(sample => sample with { CoreClockMhz = 1920 }).ToArray()
    };

    GpuAdviceStatus advice = GpuProfileAdvisor.BuildInitial(
        baseline,
        new BaselineValidationResult(true, 0.2, []),
        Policy(),
        entries);

    True(!advice.Available && advice.Suggestion == null,
        "One physical 3060 Ti must not be enough to generate a recommendation.");
    True(advice.Message.Contains("insuffisante", StringComparison.OrdinalIgnoreCase),
        "Sparse evidence must report why advice is unavailable.");
}

static async Task BlockEveryUnderDocumentedModel()
{
    string path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "data", "gpu-tuning", "published_tuning_evidence.json"));
    await using var stream = File.OpenRead(path);
    PublishedTuningEvidence[] entries = await JsonSerializer.DeserializeAsync(
        stream,
        LabJsonContext.Default.PublishedTuningEvidenceArray) ?? [];

    foreach (string model in entries.Select(static entry => entry.Model)
                 .Distinct(StringComparer.OrdinalIgnoreCase))
    {
        TestRun baseline = MakeRun(100, 200, 65, 4) with
        {
            Identity = new GpuIdentity($"NVIDIA {model}", "uuid", "bus", "dev", "sub", "driver", "vbios")
        };
        GpuAdviceStatus advice = GpuProfileAdvisor.BuildInitial(
            baseline,
            new BaselineValidationResult(true, 0.2, []),
            Policy(),
            entries);

        if (model.Equals("GeForce RTX 4070 SUPER", StringComparison.OrdinalIgnoreCase))
            True(advice.Available, "The sufficiently documented RTX 4070 SUPER should remain eligible.");
        else
            True(!advice.Available, $"{model} is still under-documented and must not generate advice.");
    }
}

static async Task RejectNonUndervoltSeed()
{
    string path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "data", "gpu-tuning", "published_tuning_evidence.json"));
    await using var stream = File.OpenRead(path);
    PublishedTuningEvidence[] entries = await JsonSerializer.DeserializeAsync(
        stream,
        LabJsonContext.Default.PublishedTuningEvidenceArray) ?? [];
    TestRun baseline = MakeRun(100, 160, 65, 4) with
    {
        Identity = new GpuIdentity(
            "NVIDIA GeForce RTX 4070 SUPER", "uuid", "bus", "dev", "sub", "driver", "vbios"),
        Samples = MakeRun(100, 160, 65, 4).Samples
            .Select(sample => sample with { VoltageV = 0.950 })
            .ToArray()
    };

    GpuAdviceStatus advice = GpuProfileAdvisor.BuildInitial(
        baseline,
        new BaselineValidationResult(true, 0.2, []),
        Policy(),
        entries);

    True(!advice.Available, "Tweakly must not suggest a voltage equal to or above measured stock voltage.");
}

static EvaluationPolicy Policy() => new()
{
    SamplingIntervalMs = 500,
    ShortValidationMinutes = 1,
    LongValidationMinutes = 4
};

static TestRun MakeRun(double score, double power, double temp, int minutes, ProfileKind kind = ProfileKind.Stock)
{
    var start = DateTimeOffset.Now;
    TimeSpan workloadDuration = TimeSpan.FromSeconds(minutes * 15);
    WorkloadKind[] workloadKinds = Enum.GetValues<WorkloadKind>()
        .Where(static workload => workload != WorkloadKind.Game)
        .ToArray();
    TimeSpan totalDuration = TimeSpan.FromTicks(workloadDuration.Ticks * workloadKinds.Length);
    int count = (int)Math.Ceiling(totalDuration.TotalMilliseconds / 500) + 1;
    var samples = Enumerable.Range(0, count).Select(index => new GpuTelemetrySample
    {
        Timestamp = start.AddMilliseconds(index * 500),
        TemperatureC = temp,
        PowerAverageW = power,
        RequestedPowerLimitW = 220,
        EnforcedPowerLimitW = 220,
        DefaultPowerLimitW = 220,
        CoreClockMhz = 2700,
        MemoryClockMhz = 10501,
        MaxMemoryClockMhz = 10501,
        GpuUtilizationPercent = 99
    }).ToArray();
    var workloads = workloadKinds.Select(workload => new WorkloadResult
    {
        Name = workload.ToString(),
        Version = "1",
        Kind = workload,
        Score = score,
        ScoreUnit = "points",
        Duration = workloadDuration
    }).ToArray();
    return new TestRun
    {
        StartedAt = start,
        Identity = new GpuIdentity("GPU", "uuid", "bus", "dev", "sub", "driver", "vbios"),
        Profile = new GpuTuningProfile
        {
            Name = kind.ToString(),
            Kind = kind,
            AppliedBy = kind == ProfileKind.Stock ? "manual-confirmed-stock" : "manual"
        },
        WorkloadPackageFingerprint = "test-package-v1",
        Samples = samples,
        Workloads = workloads,
        WorkloadWindows = workloads.Select((workload, index) => new WorkloadTelemetryWindow
        {
            Name = workload.Name,
            Kind = workload.Kind,
            StartedAt = start.AddTicks(workloadDuration.Ticks * index),
            EndedAt = start.AddTicks(workloadDuration.Ticks * (index + 1)),
            SampleCount = (int)Math.Ceiling(workloadDuration.TotalMilliseconds / 500) + 1
        }).ToArray()
    };
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Near(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance)
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class FixedGpuContaminationMonitor : IGpuContaminationMonitor
{
    public Task<GpuContaminationResult> ObserveAsync(int workloadProcessId, CancellationToken cancellationToken)
        => Task.FromResult(new GpuContaminationResult(
            true,
            [new ActiveGpuProcess(42, "browser.exe", 18, 4)],
            ""));
}
