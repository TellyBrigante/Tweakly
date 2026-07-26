using System.Text.Json.Serialization;

namespace GpuTuningLab.Core;

public enum ProfileKind
{
    Stock,
    Undervolt,
    Overclock,
    Mixed
}

public enum WorkloadKind
{
    Compute,
    Graphics,
    RayTracing,
    Vram,
    Transient,
    Game
}

public enum StabilityEventKind
{
    DriverReset,
    Tdr,
    ApplicationCrash,
    Artifact,
    Whea,
    TelemetryGap,
    ManualStop
}

public enum StabilityVerdict
{
    Rejected,
    InvalidTelemetry,
    TelemetryOnly,
    Exploratory,
    ShortPass,
    Validated
}

public enum RecommendationKind
{
    MeasureStock,
    RepeatRun,
    RestoreLastStable,
    IncreaseVoltageOrReduceClock,
    ReducePowerOrVoltage,
    ValidateLonger,
    ExploreLowerVoltage,
    KeepProfile
}

[Flags]
public enum NvidiaClockEventReasons : ulong
{
    None = 0,
    GpuIdle = 0x1,
    ApplicationsClocks = 0x2,
    SoftwarePowerCap = 0x4,
    HardwareSlowdown = 0x8,
    SyncBoost = 0x10,
    SoftwareThermalSlowdown = 0x20,
    HardwareThermalSlowdown = 0x40,
    HardwarePowerBrake = 0x80,
    DisplayClock = 0x100
}

public sealed record GpuIdentity(
    string Name,
    string Uuid,
    string PciBusId,
    string DeviceId,
    string SubsystemId,
    string DriverVersion,
    string VbiosVersion);

public sealed record VoltageFrequencyPoint(int VoltageMv, int FrequencyMhz);

public sealed record GpuTuningProfile
{
    public required string Name { get; init; }
    public required ProfileKind Kind { get; init; }
    public double? PowerLimitPercent { get; init; }
    public int? CoreOffsetMhz { get; init; }
    public int? MemoryOffsetMhz { get; init; }
    public int? TargetVoltageMv { get; init; }
    public int? TargetClockMhz { get; init; }
    public IReadOnlyList<VoltageFrequencyPoint> Curve { get; init; } = [];
    public string AppliedBy { get; init; } = "manual";
    public IReadOnlyList<string> VerificationEvidence { get; init; } = [];
}

public sealed record GpuTelemetrySample
{
    public required DateTimeOffset Timestamp { get; init; }
    public double? TemperatureC { get; init; }
    public double? HotspotTemperatureC { get; init; }
    public double? MemoryTemperatureC { get; init; }
    public double? PowerAverageW { get; init; }
    public double? PowerInstantW { get; init; }
    public double? RequestedPowerLimitW { get; init; }
    public double? EnforcedPowerLimitW { get; init; }
    public double? DefaultPowerLimitW { get; init; }
    public double? MinPowerLimitW { get; init; }
    public double? MaxPowerLimitW { get; init; }
    public double? CoreClockMhz { get; init; }
    public double? MemoryClockMhz { get; init; }
    public double? MaxCoreClockMhz { get; init; }
    public double? MaxMemoryClockMhz { get; init; }
    public double? VoltageV { get; init; }
    public double? GpuUtilizationPercent { get; init; }
    public double? MemoryUtilizationPercent { get; init; }
    public double? VramUsedMiB { get; init; }
    public double? VramTotalMiB { get; init; }
    public double? FanPercent { get; init; }
    public string PerformanceState { get; init; } = "N/A";
    public NvidiaClockEventReasons ClockEventReasons { get; init; }
}

public sealed record TelemetryCapture(
    GpuIdentity Identity,
    IReadOnlyList<GpuTelemetrySample> Samples,
    IReadOnlyList<string> Warnings);

public sealed record WorkloadResult
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required WorkloadKind Kind { get; init; }
    public required double Score { get; init; }
    public required string ScoreUnit { get; init; }
    public required TimeSpan Duration { get; init; }
    public bool Completed { get; init; } = true;
    public string FailureReason { get; init; } = "";
    public int? Iterations { get; init; }
    public double? ReportedWrittenGiB { get; init; }
    public double? ReportedCheckedGiB { get; init; }
    public double? FrameTimeP99Ms { get; init; }
    public double? ScoreVariancePercent { get; init; }
}

public sealed record StabilityEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required StabilityEventKind Kind { get; init; }
    public required string Evidence { get; init; }
}

public sealed record WorkloadTelemetryWindow
{
    public required string Name { get; init; }
    public required WorkloadKind Kind { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required int SampleCount { get; init; }
}

public sealed record TestRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? BatchId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required GpuIdentity Identity { get; init; }
    public required GpuTuningProfile Profile { get; init; }
    public string? WorkloadPackageFingerprint { get; init; }
    public IReadOnlyList<GpuTelemetrySample> Samples { get; init; } = [];
    public IReadOnlyList<WorkloadResult> Workloads { get; init; } = [];
    public IReadOnlyList<WorkloadTelemetryWindow> WorkloadWindows { get; init; } = [];
    public IReadOnlyList<StabilityEvent> StabilityEvents { get; init; } = [];
    public string Notes { get; init; } = "";
}

public sealed record RunSummary
{
    public required Guid RunId { get; init; }
    public required StabilityVerdict Verdict { get; init; }
    public required TimeSpan TelemetryDuration { get; init; }
    public required double TelemetryCoveragePercent { get; init; }
    public double? AverageTemperatureC { get; init; }
    public double? P95TemperatureC { get; init; }
    public double? MaxTemperatureC { get; init; }
    public double? AveragePowerW { get; init; }
    public double? P95PowerW { get; init; }
    public double? MaxPowerW { get; init; }
    public double? AverageCoreClockMhz { get; init; }
    public double? P05CoreClockMhz { get; init; }
    public double? StartingTemperatureC { get; init; }
    public double EnergyWh { get; init; }
    public double PowerLimitTimePercent { get; init; }
    public double ThermalLimitTimePercent { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
}

public sealed record ProfileComparison
{
    public required Guid BaselineRunId { get; init; }
    public required Guid CandidateRunId { get; init; }
    public required double PerformanceIndex { get; init; }
    public required double MinimumWorkloadPerformanceIndex { get; init; }
    public required string WeakestWorkloadName { get; init; }
    public required double PowerIndex { get; init; }
    public required double EfficiencyIndex { get; init; }
    public double? TemperatureDeltaC { get; init; }
    public required bool ThermalComparisonReliable { get; init; }
    public double? StartingTemperatureDeltaC { get; init; }
    public required double BalancedScore { get; init; }
    public required StabilityVerdict CandidateVerdict { get; init; }
    public required bool MeetsPerformanceFloor { get; init; }
}

public sealed record ProfileRankingRow(
    TestRun Run,
    RunSummary Summary,
    ProfileComparison Comparison,
    bool ParetoEfficient,
    int Rank);

public sealed record BaselineValidationResult(
    bool Valid,
    double ScoreCoefficientOfVariationPercent,
    IReadOnlyList<string> Reasons);

public sealed record Recommendation(
    RecommendationKind Kind,
    string Reason,
    int ConfidencePercent,
    int? SuggestedVoltageStepMv = null);

public sealed record SafetyGateResult(
    bool Allowed,
    IReadOnlyList<string> BlockingReasons);

public sealed record ProfileApplicationAssessment
{
    public required bool Verified { get; init; }
    public IReadOnlyList<string> BlockingReasons { get; init; } = [];
    public int? ObservedVoltageMv { get; init; }
    public int? ObservedClockMhz { get; init; }
    public int? ObservedMemoryOffsetMhz { get; init; }
    public double? ObservedPowerLimitPercent { get; init; }
}

public sealed record EvaluationPolicy
{
    public int SamplingIntervalMs { get; init; } = 500;
    public double MinimumTelemetryCoveragePercent { get; init; } = 95;
    public double MinimumRequiredMetricCoveragePercent { get; init; } = 95;
    public double MinimumPerformanceRetentionPercent { get; init; } = 97;
    public double MinimumIndividualWorkloadRetentionPercent { get; init; } = 90;
    public double MaximumBenchmarkVariancePercent { get; init; } = 2;
    public double MaximumStartingTemperatureDeltaC { get; init; } = 3;
    public int MinimumBaselineWorkloadSeconds { get; init; } = 30;
    public int ShortValidationMinutes { get; init; } = 10;
    public int LongValidationMinutes { get; init; } = 60;
    public int VoltageStepMv { get; init; } = 25;
    public int ProfileVoltageToleranceMv { get; init; } = 25;
    public int ProfileClockToleranceMhz { get; init; } = 90;
    public int ProfileMemoryOffsetToleranceMhz { get; init; } = 100;
    public double ProfilePowerLimitTolerancePercent { get; init; } = 1.5;
    public double PerformanceWeight { get; init; } = 0.35;
    public double EfficiencyWeight { get; init; } = 0.45;
    public double ThermalWeight { get; init; } = 0.20;
    public WorkloadKind[] RequiredValidationWorkloads { get; init; } =
        [WorkloadKind.Graphics, WorkloadKind.RayTracing, WorkloadKind.Vram, WorkloadKind.Transient];
}

public sealed record EvidenceSource
{
    public required string Url { get; init; }
    public required string Publisher { get; init; }
    public required DateOnly RetrievedOn { get; init; }
    public required string EvidenceType { get; init; }
    public string Title { get; init; } = "";
    public DateOnly? PublishedOn { get; init; }
    public string MeasurementProtocol { get; init; } = "";
}

public sealed record GpuTuningObservation
{
    public required string ObservationId { get; init; }
    public required string IndependentUnitId { get; init; }
    public required string Model { get; init; }
    public required string FormFactor { get; init; }
    public required string BoardPartner { get; init; }
    public required string VbiosVersion { get; init; }
    public required string DriverVersion { get; init; }
    public required string CoolingClass { get; init; }
    public string DeviceId { get; init; } = "";
    public string SubsystemId { get; init; } = "";
    public int? VramMiB { get; init; }
    public double? DefaultPowerLimitW { get; init; }
    public double? MinimumPowerLimitW { get; init; }
    public double? MaximumPowerLimitW { get; init; }
    public double? AmbientTemperatureC { get; init; }
    public required string ProtocolVersion { get; init; }
    public required GpuTuningProfile Profile { get; init; }
    public required RunSummary Summary { get; init; }
    public required double PerformanceIndex { get; init; }
    public double? EfficiencyIndex { get; init; }
    public required double BenchmarkVariancePercent { get; init; }
    public required EvidenceSource Source { get; init; }
}

public sealed record ReferenceBuildPolicy
{
    public int MinimumIndependentUnits { get; init; } = 5;
    public int MinimumIndependentPublishers { get; init; } = 3;
    public double MinimumTelemetryCoveragePercent { get; init; } = 90;
    public double MaximumBenchmarkVariancePercent { get; init; } = 2;
    public double MinimumPerformanceRetentionPercent { get; init; } = 97;
}

public sealed record ReferenceBuildResult(
    string Model,
    bool Eligible,
    string Confidence,
    SearchEnvelope? Envelope,
    int AcceptedObservations,
    int IndependentUnits,
    int IndependentPublishers,
    IReadOnlyList<string> RejectionReasons);

public sealed record SearchEnvelope
{
    public int? MinimumVoltageMv { get; init; }
    public int? MaximumVoltageMv { get; init; }
    public int? MinimumClockMhz { get; init; }
    public int? MaximumClockMhz { get; init; }
    public int? MaximumMemoryOffsetMhz { get; init; }
    public int? VoltageStepMv { get; init; }
}

public sealed record GpuReferenceEntry
{
    public required string Model { get; init; }
    public required int Series { get; init; }
    public required string Architecture { get; init; }
    public required string FormFactor { get; init; }
    public required string Confidence { get; init; }
    public int? VramMiB { get; init; }
    public IReadOnlyList<string> DeviceIds { get; init; } = [];
    public double? ReferenceBoardPowerW { get; init; }
    public SearchEnvelope? SearchEnvelope { get; init; }
    public IReadOnlyList<EvidenceSource> Sources { get; init; } = [];
}

public sealed record PublishedGpuPoint
{
    public int? VoltageMv { get; init; }
    public int? ClockMhz { get; init; }
    public int? MemoryOffsetMhz { get; init; }
    public double? PowerLimitPercent { get; init; }
    public double? Score { get; init; }
    public string ScoreUnit { get; init; } = "";
    public double? PowerW { get; init; }
    public double? TemperatureC { get; init; }
}

public sealed record PublishedTuningEvidence
{
    public required string EvidenceId { get; init; }
    public required string IndependentUnitId { get; init; }
    public required string Model { get; init; }
    public int? VramMiB { get; init; }
    public required string BoardModel { get; init; }
    public required string FormFactor { get; init; }
    public required string Method { get; init; }
    public required string Benchmark { get; init; }
    public double? DurationSeconds { get; init; }
    public int? StablePasses { get; init; }
    public bool LongTermStabilityReported { get; init; }
    public string Outcome { get; init; } = "reported-stable";
    public string OutcomeNotes { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public double? AmbientTemperatureC { get; init; }
    public required PublishedGpuPoint Stock { get; init; }
    public required PublishedGpuPoint Tuned { get; init; }
    public required EvidenceSource Source { get; init; }
}

public sealed record PublishedEvidenceReview(
    string EvidenceId,
    bool Valid,
    bool EligibleForAdvisory,
    bool EligibleForVoltageEnvelope,
    bool EligibleForPowerGuidance,
    double? PerformanceIndex,
    double? PowerIndex,
    double? EfficiencyIndex,
    bool ImprovesThermals,
    bool MaintainsPerformance,
    bool HasShortTermStability,
    bool HasLongTermStability,
    IReadOnlyList<string> ValidationErrors,
    IReadOnlyList<string> EligibilityReasons);

public sealed record GpuProfileSuggestion
{
    public required GpuTuningProfile Profile { get; init; }
    public required string Confidence { get; init; }
    public required string Summary { get; init; }
    public int IndependentUnits { get; init; }
    public int IndependentSources { get; init; }
    public int SupportingPoints { get; init; }
    public int ExcludedFailurePoints { get; init; }
    public int? PublicAnchorVoltageMv { get; init; }
    public int? PublicAnchorClockMhz { get; init; }
    public double? StockClockMhz { get; init; }
    public IReadOnlyList<string> SourceUrls { get; init; } = [];
}

public sealed record GpuAdviceStatus(
    bool Available,
    string Message,
    GpuProfileSuggestion? Suggestion);

public sealed record LabSession
{
    public int SchemaVersion { get; init; } = 2;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<TestRun> Runs { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LabSession))]
[JsonSerializable(typeof(EvaluationPolicy))]
[JsonSerializable(typeof(GpuReferenceEntry[]))]
[JsonSerializable(typeof(GpuTuningObservation[]))]
[JsonSerializable(typeof(PublishedTuningEvidence[]))]
public partial class LabJsonContext : JsonSerializerContext;
