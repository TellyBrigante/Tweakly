namespace FanControl.Core;

public static class FanControlLoop
{
    public static FanControlDecision Decide(FanControlTick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        if (tick.Curve.Count < 2)
            return new(FanControlActionKind.RestoreHardwareDefault, 0, "The fan curve is invalid.");

        double telemetryAge = (tick.Now - tick.LastTelemetryAt).TotalSeconds;
        if (telemetryAge < 0 || telemetryAge > tick.MaximumTelemetryAgeSeconds ||
            !double.IsFinite(tick.TemperatureC))
            return new(FanControlActionKind.RestoreHardwareDefault, 0, "Temperature telemetry is stale.");

        if (tick.TemperatureC >= tick.EmergencyTemperatureC)
            return new(FanControlActionKind.SetDuty, 100, "Emergency temperature reached.");

        IReadOnlyList<FanCurvePoint> curve;
        try
        {
            curve = FanCurvePlanner.Normalize(tick.Curve);
        }
        catch (ArgumentException)
        {
            return new(FanControlActionKind.RestoreHardwareDefault, 0, "The fan curve is invalid.");
        }

        double requested = Interpolate(curve, tick.TemperatureC);
        double elapsed = Math.Clamp(tick.ElapsedSeconds, 0, 10);
        if (requested >= tick.CurrentDutyPercent)
        {
            double rampUp = Math.Clamp(tick.RampUpPercentPerSecond, 1, 100);
            double gradualIncrease = Math.Min(
                requested,
                tick.CurrentDutyPercent + (rampUp * elapsed));
            return new(FanControlActionKind.SetDuty, gradualIncrease, "Fan speed increases gradually.");
        }

        double rampDown = Math.Clamp(tick.RampDownPercentPerSecond, 1, 100);
        double slowDecrease = Math.Max(
            requested,
            tick.CurrentDutyPercent - (rampDown * elapsed));
        return new(FanControlActionKind.SetDuty, slowDecrease, "Fan speed decreases gradually.");
    }

    public static double ApplyTemperatureHysteresis(
        double measuredTemperatureC,
        double previousControlTemperatureC,
        double hysteresisC)
    {
        if (!double.IsFinite(measuredTemperatureC))
            return double.NaN;
        if (!double.IsFinite(previousControlTemperatureC))
            return measuredTemperatureC;

        double hysteresis = Math.Clamp(hysteresisC, 0, 10);
        if (measuredTemperatureC >= previousControlTemperatureC)
            return measuredTemperatureC;
        if (measuredTemperatureC <= previousControlTemperatureC - hysteresis)
            return measuredTemperatureC + hysteresis;
        return previousControlTemperatureC;
    }

    private static double Interpolate(IReadOnlyList<FanCurvePoint> curve, double temperatureC)
    {
        if (temperatureC <= curve[0].TemperatureC)
            return curve[0].DutyPercent;

        for (int i = 1; i < curve.Count; i++)
        {
            FanCurvePoint upper = curve[i];
            FanCurvePoint lower = curve[i - 1];
            if (temperatureC > upper.TemperatureC)
                continue;

            double span = upper.TemperatureC - lower.TemperatureC;
            double ratio = (temperatureC - lower.TemperatureC) / span;
            return lower.DutyPercent + ((upper.DutyPercent - lower.DutyPercent) * ratio);
        }

        return curve[^1].DutyPercent;
    }
}
