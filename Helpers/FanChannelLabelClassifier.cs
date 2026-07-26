using FanControl.Core;

namespace Optimisation_Tool.Helpers;

public static class FanChannelLabelClassifier
{
    public static FanRole InferRole(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return FanRole.Unknown;

        string normalized = name.Trim()
            .Replace('-', '_')
            .Replace(' ', '_')
            .ToUpperInvariant();

        if (ContainsAny(normalized, "AIO_PUMP", "W_PUMP", "WATER_PUMP", "PUMP_FAN", "PUMPFAN"))
            return FanRole.Pump;
        if (ContainsAny(normalized, "RADIATOR", "RAD_FAN"))
            return FanRole.Radiator;
        if (ContainsAny(normalized, "CPU_FAN", "CPUFAN", "CPU_OPT"))
            return FanRole.Cpu;
        if (ContainsAny(normalized, "CHASSIS_FAN", "CHA_FAN", "SYSTEM_FAN", "SYS_FAN", "CASE_FAN"))
            return FanRole.Chassis;

        return FanRole.Unknown;
    }

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.Ordinal));
}
