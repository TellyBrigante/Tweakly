namespace FanControl.Core;

public sealed record FanRpmStabilityResult(
    bool Stable,
    double RepresentativeRpm,
    double SpreadRatio,
    double EndpointDriftRatio);

public static class FanRpmStabilityAnalyzer
{
    public const int WindowSize = 5;

    public static FanRpmStabilityResult AnalyzeWindow(IReadOnlyList<double> rpm)
    {
        ArgumentNullException.ThrowIfNull(rpm);
        if (rpm.Count < WindowSize || rpm.Any(value => !double.IsFinite(value)))
            return new(false, 0, double.PositiveInfinity, double.PositiveInfinity);

        double[] window = rpm.TakeLast(WindowSize).ToArray();
        double[] ordered = window.OrderBy(value => value).ToArray();
        double median = ordered[ordered.Length / 2];
        if (median < FanSafetyPolicy.MinimumReadableRpm)
            return new(false, median, double.PositiveInfinity, double.PositiveInfinity);

        double spread = (ordered[^1] - ordered[0]) / median;
        double endpointDrift = Math.Abs(window[^1] - window[0]) / median;
        bool stable = spread <= 0.10 && endpointDrift <= 0.05;
        return new(stable, median, spread, endpointDrift);
    }
}
