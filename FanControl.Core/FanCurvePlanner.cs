namespace FanControl.Core;

public static class FanCurvePlanner
{
    private const double MinimumThermalReserveC = 2;
    private const double MaximumAdditionalMeasuredReserveC = 2;
    private const double MinimumAutomaticCurveSpanC = 5;
    private const double ModeratePointPosition = 0.35;
    private const double HeavyPointPosition = 0.65;
    private const double PreFullPointPosition = 0.84;

    public static FanCurvePlan Build(FanCurvePlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Calibration.IsValid)
            throw new ArgumentException("A valid calibration is required.", nameof(request));
        if (request.ThermalLimitC <= 0 || request.SafetyMarginC < 5 ||
            request.SafetyMarginC >= request.ThermalLimitC)
            throw new ArgumentOutOfRangeException(nameof(request), "The thermal limit or safety margin is invalid.");

        double target = request.ThermalLimitC - request.SafetyMarginC;
        if (request.IdleTemperatureC >= target)
            throw new ArgumentException("Idle temperature is already above the load target.", nameof(request));

        double moderateDuty = SelectLowestPassingDuty(request.Trials, WorkloadLevel.Moderate, target - 8);
        double heavyDuty = SelectLowestPassingDuty(request.Trials, WorkloadLevel.Heavy, target);
        double floor = Math.Max(request.Calibration.MinimumStableDutyPercent, request.Calibration.RestartDutyPercent);

        moderateDuty = Math.Clamp(Math.Max(moderateDuty, floor + 10), floor, 100);
        heavyDuty = Math.Clamp(Math.Max(heavyDuty, Math.Max(moderateDuty, 70)), moderateDuty, 100);
        double preFullDuty = Math.Clamp(Math.Max(heavyDuty, 85), heavyDuty, 100);

        double fullSpeedTemperature = CalculateFullSpeedTemperature(request, target);
        double temperatureRange = fullSpeedTemperature - request.IdleTemperatureC;
        if (temperatureRange < MinimumAutomaticCurveSpanC)
            throw new ArgumentException(
                "The measured idle temperature leaves insufficient headroom for an automatic curve.",
                nameof(request));
        double moderateTemperature = request.IdleTemperatureC + (temperatureRange * ModeratePointPosition);
        double heavyTemperature = request.IdleTemperatureC + (temperatureRange * HeavyPointPosition);
        double preFullTemperature = request.IdleTemperatureC + (temperatureRange * PreFullPointPosition);
        IReadOnlyList<FanCurvePoint> points = Normalize(
        [
            new(request.IdleTemperatureC, floor),
            new(moderateTemperature, moderateDuty),
            new(heavyTemperature, heavyDuty),
            new(preFullTemperature, preFullDuty),
            new(fullSpeedTemperature, 100)
        ]);

        var notes = new List<string>();
        if (!request.Trials.Any(x => x.Workload == WorkloadLevel.Moderate && Passes(x, target - 8)))
            notes.Add("No moderate-load duty met the target; 100% was retained.");
        if (!request.Trials.Any(x => x.Workload == WorkloadLevel.Heavy && Passes(x, target)))
            notes.Add("No heavy-load duty met the target; 100% was retained.");

        return new FanCurvePlan
        {
            Source = request.Source,
            Points = points,
            TargetLoadTemperatureC = target,
            EmergencyTemperatureC = fullSpeedTemperature,
            Notes = notes
        };
    }

    private static double CalculateFullSpeedTemperature(FanCurvePlanRequest request, double target)
    {
        double? measuredHeavyMaximum = request.Trials
            .Where(static trial => trial.Workload == WorkloadLevel.Heavy && trial.Stable &&
                                   !trial.Throttled && double.IsFinite(trial.MaximumTemperatureC))
            .Select(static trial => (double?)trial.MaximumTemperatureC)
            .Max();

        double availableSpan = target - request.IdleTemperatureC;
        double measuredThermalPressure = measuredHeavyMaximum.HasValue
            ? Math.Clamp(
                (measuredHeavyMaximum.Value - request.IdleTemperatureC) / availableSpan,
                0,
                1)
            : 0;
        double reserve = MinimumThermalReserveC +
                         (measuredThermalPressure * MaximumAdditionalMeasuredReserveC);
        return Math.Ceiling(target - reserve);
    }

    public static IReadOnlyList<FanCurvePoint> Normalize(IEnumerable<FanCurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        FanCurvePoint[] ordered = points.OrderBy(x => x.TemperatureC).ToArray();
        if (ordered.Length < 2)
            throw new ArgumentException("At least two curve points are required.", nameof(points));

        var normalized = new List<FanCurvePoint>(ordered.Length);
        double previousTemperature = double.NegativeInfinity;
        double previousDuty = 0;
        foreach (FanCurvePoint point in ordered)
        {
            if (!double.IsFinite(point.TemperatureC) || !double.IsFinite(point.DutyPercent))
                throw new ArgumentException("Curve values must be finite.", nameof(points));
            if (point.TemperatureC <= previousTemperature)
                throw new ArgumentException("Curve temperatures must be unique.", nameof(points));

            double duty = Math.Clamp(Math.Max(point.DutyPercent, previousDuty), 0, 100);
            normalized.Add(new FanCurvePoint(point.TemperatureC, duty));
            previousTemperature = point.TemperatureC;
            previousDuty = duty;
        }

        return normalized;
    }

    private static double SelectLowestPassingDuty(
        IEnumerable<ThermalTrial> trials,
        WorkloadLevel workload,
        double targetTemperatureC) => trials
        .Where(x => x.Workload == workload && Passes(x, targetTemperatureC))
        .Select(x => x.DutyPercent)
        .DefaultIfEmpty(100)
        .Min();

    private static bool Passes(ThermalTrial trial, double targetTemperatureC) =>
        trial.Stable && !trial.Throttled &&
        double.IsFinite(trial.MaximumTemperatureC) &&
        trial.MaximumTemperatureC <= targetTemperatureC;
}
