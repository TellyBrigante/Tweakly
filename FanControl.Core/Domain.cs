namespace FanControl.Core;

public enum FanDriveMode
{
    Unknown,
    Pwm,
    Dc
}

public enum FanRole
{
    Unknown,
    Chassis,
    Cpu,
    Gpu,
    Radiator,
    Pump
}

public enum ThermalSource
{
    Cpu,
    Gpu,
    Mixed
}

public enum WorkloadLevel
{
    Idle,
    Moderate,
    Heavy
}

public sealed record FanChannelCapability
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public FanRole Role { get; init; }
    public FanDriveMode DriveMode { get; init; }
    public bool HasRpmFeedback { get; init; }
    public bool HasWritableControl { get; init; }
    public double? CurrentRpm { get; init; }
    public double MinControlPercent { get; init; }
    public double MaxControlPercent { get; init; } = 100;
}

public sealed record FanEligibility(bool Allowed, string Reason);

public sealed record FanResponseSample
{
    public required double DutyPercent { get; init; }
    public required double Rpm { get; init; }
    public required double TemperatureC { get; init; }
    public required bool Stable { get; init; }
}

public sealed record FanRestartSample
{
    public required double DutyPercent { get; init; }
    public required int Attempts { get; init; }
    public required int SuccessfulStarts { get; init; }
}

public sealed record FanCalibrationResult
{
    public required bool IsValid { get; init; }
    public required string FailureReason { get; init; }
    public required FanDriveMode DriveMode { get; init; }
    public required double MinimumStableDutyPercent { get; init; }
    public required double RestartDutyPercent { get; init; }
    public required double MaximumObservedRpm { get; init; }
}

public sealed record ThermalTrial
{
    public required WorkloadLevel Workload { get; init; }
    public required double DutyPercent { get; init; }
    public required double MaximumTemperatureC { get; init; }
    public required bool Stable { get; init; }
    public bool Throttled { get; init; }
    public double? ObservedRpm { get; init; }
}

public sealed record FanCurvePoint(double TemperatureC, double DutyPercent);

public sealed record FanCurvePlan
{
    public required ThermalSource Source { get; init; }
    public required IReadOnlyList<FanCurvePoint> Points { get; init; }
    public required double TargetLoadTemperatureC { get; init; }
    public required double EmergencyTemperatureC { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

public sealed record FanCurvePlanRequest
{
    public required ThermalSource Source { get; init; }
    public required FanCalibrationResult Calibration { get; init; }
    public required IReadOnlyList<ThermalTrial> Trials { get; init; }
    public required double IdleTemperatureC { get; init; }
    public required double ThermalLimitC { get; init; }
    public double SafetyMarginC { get; init; } = 10;
}

public enum FanControlActionKind
{
    SetDuty,
    RestoreHardwareDefault
}

public sealed record FanControlDecision(FanControlActionKind Action, double DutyPercent, string Reason);

public sealed record FanControlTick
{
    public required DateTimeOffset Now { get; init; }
    public required DateTimeOffset LastTelemetryAt { get; init; }
    public required double TemperatureC { get; init; }
    public required double CurrentDutyPercent { get; init; }
    public required double ElapsedSeconds { get; init; }
    public required IReadOnlyList<FanCurvePoint> Curve { get; init; }
    public required double EmergencyTemperatureC { get; init; }
    public double MaximumTelemetryAgeSeconds { get; init; } = 3;
    public double RampUpPercentPerSecond { get; init; } = 12;
    public double RampDownPercentPerSecond { get; init; } = 3;
}
