namespace FanControl.Core;

public static class FanTemperatureStabilityAnalyzer
{
    public static bool IsSettled(
        IReadOnlyList<double> temperatures,
        int sampleWindow = 20,
        int endpointWindow = 5,
        double maximumEndpointDeltaC = 2.0,
        double maximumRecentSpreadC = 2.5)
    {
        ArgumentNullException.ThrowIfNull(temperatures);
        if (sampleWindow < endpointWindow * 2 ||
            endpointWindow < 1 ||
            maximumEndpointDeltaC < 0 ||
            maximumRecentSpreadC < 0 ||
            temperatures.Count < sampleWindow)
            return false;

        double[] window = temperatures.TakeLast(sampleWindow).ToArray();
        if (window.Any(static value => !double.IsFinite(value)))
            return false;

        double earlyAverage = window.Take(endpointWindow).Average();
        double recentAverage = window.TakeLast(endpointWindow).Average();
        double[] recent = window.TakeLast(endpointWindow * 2).ToArray();

        return Math.Abs(recentAverage - earlyAverage) <= maximumEndpointDeltaC &&
               recent.Max() - recent.Min() <= maximumRecentSpreadC;
    }
}
