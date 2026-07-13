namespace GpuTuningLab.Core;

public sealed record StockStateAssessment(
    bool ObservableStateMatchesStock,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Warnings);

public static class StockStateVerifier
{
    private const double PowerToleranceW = 0.5;
    private const double MemoryClockToleranceMhz = 25;

    public static StockStateAssessment Assess(IReadOnlyList<GpuTelemetrySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var blocking = new List<string>();
        var warnings = new List<string>();
        if (samples.Count == 0)
            return new(false, ["No telemetry is available to verify the observable stock state."], []);

        GpuTelemetrySample[] powerSamples = samples.Where(static sample =>
            sample.RequestedPowerLimitW.HasValue
            && sample.EnforcedPowerLimitW.HasValue
            && sample.DefaultPowerLimitW.HasValue).ToArray();
        if (powerSamples.Length == 0)
        {
            blocking.Add("Power-limit telemetry is unavailable.");
        }
        else if (powerSamples.Any(sample =>
                     Math.Abs(sample.RequestedPowerLimitW!.Value - sample.DefaultPowerLimitW!.Value) > PowerToleranceW
                     || Math.Abs(sample.EnforcedPowerLimitW!.Value - sample.DefaultPowerLimitW!.Value) > PowerToleranceW))
        {
            blocking.Add("The requested or enforced power limit does not match the GPU default power limit.");
        }

        GpuTelemetrySample[] memorySamples = samples.Where(static sample =>
            sample.MemoryClockMhz.HasValue && sample.MaxMemoryClockMhz.HasValue).ToArray();
        if (memorySamples.Length == 0)
        {
            blocking.Add("Memory-clock telemetry is unavailable.");
        }
        else if (memorySamples.Any(sample =>
                     sample.MemoryClockMhz!.Value > sample.MaxMemoryClockMhz!.Value + MemoryClockToleranceMhz))
        {
            blocking.Add("The active memory clock exceeds the GPU stock maximum clock.");
        }

        warnings.Add("The voltage-frequency curve cannot be proven stock through nvidia-smi; manual reset confirmation is still required.");
        return new(blocking.Count == 0, blocking.Distinct().ToArray(), warnings);
    }
}
