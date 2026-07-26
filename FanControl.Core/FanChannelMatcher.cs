namespace FanControl.Core;

public sealed record FanTachometerDescriptor(
    string HardwareId,
    int Index,
    string Id,
    string Name,
    double? Rpm);

public sealed record FanControlDescriptor(
    string HardwareId,
    int Index,
    string Id,
    string Name,
    double? CurrentPercent,
    double MinimumPercent,
    double MaximumPercent,
    bool Writable);

public sealed record MatchedFanChannel(
    string HardwareId,
    int Index,
    FanTachometerDescriptor Tachometer,
    FanControlDescriptor Control);

public static class FanChannelMatcher
{
    public static IReadOnlyList<MatchedFanChannel> MatchActiveChannels(
        IEnumerable<FanTachometerDescriptor> tachometers,
        IEnumerable<FanControlDescriptor> controls)
    {
        ArgumentNullException.ThrowIfNull(tachometers);
        ArgumentNullException.ThrowIfNull(controls);

        var controlByKey = controls
            .Where(IsUsableControl)
            .GroupBy(x => (x.HardwareId, x.Index))
            .ToDictionary(x => x.Key, x => x.First());

        var result = new List<MatchedFanChannel>();
        foreach (FanTachometerDescriptor tachometer in tachometers)
        {
            if (tachometer.Rpm is null || !double.IsFinite(tachometer.Rpm.Value) ||
                tachometer.Rpm.Value < FanSafetyPolicy.MinimumReadableRpm)
                continue;

            if (!controlByKey.TryGetValue((tachometer.HardwareId, tachometer.Index), out FanControlDescriptor? control))
                continue;

            result.Add(new MatchedFanChannel(
                tachometer.HardwareId,
                tachometer.Index,
                tachometer,
                control));
        }

        return result
            .OrderBy(x => x.HardwareId, StringComparer.Ordinal)
            .ThenBy(x => x.Index)
            .ToArray();
    }

    private static bool IsUsableControl(FanControlDescriptor control) =>
        control.Writable &&
        control.MinimumPercent >= 0 &&
        control.MaximumPercent <= 100 &&
        control.MinimumPercent < control.MaximumPercent;
}
