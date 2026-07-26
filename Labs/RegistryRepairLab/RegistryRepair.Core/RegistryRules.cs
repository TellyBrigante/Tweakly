namespace RegistryRepair.Core;

public enum RegistryFindingState
{
    Healthy,
    Missing,
    WrongType,
    WrongData,
    Unreadable,
    NotApplicable,
}

public enum RegistryCorrectionPolicy
{
    None,
    SetDocumentedValue,
}

public enum RuleEvidenceLevel
{
    Informational,
    Correlated,
    MicrosoftDocumentedExact,
}

public sealed record RegistryRule(
    string Id,
    string Title,
    RegistryAddress Address,
    RawRegistryValue ExpectedValue,
    int MinimumBuild,
    int? MaximumBuild,
    IReadOnlySet<string> SupportedEditions,
    Uri Source,
    RuleEvidenceLevel Evidence,
    RegistryCorrectionPolicy CorrectionPolicy,
    string Reason)
{
    public bool AppliesTo(WindowsIdentity windows)
    {
        if (windows.Build < MinimumBuild ||
            (MaximumBuild.HasValue && windows.Build > MaximumBuild.Value))
            return false;

        return SupportedEditions.Count == 0 ||
               SupportedEditions.Contains(windows.Edition);
    }

    public bool HasTrustedCorrectionSource =>
        Evidence == RuleEvidenceLevel.MicrosoftDocumentedExact &&
        CorrectionPolicy == RegistryCorrectionPolicy.SetDocumentedValue &&
        Source.Scheme == Uri.UriSchemeHttps &&
        (Source.Host.Equals("learn.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
         Source.Host.Equals("support.microsoft.com", StringComparison.OrdinalIgnoreCase));
}

public sealed record RegistryFinding(
    RegistryRule Rule,
    RegistryFindingState State,
    RegistrySnapshot? Observed,
    string? ReadError,
    bool CanRepair)
{
    public string? ObservedFingerprint => Observed?.Fingerprint();
}
