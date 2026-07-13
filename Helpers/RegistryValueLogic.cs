using System;
using System.Collections.Generic;

namespace Optimisation_Tool.Helpers;

public readonly record struct GpuPriorityProfile(
    int GpuPriority,
    int Priority,
    string SchedulingCategory,
    string SfioPriority);

public static class RegistryValueLogic
{
    public const int SystemResponsivenessDefault = 20;
    public const int NetworkThrottlingDefault = 10;

    public static int SetMaskedBits(int current, int mask, bool enabled) =>
        enabled ? current | mask : current & ~mask;

    public static int EnsureBits(int current, int mask) => current | mask;

    public static GpuPriorityProfile GpuPriority(bool forced) => forced
        ? new GpuPriorityProfile(8, 6, "High", "High")
        : new GpuPriorityProfile(8, 2, "Medium", "Normal");

    public static bool IsForcedGpuPriority(
        int gpuPriority,
        int priority,
        string? schedulingCategory,
        string? sfioPriority) =>
        gpuPriority >= 8 &&
        priority >= 6 &&
        string.Equals(schedulingCategory, "High", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(sfioPriority, "High", StringComparison.OrdinalIgnoreCase);

    public static bool HasSemicolonValue(string? current, string key, string expectedValue)
    {
        foreach (var pair in SplitPairs(current))
        {
            int separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            if (pair[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase) &&
                pair[(separator + 1)..].Trim().Equals(expectedValue, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static string? SetSemicolonValue(string? current, string key, string? value)
    {
        var pairs = new List<string>();
        foreach (var pair in SplitPairs(current))
        {
            int separator = pair.IndexOf('=');
            string pairKey = separator > 0 ? pair[..separator].Trim() : pair;
            if (!pairKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                pairs.Add(pair);
        }

        if (value != null)
            pairs.Add($"{key}={value}");

        return pairs.Count == 0 ? null : string.Join(";", pairs) + ";";
    }

    private static IEnumerable<string> SplitPairs(string? current)
    {
        if (string.IsNullOrWhiteSpace(current)) yield break;
        foreach (var raw in current.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = raw.Trim();
            if (pair.Length > 0) yield return pair;
        }
    }
}
