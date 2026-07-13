using System;

namespace Optimisation_Tool.Helpers;

public enum BatteryResumeAction
{
    None,
    TelemetryGapWithoutRestart,
    RestIncomplete,
    RestComplete
}

public readonly record struct BatteryResumeDecision(
    BatteryCalibrationPhase Phase,
    DateTime PhaseStartedAt,
    double VerifiedRestSeconds,
    BatteryResumeAction Action,
    TimeSpan OfflineDuration,
    bool RecoveredPhase);

public static class BatteryResumeEvaluator
{
    private static readonly TimeSpan TelemetryGap = TimeSpan.FromMinutes(30);

    public static BatteryResumeDecision Evaluate(
        BatteryCalibrationPhase sessionPhase,
        DateTime phaseStartedAt,
        double verifiedRestSeconds,
        BatteryCalibrationPhase lastSamplePhase,
        DateTime lastSampleAt,
        DateTime now,
        DateTime? systemBootTime,
        TimeSpan restTarget)
    {
        var phase = sessionPhase;
        var phaseStart = phaseStartedAt;
        bool recovered = false;

        if (phase != BatteryCalibrationPhase.Complete &&
            IsActive(lastSamplePhase) &&
            PhaseOrder(phase) < PhaseOrder(lastSamplePhase))
        {
            phase = lastSamplePhase;
            phaseStart = lastSampleAt;
            recovered = true;
        }

        var gap = now - lastSampleAt;
        if (phase == BatteryCalibrationPhase.Drain && gap >= TelemetryGap)
        {
            if (!TryGetVerifiedOfflineDuration(lastSampleAt, now, systemBootTime, out var offline))
                return new BatteryResumeDecision(
                    phase, phaseStart, verifiedRestSeconds,
                    BatteryResumeAction.TelemetryGapWithoutRestart,
                    TimeSpan.Zero, recovered);

            return RestDecision(phase, phaseStart, now, offline, restTarget, recovered);
        }

        if (phase == BatteryCalibrationPhase.Rest &&
            TryGetVerifiedOfflineDuration(lastSampleAt, now, systemBootTime, out var restOffline))
            return RestDecision(phase, phaseStart, now, restOffline, restTarget, recovered);

        return new BatteryResumeDecision(
            phase, phaseStart, verifiedRestSeconds,
            BatteryResumeAction.None, TimeSpan.Zero, recovered);
    }

    private static BatteryResumeDecision RestDecision(
        BatteryCalibrationPhase currentPhase,
        DateTime phaseStartedAt,
        DateTime now,
        TimeSpan offline,
        TimeSpan restTarget,
        bool recovered)
    {
        bool complete = offline >= restTarget;
        return new BatteryResumeDecision(
            complete ? BatteryCalibrationPhase.Recharge : BatteryCalibrationPhase.Rest,
            complete
                ? DateTime.MinValue
                : currentPhase == BatteryCalibrationPhase.Rest ? phaseStartedAt : now,
            offline.TotalSeconds,
            complete ? BatteryResumeAction.RestComplete : BatteryResumeAction.RestIncomplete,
            offline,
            recovered);
    }

    private static bool TryGetVerifiedOfflineDuration(
        DateTime lastSampleAt,
        DateTime now,
        DateTime? systemBootTime,
        out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (!systemBootTime.HasValue) return false;

        DateTime lastUtc = ToUtc(lastSampleAt);
        DateTime nowUtc = ToUtc(now);
        DateTime bootUtc = ToUtc(systemBootTime.Value);
        if (bootUtc <= lastUtc.AddSeconds(30) || bootUtc > nowUtc.AddMinutes(2)) return false;

        duration = bootUtc - lastUtc;
        return duration > TimeSpan.Zero;
    }

    private static DateTime ToUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc) return value;
        if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
        try { return TimeZoneInfo.ConvertTimeToUtc(value, TimeZoneInfo.Local); }
        catch { return DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(); }
    }

    private static bool IsActive(BatteryCalibrationPhase phase) =>
        phase is not BatteryCalibrationPhase.Idle and not BatteryCalibrationPhase.Complete;

    private static int PhaseOrder(BatteryCalibrationPhase phase) => phase switch
    {
        BatteryCalibrationPhase.Idle => 0,
        BatteryCalibrationPhase.ChargeToFull => 1,
        BatteryCalibrationPhase.CellBalance => 2,
        BatteryCalibrationPhase.Drain => 3,
        BatteryCalibrationPhase.Rest => 4,
        BatteryCalibrationPhase.Recharge => 5,
        BatteryCalibrationPhase.Complete => 6,
        _ => 0
    };
}
