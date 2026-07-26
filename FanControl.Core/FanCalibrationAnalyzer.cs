namespace FanControl.Core;

public static class FanCalibrationAnalyzer
{
    private const int RequiredRestartAttempts = 3;

    public static FanCalibrationResult Analyze(
        FanDriveMode driveMode,
        IReadOnlyList<FanResponseSample> responseSamples,
        IReadOnlyList<FanRestartSample> restartSamples)
    {
        ArgumentNullException.ThrowIfNull(responseSamples);
        ArgumentNullException.ThrowIfNull(restartSamples);

        FanResponseSample[] usable = responseSamples
            .Where(x => x.Stable && double.IsFinite(x.Rpm) && x.Rpm >= FanSafetyPolicy.MinimumReadableRpm)
            .OrderBy(x => x.DutyPercent)
            .ToArray();

        if (usable.Length < 3)
            return Invalid(driveMode, "At least three stable RPM points are required.");

        double maximumRpm = usable.Max(x => x.Rpm);
        if (maximumRpm < FanSafetyPolicy.MinimumReadableRpm)
            return Invalid(driveMode, "No usable RPM response was measured.");

        double margin = driveMode == FanDriveMode.Pwm ? 5 : 10;
        double minimumStable = Math.Clamp(usable[0].DutyPercent + margin, 0, 100);

        FanRestartSample? restart = restartSamples
            .Where(x => x.Attempts >= RequiredRestartAttempts && x.SuccessfulStarts == x.Attempts)
            .OrderBy(x => x.DutyPercent)
            .FirstOrDefault();

        if (restart is null)
            return Invalid(driveMode, "No duty restarted the fan successfully three times.");

        double restartDuty = Math.Clamp(Math.Max(restart.DutyPercent, minimumStable), 0, 100);
        if (restartDuty >= 100)
            return Invalid(driveMode, "A safe restart duty could not be established.");

        return new FanCalibrationResult
        {
            IsValid = true,
            FailureReason = "",
            DriveMode = driveMode,
            MinimumStableDutyPercent = minimumStable,
            RestartDutyPercent = restartDuty,
            MaximumObservedRpm = maximumRpm
        };
    }

    private static FanCalibrationResult Invalid(FanDriveMode mode, string reason) => new()
    {
        IsValid = false,
        FailureReason = reason,
        DriveMode = mode,
        MinimumStableDutyPercent = 100,
        RestartDutyPercent = 100,
        MaximumObservedRpm = 0
    };
}
