using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FanControl.Core;

namespace Optimisation_Tool.Helpers;

public sealed record FanChannelMetadata(string DisplayName, FanRole Role, string Source);

public static class FanVendorMetadataResolver
{
    public static IReadOnlyDictionary<int, FanChannelMetadata> Read(string motherboardName)
    {
        if (string.IsNullOrWhiteSpace(motherboardName))
            return new Dictionary<int, FanChannelMetadata>();

        var result = new Dictionary<int, FanChannelMetadata>();

        try
        {
            MergeMissing(result, ReadBundledCatalog(motherboardName));
        }
        catch (Exception ex)
        {
            AppLog.ErrorOnce("fan-header-catalog", "Ventilation : catalogue des connecteurs illisible", ex);
        }

        // Une fiche exacte Tweakly est autonome et fait autorité pour ce modèle.
        if (result.Count > 0)
            return result;

        try
        {
            IReadOnlyDictionary<int, FanChannelMetadata> asus = ReadAsusFanXpert(motherboardName);
            MergeMissing(result, asus);
        }
        catch (Exception ex)
        {
            AppLog.ErrorOnce("fan-vendor-metadata-asus", "Ventilation : lecture des noms ASUS impossible", ex);
        }

        return result;
    }

    private static IReadOnlyDictionary<int, FanChannelMetadata> ReadBundledCatalog(string motherboardName)
    {
        if (!File.Exists(PathLayout.FanHeaderCatalog))
            return new Dictionary<int, FanChannelMetadata>();

        string normalizedModel = NormalizeMotherboardModel(motherboardName);
        string? manufacturer = DetectManufacturer(motherboardName);
        using FileStream stream = File.OpenRead(PathLayout.FanHeaderCatalog);
        FanHeaderCatalogDocument? catalog = JsonSerializer.Deserialize<FanHeaderCatalogDocument>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        FanHeaderBoard? board = catalog?.Boards.FirstOrDefault(candidate =>
            (manufacturer is null || string.Equals(candidate.Manufacturer, manufacturer, StringComparison.OrdinalIgnoreCase)) &&
            candidate.Models.Any(model => string.Equals(
                NormalizeMotherboardModel(model),
                normalizedModel,
                StringComparison.OrdinalIgnoreCase)));
        if (board is null)
            return new Dictionary<int, FanChannelMetadata>();

        var result = new Dictionary<int, FanChannelMetadata>();
        foreach (FanHeaderChannel channel in board.Channels)
        {
            if (channel.Index < 0 || string.IsNullOrWhiteSpace(channel.Header) ||
                !Enum.TryParse(channel.Role, true, out FanRole role) || role is FanRole.Unknown)
                continue;

            result[channel.Index] = new FanChannelMetadata(
                FormatDisplayName(channel.Header, role),
                role,
                "Identifi\u00e9 automatiquement");
        }

        return result;
    }

    private static IReadOnlyDictionary<int, FanChannelMetadata> ReadAsusFanXpert(string motherboardName)
    {
        string? manufacturer = DetectManufacturer(motherboardName);
        if (manufacturer is not null && !manufacturer.Equals("ASUS", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<int, FanChannelMetadata>();

        string serviceRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "ASUS",
            "AsusFanControlService");
        string calibrationPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ASUS",
            "Dip",
            "FanXpert",
            "FanCalibrationData.xml");

        if (!Directory.Exists(serviceRoot) || !File.Exists(calibrationPath))
            return new Dictionary<int, FanChannelMetadata>();

        string? matchingStore = Directory.EnumerateFiles(serviceRoot, "FanStore.xml", SearchOption.AllDirectories)
            .FirstOrDefault(path => StoreMatchesMotherboard(path, motherboardName));
        if (matchingStore is null)
            return new Dictionary<int, FanChannelMetadata>();

        XDocument calibration = LoadXml(calibrationPath);
        string[] names = calibration.Root?.Elements("fan")
            .Select(element => element.Element("name")?.Value.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray() ?? [];

        var result = new Dictionary<int, FanChannelMetadata>();
        AddAsusChannel(result, 1, names.FirstOrDefault(IsCpuFan), "CPU_FAN");
        AddAsusChannel(result, 0, names.FirstOrDefault(name => IsChassisFan(name, 1)), "CHA_FAN1");
        for (int chassisNumber = 2; chassisNumber <= 5; chassisNumber++)
            AddAsusChannel(result, chassisNumber, names.FirstOrDefault(name => IsChassisFan(name, chassisNumber)), $"CHA_FAN{chassisNumber}");
        AddAsusChannel(result, 5, names.FirstOrDefault(IsAioPump), "AIO_PUMP");
        return result;
    }

    private static bool StoreMatchesMotherboard(string path, string motherboardName)
    {
        XDocument store = LoadXml(path);
        string? storedModel = store.Root?
            .Element("checkmodelname")?
            .Element("modelname")?
            .Value
            .Trim();
        return string.Equals(
            NormalizeMotherboardModel(storedModel),
            NormalizeMotherboardModel(motherboardName),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMotherboardModel(string? model)
    {
        string normalized = model?.Trim() ?? "";
        string[] manufacturerPrefixes =
        [
            "ASUSTeK COMPUTER INC. ",
            "ASUSTeK ",
            "ASUS ",
            "Micro-Star International Co., Ltd. ",
            "Micro-Star International ",
            "MSI ",
            "Gigabyte Technology Co., Ltd. ",
            "Gigabyte Technology ",
            "GIGABYTE ",
            "ASRock ",
            "BIOSTAR Group ",
            "BIOSTAR "
        ];

        foreach (string prefix in manufacturerPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return normalized[prefix.Length..].Trim();
        }

        return normalized;
    }

    private static string? DetectManufacturer(string? model)
    {
        string value = model?.Trim() ?? "";
        if (value.StartsWith("ASUS ", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("ASUSTeK ", StringComparison.OrdinalIgnoreCase)) return "ASUS";
        if (value.StartsWith("MSI ", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("Micro-Star International", StringComparison.OrdinalIgnoreCase)) return "MSI";
        if (value.StartsWith("GIGABYTE ", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("Gigabyte Technology", StringComparison.OrdinalIgnoreCase)) return "GIGABYTE";
        if (value.StartsWith("ASRock ", StringComparison.OrdinalIgnoreCase)) return "ASROCK";
        if (value.StartsWith("BIOSTAR ", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("BIOSTAR Group ", StringComparison.OrdinalIgnoreCase)) return "BIOSTAR";
        return null;
    }

    private static void MergeMissing(
        IDictionary<int, FanChannelMetadata> destination,
        IReadOnlyDictionary<int, FanChannelMetadata> source)
    {
        foreach ((int index, FanChannelMetadata metadata) in source)
            destination.TryAdd(index, metadata);
    }

    private static void AddAsusChannel(
        IDictionary<int, FanChannelMetadata> result,
        int hardwareIndex,
        string? vendorName,
        string headerName)
    {
        if (string.IsNullOrWhiteSpace(vendorName) || result.ContainsKey(hardwareIndex)) return;

        FanRole role;
        if (IsAioPump(vendorName))
        {
            role = FanRole.Pump;
        }
        else if (IsCpuFan(vendorName))
        {
            role = FanRole.Cpu;
        }
        else
        {
            role = FanRole.Chassis;
        }

        result[hardwareIndex] = new FanChannelMetadata(
            FormatDisplayName(headerName, role),
            role,
            "Identifi\u00e9 automatiquement par les donn\u00e9es constructeur locales");
    }

    private static string FormatDisplayName(string headerName, FanRole role) => role switch
    {
        FanRole.Cpu => $"Ventilateur processeur ({headerName})",
        FanRole.Pump => $"Pompe AIO ({headerName})",
        FanRole.Radiator => $"Ventilateur radiateur ({headerName})",
        _ => $"Ventilateurs bo\u00eetier ({headerName})"
    };

    private static bool IsCpuFan(string name) =>
        name.Contains("CPU Fan", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Optional", StringComparison.OrdinalIgnoreCase);

    private static bool IsChassisFan(string name, int number) =>
        name.Equals($"Chassis Fan {number}", StringComparison.OrdinalIgnoreCase);

    private static bool IsAioPump(string name) =>
        name.Contains("AIO", StringComparison.OrdinalIgnoreCase) &&
        name.Contains("Pump", StringComparison.OrdinalIgnoreCase);

    private static XDocument LoadXml(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };
        using XmlReader reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private sealed class FanHeaderCatalogDocument
    {
        public int SchemaVersion { get; set; }
        public List<FanHeaderBoard> Boards { get; set; } = [];
    }

    private sealed class FanHeaderBoard
    {
        public string Manufacturer { get; set; } = "";
        public List<string> Models { get; set; } = [];
        public List<FanHeaderChannel> Channels { get; set; } = [];
    }

    private sealed class FanHeaderChannel
    {
        public int Index { get; set; }
        public string Header { get; set; } = "";
        public string Role { get; set; } = "";
    }
}
