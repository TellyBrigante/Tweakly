using System.ComponentModel;
using System.Runtime.InteropServices;
using FanControl.Core;
using LibreHardwareMonitor.Hardware;

namespace Optimisation_Tool.Helpers;

public sealed record DetectedFanChannel
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string HardwareName { get; init; }
    public required int Index { get; init; }
    public required double Rpm { get; init; }
    public required double ControlPercent { get; init; }
    public required double MinimumControlPercent { get; init; }
    public required double MaximumControlPercent { get; init; }
    public required FanRole SuggestedRole { get; init; }
    public required FanDriveMode DriveMode { get; init; }
    public required bool RequiresRoleConfirmation { get; init; }
    public required string RoleSource { get; init; }
}

public sealed record FanHardwareInventoryResult
{
    public required bool Available { get; init; }
    public required string MotherboardName { get; init; }
    public required string ControllerName { get; init; }
    public required IReadOnlyList<DetectedFanChannel> Channels { get; init; }
    public required string Message { get; init; }
}

public static class FanHardwareInventory
{
    public static FanHardwareInventoryResult Read()
    {
        using IDisposable hardwareLease = HardwareMonitorAccess.Enter();
        CpuTemperature.SuspendForExclusiveHardwareAccess();

        if (!SetDllDirectory(PathLayout.DataDrv))
        {
            AppLog.ErrorOnce(
                "fan-inventory-dll-directory",
                "Ventilation : dossier PawnIO non enregistre",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var computer = new Computer
        {
            IsMotherboardEnabled = true,
            IsControllerEnabled = true
        };

        try
        {
            computer.Open();
            IHardware? motherboard = computer.Hardware.FirstOrDefault(x => x.HardwareType == HardwareType.Motherboard);
            if (motherboard is null)
                return Unavailable("Carte mere non detectee.");

            var tachometers = new List<FanTachometerDescriptor>();
            var controls = new List<FanControlDescriptor>();
            var hardwareNames = new Dictionary<string, string>(StringComparer.Ordinal);
            Collect(motherboard, tachometers, controls, hardwareNames);

            IReadOnlyList<MatchedFanChannel> matched = FanChannelMatcher.MatchActiveChannels(tachometers, controls);
            IReadOnlyDictionary<int, FanChannelMetadata> vendorMetadata = FanVendorMetadataResolver.Read(motherboard.Name);
            DetectedFanChannel[] channels = matched
                .Select(match => ToDetected(match, hardwareNames, vendorMetadata))
                .ToArray();
            string controller = matched.Count == 0
                ? motherboard.SubHardware.FirstOrDefault()?.Name ?? "Non expose"
                : hardwareNames.GetValueOrDefault(matched[0].HardwareId, "Controleur carte mere");

            return new FanHardwareInventoryResult
            {
                Available = channels.Length > 0,
                MotherboardName = motherboard.Name,
                ControllerName = controller,
                Channels = channels,
                Message = channels.Length > 0
                    ? $"{channels.Length} canal(aux) de carte mere pilotable(s) avec retour de vitesse detecte(s)."
                    : "Aucun canal de carte mere actif avec retour de vitesse et controle logiciel."
            };
        }
        catch (Exception ex)
        {
            AppLog.ErrorOnce("fan-inventory-read", "Ventilation : inventaire materiel impossible", ex);
            return Unavailable("Inventaire materiel indisponible.");
        }
        finally
        {
            try { computer.Close(); } catch { }
        }
    }

    private static void Collect(
        IHardware hardware,
        ICollection<FanTachometerDescriptor> tachometers,
        ICollection<FanControlDescriptor> controls,
        IDictionary<string, string> hardwareNames)
    {
        hardware.Update();
        string hardwareId = hardware.Identifier.ToString();
        hardwareNames[hardwareId] = hardware.Name;

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Fan)
            {
                tachometers.Add(new FanTachometerDescriptor(
                    hardwareId,
                    sensor.Index,
                    sensor.Identifier.ToString(),
                    sensor.Name,
                    sensor.Value));
            }
            else if (sensor.SensorType == SensorType.Control)
            {
                IControl? control = sensor.Control;
                controls.Add(new FanControlDescriptor(
                    hardwareId,
                    sensor.Index,
                    sensor.Identifier.ToString(),
                    sensor.Name,
                    sensor.Value,
                    control?.MinSoftwareValue ?? 0,
                    control?.MaxSoftwareValue ?? 0,
                    control is not null));
            }
        }

        foreach (IHardware child in hardware.SubHardware)
            Collect(child, tachometers, controls, hardwareNames);
    }

    private static DetectedFanChannel ToDetected(
        MatchedFanChannel match,
        IReadOnlyDictionary<string, string> hardwareNames,
        IReadOnlyDictionary<int, FanChannelMetadata> vendorMetadata)
    {
        vendorMetadata.TryGetValue(match.Index, out FanChannelMetadata? metadata);
        FanRole role = metadata?.Role ?? FanChannelLabelClassifier.InferRole(match.Tachometer.Name);
        bool automaticallyIdentified = metadata is not null || role is not FanRole.Unknown;
        return new DetectedFanChannel
        {
            Id = match.Control.Id,
            DisplayName = metadata?.DisplayName ?? FriendlyName(match.Tachometer.Name),
            HardwareName = hardwareNames.GetValueOrDefault(match.HardwareId, "Controleur carte mere"),
            Index = match.Index,
            Rpm = match.Tachometer.Rpm!.Value,
            ControlPercent = match.Control.CurrentPercent ?? 0,
            MinimumControlPercent = match.Control.MinimumPercent,
            MaximumControlPercent = match.Control.MaximumPercent,
            SuggestedRole = role,
            DriveMode = FanDriveMode.Unknown,
            RequiresRoleConfirmation = !automaticallyIdentified,
            RoleSource = metadata?.Source ?? (automaticallyIdentified
                ? "D\u00e9tect\u00e9 automatiquement par le contr\u00f4leur"
                : "Type non expos\u00e9 par le mat\u00e9riel")
        };
    }

    private static string FriendlyName(string name) => string.IsNullOrWhiteSpace(name) ? "Ventilateur" : name.Trim();

    private static FanHardwareInventoryResult Unavailable(string message) => new()
    {
        Available = false,
        MotherboardName = "Non detectee",
        ControllerName = "Non detecte",
        Channels = [],
        Message = message
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
}
