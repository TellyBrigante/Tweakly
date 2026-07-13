namespace GpuTuningLab.Core;

public static class GpuTuningCompatibility
{
    public const string SupportedFamilies = "GeForce RTX 3000, 4000 et 5000 de bureau";

    public static bool IsSupported(GpuIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return IsSupportedModelName(identity.Name);
    }

    public static bool IsSupportedModelName(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        string name = modelName.Trim().ToUpperInvariant();
        if (name.Contains("LAPTOP", StringComparison.Ordinal)
            || name.Contains("NOTEBOOK", StringComparison.Ordinal)
            || name.Contains("MAX-Q", StringComparison.Ordinal))
            return false;

        return name.Contains("GEFORCE RTX 30", StringComparison.Ordinal)
               || name.Contains("GEFORCE RTX 40", StringComparison.Ordinal)
               || name.Contains("GEFORCE RTX 50", StringComparison.Ordinal);
    }
}
