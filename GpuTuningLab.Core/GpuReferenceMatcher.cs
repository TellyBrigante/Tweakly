using System.Text.RegularExpressions;

namespace GpuTuningLab.Core;

public static partial class GpuReferenceMatcher
{
    private const int VramToleranceMiB = 384;

    public static string NormalizeModelName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = WhitespaceRegex().Replace(value.Trim(), " ");
        return NvidiaPrefixRegex().Replace(normalized, "").Trim();
    }

    public static bool SameModel(string left, string right)
        => NormalizeModelName(left).Equals(NormalizeModelName(right), StringComparison.OrdinalIgnoreCase);

    public static bool SameFamily(string left, string right)
        => FamilyName(left).Equals(FamilyName(right), StringComparison.OrdinalIgnoreCase);

    public static GpuReferenceEntry? Find(
        GpuIdentity identity,
        double? detectedVramMiB,
        IReadOnlyList<GpuReferenceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string family = FamilyName(identity.Name);
        GpuReferenceEntry[] candidates = entries
            .Where(entry => entry.FormFactor.Equals("desktop", StringComparison.OrdinalIgnoreCase))
            .Where(entry => FamilyName(entry.Model).Equals(family, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (detectedVramMiB.HasValue)
        {
            GpuReferenceEntry[] matchingVram = candidates
                .Where(entry => entry.VramMiB.HasValue
                    && Math.Abs(entry.VramMiB.Value - detectedVramMiB.Value) <= VramToleranceMiB)
                .ToArray();
            if (matchingVram.Length == 1) return matchingVram[0];
            if (matchingVram.Length > 1) return null;
            if (candidates.Any(static entry => entry.VramMiB.HasValue)) return null;
        }

        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string FamilyName(string value)
        => VramSuffixRegex().Replace(NormalizeModelName(value), "").Trim();

    [GeneratedRegex(@"^NVIDIA\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaPrefixRegex();

    [GeneratedRegex(@"\s+\d+\s*GB$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VramSuffixRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
