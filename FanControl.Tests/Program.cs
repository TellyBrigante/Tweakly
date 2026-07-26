using FanControl.Core;

var tests = new (string Name, Action Run)[]
{
    ("Only rotating writable channels are offered", OnlyRotatingWritableChannelsAreOffered),
    ("Only paired active motherboard channels are matched", OnlyPairedActiveMotherboardChannelsAreMatched),
    ("Pumps are always excluded", PumpsAreAlwaysExcluded),
    ("PWM calibration keeps a safety margin", PwmCalibrationKeepsSafetyMargin),
    ("DC calibration keeps a larger safety margin", DcCalibrationKeepsLargerSafetyMargin),
    ("Restart must succeed three times", RestartMustSucceedThreeTimes),
    ("Curve is monotonic and ends at 100 percent", CurveIsMonotonicAndEndsAtMaximum),
    ("Measured thermal headroom advances full speed", MeasuredThermalHeadroomAdvancesFullSpeed),
    ("Recalibration rebuilds the complete automatic curve", RecalibrationRebuildsCompleteAutomaticCurve),
    ("Insufficient thermal headroom blocks automatic curve", InsufficientThermalHeadroomBlocksAutomaticCurve),
    ("Missing thermal evidence falls back to 100 percent", MissingThermalEvidenceFallsBackToMaximum),
    ("Stale telemetry restores hardware control", StaleTelemetryRestoresHardwareControl),
    ("Emergency temperature forces 100 percent", EmergencyTemperatureForcesMaximum),
    ("Ramp up is limited", RampUpIsLimited),
    ("Ramp down is limited", RampDownIsLimited),
    ("Temperature hysteresis prevents oscillation", TemperatureHysteresisPreventsOscillation),
    ("Stable temperature is settled", StableTemperatureIsSettled),
    ("Rising temperature is not settled", RisingTemperatureIsNotSettled),
    ("Falling temperature is not settled", FallingTemperatureIsNotSettled),
    ("Oscillating temperature is not settled", OscillatingTemperatureIsNotSettled),
    ("A rising fan is not stable", RisingFanIsNotStable),
    ("A settled fan is stable", SettledFanIsStable),
    ("A noisy fan is not stable", NoisyFanIsNotStable)
};

int passed = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        passed++;
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("FAIL " + name + ": " + ex.Message);
        return 1;
    }
}

Console.WriteLine($"FanControl tests: {passed}/{tests.Length} OK");
return 0;

static void OnlyRotatingWritableChannelsAreOffered()
{
    True(FanSafetyPolicy.CanOfferControl(Channel()).Allowed, "Expected eligible channel.");
    True(!FanSafetyPolicy.CanOfferControl(Channel() with { HasRpmFeedback = false }).Allowed, "Missing RPM must block.");
    True(!FanSafetyPolicy.CanOfferControl(Channel() with { HasWritableControl = false }).Allowed, "Read-only must block.");
    True(!FanSafetyPolicy.CanOfferControl(Channel() with { CurrentRpm = 0 }).Allowed, "Stopped fan must block.");
}

static void PumpsAreAlwaysExcluded() =>
    True(!FanSafetyPolicy.CanOfferControl(Channel() with { Role = FanRole.Pump }).Allowed, "Pump must block.");

static void OnlyPairedActiveMotherboardChannelsAreMatched()
{
    FanTachometerDescriptor[] fans =
    [
        new("board", 0, "fan/0", "Fan #1", 0),
        new("board", 1, "fan/1", "Fan #2", 640),
        new("board", 2, "fan/2", "Fan #3", 710),
        new("gpu", 0, "gpu/fan/0", "GPU Fan 1", 1300)
    ];
    FanControlDescriptor[] controls =
    [
        new("board", 0, "control/0", "Fan #1", 60, 0, 100, true),
        new("board", 1, "control/1", "Fan #2", 35, 0, 100, true),
        new("board", 2, "control/2", "Fan #3", 35, 0, 100, true),
        new("gpu", 0, "gpu/control/0", "GPU Fan 1", 30, 30, 100, true)
    ];

    IReadOnlyList<MatchedFanChannel> matched = FanChannelMatcher.MatchActiveChannels(
        fans.Where(x => x.HardwareId == "board"),
        controls.Where(x => x.HardwareId == "board"));

    Equal(2, matched.Count);
    Equal(1, matched[0].Index);
    Equal(2, matched[1].Index);
}

static void PwmCalibrationKeepsSafetyMargin()
{
    FanCalibrationResult result = FanCalibrationAnalyzer.Analyze(
        FanDriveMode.Pwm,
        Responses(),
        [new() { DutyPercent = 30, Attempts = 3, SuccessfulStarts = 3 }]);
    True(result.IsValid, result.FailureReason);
    Equal(25, result.MinimumStableDutyPercent);
    Equal(30, result.RestartDutyPercent);
}

static void DcCalibrationKeepsLargerSafetyMargin()
{
    FanCalibrationResult result = FanCalibrationAnalyzer.Analyze(
        FanDriveMode.Dc,
        Responses(),
        [new() { DutyPercent = 35, Attempts = 3, SuccessfulStarts = 3 }]);
    True(result.IsValid, result.FailureReason);
    Equal(30, result.MinimumStableDutyPercent);
    Equal(35, result.RestartDutyPercent);
}

static void RestartMustSucceedThreeTimes()
{
    FanCalibrationResult result = FanCalibrationAnalyzer.Analyze(
        FanDriveMode.Unknown,
        Responses(),
        [new() { DutyPercent = 40, Attempts = 3, SuccessfulStarts = 2 }]);
    True(!result.IsValid, "Unreliable restart must invalidate calibration.");
}

static void CurveIsMonotonicAndEndsAtMaximum()
{
    FanCurvePlan plan = FanCurvePlanner.Build(Request());
    for (int i = 1; i < plan.Points.Count; i++)
        True(plan.Points[i].DutyPercent >= plan.Points[i - 1].DutyPercent, "Duty decreased.");
    Equal(100, plan.Points[^1].DutyPercent);
    True(plan.Points.Count == 5, "The protective curve must expose five progressive points.");
    True(plan.Points[^2].DutyPercent >= 85, "The pre-full-speed point must be at least 85 percent.");
    True(plan.Points[^3].DutyPercent >= 70, "The heavy-load point must be at least 70 percent.");
}

static void MeasuredThermalHeadroomAdvancesFullSpeed()
{
    FanCurvePlan plan = FanCurvePlanner.Build(MeasuredRequest());

    Equal(45, Math.Round(plan.Points[0].TemperatureC));
    Equal(30, plan.Points[0].DutyPercent);
    Equal(56, Math.Round(plan.Points[1].TemperatureC));
    Equal(40, plan.Points[1].DutyPercent);
    Equal(65, Math.Round(plan.Points[2].TemperatureC));
    Equal(70, plan.Points[2].DutyPercent);
    Equal(71, Math.Round(plan.Points[3].TemperatureC));
    Equal(85, plan.Points[3].DutyPercent);
    Equal(76, plan.Points[^1].TemperatureC);
    Equal(100, plan.Points[^1].DutyPercent);
    True(plan.Points[^2].TemperatureC < 76, "The pre-full point must remain below full speed.");
    True(plan.Points[^2].DutyPercent >= 85, "The pre-full point must retain at least 85 percent.");
}

static void RecalibrationRebuildsCompleteAutomaticCurve()
{
    FanCurvePlan first = FanCurvePlanner.Build(MeasuredRequest());
    FanCurvePlan hotter = FanCurvePlanner.Build(MeasuredRequest() with
    {
        Trials = MeasuredRequest().Trials
            .Select(trial => trial with { MaximumTemperatureC = trial.MaximumTemperatureC + 14 })
            .ToArray()
    });

    True(!CurvesEqual(first.Points, hotter.Points), "New thermal measurements must rebuild the curve.");
    True(hotter.Points[^1].TemperatureC < first.Points[^1].TemperatureC,
        "Hotter evidence must move full speed to a lower temperature.");
}

static void InsufficientThermalHeadroomBlocksAutomaticCurve()
{
    bool rejected = false;
    try
    {
        FanCurvePlanner.Build(MeasuredRequest() with { IdleTemperatureC = 74 });
    }
    catch (ArgumentException)
    {
        rejected = true;
    }

    True(rejected, "An automatic curve must not be created without enough thermal headroom.");
}

static void MissingThermalEvidenceFallsBackToMaximum()
{
    FanCurvePlan plan = FanCurvePlanner.Build(Request() with { Trials = [] });
    Equal(100, plan.Points[1].DutyPercent);
    Equal(100, plan.Points[2].DutyPercent);
}

static void StaleTelemetryRestoresHardwareControl()
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    FanControlDecision decision = FanControlLoop.Decide(Tick(now) with { LastTelemetryAt = now.AddSeconds(-4) });
    True(decision.Action == FanControlActionKind.RestoreHardwareDefault, "Stale telemetry must restore default.");
}

static void EmergencyTemperatureForcesMaximum()
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    FanControlDecision decision = FanControlLoop.Decide(Tick(now) with { TemperatureC = 92 });
    True(decision.Action == FanControlActionKind.SetDuty, "Emergency must retain software control.");
    Equal(100, decision.DutyPercent);
}

static void RampDownIsLimited()
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    FanControlDecision decision = FanControlLoop.Decide(Tick(now) with
    {
        TemperatureC = 35,
        CurrentDutyPercent = 80,
        ElapsedSeconds = 1
    });
    Equal(77, decision.DutyPercent);
}

static void RampUpIsLimited()
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    FanControlDecision decision = FanControlLoop.Decide(Tick(now) with
    {
        TemperatureC = 80,
        CurrentDutyPercent = 30,
        ElapsedSeconds = 1,
        RampUpPercentPerSecond = 12
    });
    Equal(42, decision.DutyPercent);
}

static void TemperatureHysteresisPreventsOscillation()
{
    Equal(60, FanControlLoop.ApplyTemperatureHysteresis(59, 60, 2));
    Equal(59, FanControlLoop.ApplyTemperatureHysteresis(57, 60, 2));
    Equal(61, FanControlLoop.ApplyTemperatureHysteresis(61, 60, 2));
}

static void StableTemperatureIsSettled() =>
    True(FanTemperatureStabilityAnalyzer.IsSettled(
        [60.0, 60.2, 59.9, 60.1, 60.0, 60.2, 60.1, 59.8, 60.0, 60.1,
         60.0, 59.9, 60.1, 60.0, 60.2, 60.1, 59.9, 60.0, 60.1, 60.0]),
        "A stable thermal window should validate.");

static void RisingTemperatureIsNotSettled() =>
    True(!FanTemperatureStabilityAnalyzer.IsSettled(
        Enumerable.Range(0, 20).Select(index => 50.0 + index * 0.4).ToArray()),
        "A temperature that is still rising must not validate.");

static void FallingTemperatureIsNotSettled() =>
    True(!FanTemperatureStabilityAnalyzer.IsSettled(
        Enumerable.Range(0, 20).Select(index => 70.0 - index * 0.4).ToArray()),
        "A temperature that is still falling must not validate.");

static void OscillatingTemperatureIsNotSettled() =>
    True(!FanTemperatureStabilityAnalyzer.IsSettled(
        Enumerable.Range(0, 20).Select(index => index % 2 == 0 ? 58.0 : 62.0).ToArray()),
        "A temperature that still oscillates must not validate.");

static void RisingFanIsNotStable()
{
    FanRpmStabilityResult result = FanRpmStabilityAnalyzer.AnalyzeWindow([600, 850, 1100, 1350, 1500]);
    True(!result.Stable, "A fan that is still accelerating must not validate.");
}

static void SettledFanIsStable()
{
    FanRpmStabilityResult result = FanRpmStabilityAnalyzer.AnalyzeWindow([1492, 1508, 1497, 1511, 1502]);
    True(result.Stable, "A settled fan should validate.");
}

static void NoisyFanIsNotStable()
{
    FanRpmStabilityResult result = FanRpmStabilityAnalyzer.AnalyzeWindow([1300, 1580, 1370, 1610, 1320]);
    True(!result.Stable, "Persistent oscillation must not validate.");
}


static FanChannelCapability Channel() => new()
{
    Id = "fan/1",
    DisplayName = "Fan 1",
    Role = FanRole.Chassis,
    DriveMode = FanDriveMode.Unknown,
    HasRpmFeedback = true,
    HasWritableControl = true,
    CurrentRpm = 900,
    MinControlPercent = 0,
    MaxControlPercent = 100
};

static IReadOnlyList<FanResponseSample> Responses() =>
[
    new() { DutyPercent = 20, Rpm = 400, TemperatureC = 38, Stable = true },
    new() { DutyPercent = 40, Rpm = 800, TemperatureC = 36, Stable = true },
    new() { DutyPercent = 70, Rpm = 1300, TemperatureC = 34, Stable = true },
    new() { DutyPercent = 100, Rpm = 1800, TemperatureC = 32, Stable = true }
];

static FanCurvePlanRequest Request() => new()
{
    Source = ThermalSource.Mixed,
    Calibration = new()
    {
        IsValid = true,
        FailureReason = "",
        DriveMode = FanDriveMode.Pwm,
        MinimumStableDutyPercent = 25,
        RestartDutyPercent = 30,
        MaximumObservedRpm = 1800
    },
    IdleTemperatureC = 35,
    ThermalLimitC = 95,
    SafetyMarginC = 15,
    Trials =
    [
        new() { Workload = WorkloadLevel.Moderate, DutyPercent = 35, MaximumTemperatureC = 70, Stable = true },
        new() { Workload = WorkloadLevel.Moderate, DutyPercent = 45, MaximumTemperatureC = 66, Stable = true },
        new() { Workload = WorkloadLevel.Heavy, DutyPercent = 55, MaximumTemperatureC = 79, Stable = true },
        new() { Workload = WorkloadLevel.Heavy, DutyPercent = 70, MaximumTemperatureC = 74, Stable = true }
    ]
};

static FanCurvePlanRequest MeasuredRequest() => new()
{
    Source = ThermalSource.Cpu,
    Calibration = new()
    {
        IsValid = true,
        FailureReason = "",
        DriveMode = FanDriveMode.Pwm,
        MinimumStableDutyPercent = 30,
        RestartDutyPercent = 30,
        MaximumObservedRpm = 1485
    },
    IdleTemperatureC = 44.6666666667,
    ThermalLimitC = 90,
    SafetyMarginC = 12,
    Trials =
    [
        new() { Workload = WorkloadLevel.Moderate, DutyPercent = 70, MaximumTemperatureC = 50, Stable = true },
        new() { Workload = WorkloadLevel.Moderate, DutyPercent = 55, MaximumTemperatureC = 52, Stable = true },
        new() { Workload = WorkloadLevel.Moderate, DutyPercent = 30, MaximumTemperatureC = 56, Stable = true },
        new() { Workload = WorkloadLevel.Heavy, DutyPercent = 100, MaximumTemperatureC = 56, Stable = true },
        new() { Workload = WorkloadLevel.Heavy, DutyPercent = 85, MaximumTemperatureC = 53, Stable = true },
        new() { Workload = WorkloadLevel.Heavy, DutyPercent = 70, MaximumTemperatureC = 58, Stable = true },
        new() { Workload = WorkloadLevel.Heavy, DutyPercent = 55, MaximumTemperatureC = 58, Stable = true },
        new() { Workload = WorkloadLevel.Heavy, DutyPercent = 30, MaximumTemperatureC = 61, Stable = true }
    ]
};

static bool CurvesEqual(IReadOnlyList<FanCurvePoint> left, IReadOnlyList<FanCurvePoint> right)
{
    if (left.Count != right.Count) return false;
    for (int index = 0; index < left.Count; index++)
    {
        if (Math.Abs(left[index].TemperatureC - right[index].TemperatureC) > 0.001 ||
            Math.Abs(left[index].DutyPercent - right[index].DutyPercent) > 0.001)
            return false;
    }
    return true;
}

static FanControlTick Tick(DateTimeOffset now) => new()
{
    Now = now,
    LastTelemetryAt = now,
    TemperatureC = 60,
    CurrentDutyPercent = 60,
    ElapsedSeconds = 1,
    Curve =
    [
        new(35, 30),
        new(60, 55),
        new(80, 75),
        new(92, 100)
    ],
    EmergencyTemperatureC = 92
};

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Equal(double expected, double actual)
{
    if (Math.Abs(expected - actual) > 0.001)
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}
