using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RegistryRepair.Core;

public sealed record RegistryRuleCatalog(
    int SchemaVersion,
    string CatalogVersion,
    DateTimeOffset GeneratedAt,
    string Target,
    IReadOnlyList<RegistryRule> Rules);

public sealed record SignedRegistryRuleCatalog(
    string Algorithm,
    string KeyId,
    string Payload,
    string Signature);

public sealed class RegistryRuleCatalogException : Exception
{
    public RegistryRuleCatalogException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public static partial class RegistryRuleCatalogLoader
{
    public const string SupportedAlgorithm = "RSA-PSS-SHA256";
    public const int SupportedSchemaVersion = 1;
    private const int MaximumEnvelopeBytes = 4 * 1024 * 1024;
    private const int MaximumPayloadBytes = 2 * 1024 * 1024;
    private const int MaximumRuleCount = 2048;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static RegistryRuleCatalog LoadAndVerify(
        ReadOnlySpan<byte> envelopeJson,
        ReadOnlySpan<char> publicKeyPem,
        string expectedKeyId)
    {
        try
        {
            if (envelopeJson.Length == 0 || envelopeJson.Length > MaximumEnvelopeBytes)
                throw new RegistryRuleCatalogException(
                    "The registry rule catalog envelope size is invalid.");
            SignedRegistryRuleCatalog envelope =
                JsonSerializer.Deserialize<SignedRegistryRuleCatalog>(envelopeJson, JsonOptions)
                ?? throw new RegistryRuleCatalogException("The registry rule catalog is empty.");

            if (string.IsNullOrWhiteSpace(envelope.Algorithm) ||
                string.IsNullOrWhiteSpace(envelope.KeyId) ||
                string.IsNullOrWhiteSpace(envelope.Payload) ||
                string.IsNullOrWhiteSpace(envelope.Signature))
                throw new RegistryRuleCatalogException(
                    "The signed registry rule catalog envelope is incomplete.");

            if (!string.Equals(
                    envelope.Algorithm,
                    SupportedAlgorithm,
                    StringComparison.Ordinal))
                throw new RegistryRuleCatalogException(
                    $"Unsupported catalog signature algorithm: {envelope.Algorithm}.");
            if (!string.Equals(envelope.KeyId, expectedKeyId, StringComparison.Ordinal))
                throw new RegistryRuleCatalogException("The catalog signing key is not trusted.");

            byte[] payload = Convert.FromBase64String(envelope.Payload);
            if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
                throw new RegistryRuleCatalogException(
                    "The signed registry rule catalog payload size is invalid.");
            byte[] signature = Convert.FromBase64String(envelope.Signature);
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            if (rsa.KeySize < 2048)
                throw new RegistryRuleCatalogException(
                    "The catalog signing key must be at least 2048 bits.");
            if (!rsa.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
                throw new RegistryRuleCatalogException(
                    "The registry rule catalog signature is invalid.");

            RegistryRuleCatalog catalog =
                JsonSerializer.Deserialize<RegistryRuleCatalog>(payload, JsonOptions)
                ?? throw new RegistryRuleCatalogException(
                    "The signed registry rule catalog payload is empty.");
            Validate(catalog);
            return catalog;
        }
        catch (RegistryRuleCatalogException)
        {
            throw;
        }
        catch (Exception error) when (
            error is JsonException or FormatException or CryptographicException)
        {
            throw new RegistryRuleCatalogException(
                "Unable to validate the signed registry rule catalog.",
                error);
        }
    }

    public static void Validate(RegistryRuleCatalog catalog)
    {
        if (catalog.SchemaVersion != SupportedSchemaVersion)
            throw new RegistryRuleCatalogException(
                $"Unsupported catalog schema: {catalog.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(catalog.CatalogVersion))
            throw new RegistryRuleCatalogException("The catalog version is required.");
        if (!Version.TryParse(catalog.CatalogVersion, out _))
            throw new RegistryRuleCatalogException("The catalog version is invalid.");
        if (!string.Equals(catalog.Target, "Windows11", StringComparison.Ordinal))
            throw new RegistryRuleCatalogException("The catalog target must be Windows11.");
        if (catalog.Rules is null || catalog.Rules.Count > MaximumRuleCount)
            throw new RegistryRuleCatalogException("The catalog rule count is invalid.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RegistryRule rule in catalog.Rules)
        {
            if (rule is null ||
                rule.Address is null ||
                rule.ExpectedValue is null ||
                rule.ExpectedValue.Data is null ||
                rule.SupportedEditions is null ||
                rule.Source is null)
                throw new RegistryRuleCatalogException(
                    "The catalog contains an incomplete rule.");
            if (!RuleIdPattern().IsMatch(rule.Id))
                throw new RegistryRuleCatalogException($"Invalid rule identifier: {rule.Id}.");
            if (!ids.Add(rule.Id))
                throw new RegistryRuleCatalogException($"Duplicate rule identifier: {rule.Id}.");
            if (string.IsNullOrWhiteSpace(rule.Title) || string.IsNullOrWhiteSpace(rule.Reason))
                throw new RegistryRuleCatalogException(
                    $"Rule {rule.Id} must have a title and a reason.");
            if (rule.MinimumBuild < 22000 ||
                rule.MaximumBuild.HasValue && rule.MaximumBuild.Value < rule.MinimumBuild)
                throw new RegistryRuleCatalogException(
                    $"Rule {rule.Id} has an invalid Windows 11 build range.");
            if (rule.CorrectionPolicy != RegistryCorrectionPolicy.None &&
                rule.SupportedEditions.Count == 0)
                throw new RegistryRuleCatalogException(
                    $"Corrective rule {rule.Id} must list every supported Windows edition.");

            if (string.IsNullOrWhiteSpace(rule.Address.KeyPath) ||
                rule.Address.ValueName is null)
                throw new RegistryRuleCatalogException(
                    $"Rule {rule.Id} has an incomplete registry address.");
            RegistryAddress normalized = rule.Address.Normalize();
            if (!string.Equals(normalized.KeyPath, rule.Address.KeyPath, StringComparison.Ordinal) ||
                rule.Address.KeyPath.Contains('*', StringComparison.Ordinal) ||
                rule.Address.ValueName.Contains('*', StringComparison.Ordinal))
                throw new RegistryRuleCatalogException(
                    $"Rule {rule.Id} must use one exact normalized registry address.");

            ValidateRawValue(rule.Id, rule.ExpectedValue);
            if (rule.CorrectionPolicy != RegistryCorrectionPolicy.None &&
                !rule.HasTrustedCorrectionSource)
                throw new RegistryRuleCatalogException(
                    $"Corrective rule {rule.Id} has no exact trusted Microsoft source.");
        }
    }

    private static void ValidateRawValue(string ruleId, RawRegistryValue value)
    {
        if (value.Data.Length > 1024 * 1024)
            throw new RegistryRuleCatalogException(
                $"Rule {ruleId} exceeds the 1 MiB value limit.");

        bool valid = value.Type switch
        {
            RegistryValueType.DWord => value.Data.Length == sizeof(int),
            RegistryValueType.QWord => value.Data.Length == sizeof(long),
            RegistryValueType.String or RegistryValueType.ExpandString =>
                value.Data.Length >= sizeof(char) &&
                value.Data.Length % sizeof(char) == 0 &&
                value.Data[^1] == 0 &&
                value.Data[^2] == 0,
            RegistryValueType.MultiString =>
                value.Data.Length >= sizeof(char) * 2 &&
                value.Data.Length % sizeof(char) == 0 &&
                value.Data[^1] == 0 &&
                value.Data[^2] == 0 &&
                value.Data[^3] == 0 &&
                value.Data[^4] == 0,
            RegistryValueType.None or RegistryValueType.Binary => true,
            _ => false,
        };
        if (!valid)
            throw new RegistryRuleCatalogException(
                $"Rule {ruleId} contains invalid raw registry data for type {value.Type}.");
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleIdPattern();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
        };
        options.Converters.Add(new ReadOnlyStringSetConverter());
        return options;
    }

    private sealed class ReadOnlyStringSetConverter : JsonConverter<IReadOnlySet<string>>
    {
        public override IReadOnlySet<string> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            string[] values = JsonSerializer.Deserialize<string[]>(ref reader, options)
                ?? throw new JsonException("The edition set cannot be null.");
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlySet<string> value,
            JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
