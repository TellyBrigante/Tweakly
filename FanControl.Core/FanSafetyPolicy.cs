namespace FanControl.Core;

public static class FanSafetyPolicy
{
    public const double MinimumReadableRpm = 100;
    public const double MaximumCalibrationStartTemperatureC = 70;

    public static FanEligibility CanOfferControl(FanChannelCapability channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.Role == FanRole.Pump)
            return new(false, "Pump channels are excluded from automatic calibration.");
        if (!channel.HasRpmFeedback)
            return new(false, "No RPM feedback is available.");
        if (!channel.HasWritableControl)
            return new(false, "The channel is read-only.");
        if (channel.CurrentRpm is null || channel.CurrentRpm < MinimumReadableRpm)
            return new(false, "The fan is not currently rotating.");
        if (channel.MinControlPercent < 0 || channel.MaxControlPercent > 100 ||
            channel.MinControlPercent >= channel.MaxControlPercent)
            return new(false, "The reported control range is invalid.");

        return new(true, "RPM feedback and writable control are available.");
    }

    public static FanEligibility CanStartCalibration(FanChannelCapability channel, double hottestTemperatureC)
    {
        FanEligibility control = CanOfferControl(channel);
        if (!control.Allowed)
            return control;
        if (!double.IsFinite(hottestTemperatureC) || hottestTemperatureC < 0)
            return new(false, "Temperature telemetry is unavailable.");
        if (hottestTemperatureC > MaximumCalibrationStartTemperatureC)
            return new(false, "The system is too hot to start calibration.");

        return new(true, "Calibration can start.");
    }
}
