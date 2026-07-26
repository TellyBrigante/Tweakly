using System.IO;
using System.Text.Json;
using FanControl.Core;

namespace Optimisation_Tool.Helpers;

public sealed record SavedFanChannel
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required FanRole Role { get; init; }
    public FanDriveMode DriveMode { get; init; } = FanDriveMode.Unknown;
    public FanCalibrationResult? Calibration { get; init; }
    public ThermalSource Source { get; init; } = ThermalSource.Mixed;
    public double? IdleTemperatureC { get; init; }
    public IReadOnlyList<ThermalTrial> ThermalTrials { get; init; } = [];
    public IReadOnlyList<FanCurvePoint> AutomaticCurve { get; init; } = [];
    public IReadOnlyList<FanCurvePoint> Curve { get; init; } = [];
    public DateTimeOffset? CurveGeneratedAt { get; init; }
}

public sealed record FanProfileDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required string MotherboardName { get; init; }
    public bool AutomaticControlEnabled { get; init; }
    public bool StartWithTweakly { get; init; }
    public double TemperatureHysteresisC { get; init; } = 2;
    public double RampUpPercentPerSecond { get; init; } = 12;
    public double RampDownPercentPerSecond { get; init; } = 3;
    public DateTimeOffset? ProfileSavedAt { get; init; }
    public IReadOnlyList<SavedFanChannel> Channels { get; init; } = [];
}

public static class FanProfileStore
{
    private const int MaximumProfileBytes = 2 * 1024 * 1024;
    private static string FilePath => PathLayout.FanProfilesFile;
    private static string TempPath => FilePath + ".tmp";
    private static string BackupPath => FilePath + ".bak";

    public static FanProfileDocument? Load()
    {
        string[] candidates = new[] { FilePath, TempPath, BackupPath }
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        foreach (string path in candidates)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaximumProfileBytes)
                {
                    AppLog.Write(
                        $"Ventilation : profil ignore ({Path.GetFileName(path)}), taille invalide ({info.Length} octet(s)).");
                    continue;
                }

                byte[] json = File.ReadAllBytes(path);
                FanProfileDocument? document = JsonSerializer.Deserialize<FanProfileDocument>(json);
                string validationError = "document vide";
                if (document is not null && TryValidate(document, out validationError))
                    return document;
                AppLog.Write(
                    $"Ventilation : profil ignore ({Path.GetFileName(path)}) : {validationError}");
            }
            catch (Exception ex)
            {
                AppLog.Error($"Ventilation : lecture impossible ({Path.GetFileName(path)})", ex);
            }
        }

        return null;
    }

    public static bool Save(FanProfileDocument document, out string error)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!TryValidate(document, out error))
        {
            AppLog.Write("Ventilation : sauvegarde refusee : " + error);
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                document,
                new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(
                       TempPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath))
            {
                try
                {
                    File.Replace(TempPath, FilePath, BackupPath, ignoreMetadataErrors: true);
                }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
                {
                    File.Copy(FilePath, BackupPath, overwrite: true);
                    File.Move(TempPath, FilePath, overwrite: true);
                }
            }
            else
            {
                File.Move(TempPath, FilePath);
            }

            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            AppLog.Error("Ventilation : sauvegarde impossible", ex);
            return false;
        }
    }

    public static bool Delete(out string error)
    {
        try
        {
            foreach (string path in new[] { FilePath, TempPath, BackupPath })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }

            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            AppLog.Error("Ventilation : suppression du profil impossible", ex);
            return false;
        }
    }

    public static bool TryValidate(FanProfileDocument document, out string error)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();
        if (document.SchemaVersion != 1)
            errors.Add($"schema {document.SchemaVersion} non pris en charge");
        if (string.IsNullOrWhiteSpace(document.MotherboardName) || document.MotherboardName.Length > 256)
            errors.Add("nom de carte mere invalide");
        InRange(document.TemperatureHysteresisC, 0, 5, "hysteresis", errors);
        InRange(document.RampUpPercentPerSecond, 2, 30, "vitesse de montee", errors);
        InRange(document.RampDownPercentPerSecond, 1, 15, "vitesse de descente", errors);

        if (document.Channels is null)
        {
            errors.Add("liste des canaux absente");
        }
        else
        {
            if (document.Channels.Count > 64)
                errors.Add("trop de canaux");
            if (document.Channels.Any(static channel => channel is null))
                errors.Add("canal nul");

            SavedFanChannel[] channels = document.Channels
                .Where(static channel => channel is not null)
                .ToArray();
            if (channels.Any(static channel =>
                    string.IsNullOrWhiteSpace(channel.Id) ||
                    channel.Id.Length > 512 ||
                    string.IsNullOrWhiteSpace(channel.DisplayName) ||
                    channel.DisplayName.Length > 256))
                errors.Add("identifiant ou nom de canal invalide");
            if (channels.GroupBy(static channel => channel.Id, StringComparer.Ordinal).Any(static group => group.Count() > 1))
                errors.Add("identifiant de canal duplique");

            foreach (SavedFanChannel channel in channels)
                ValidateChannel(channel, errors);

            bool hasUsableCurves = channels.Any(static channel =>
                channel.Calibration is { IsValid: true } &&
                channel.Curve is { Count: >= 2 } &&
                channel.Role is FanRole.Cpu or FanRole.Chassis or FanRole.Radiator);
            if (document.AutomaticControlEnabled && !hasUsableCurves)
                errors.Add("controle automatique demande sans courbe utilisable");
        }

        error = string.Join(" | ", errors.Distinct(StringComparer.Ordinal));
        return errors.Count == 0;
    }

    public static FanProfileDocument RefreshAutomaticCurves(FanProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with
        {
            Channels = document.Channels.Select(RefreshAutomaticCurve).ToArray()
        };
    }

    private static SavedFanChannel RefreshAutomaticCurve(SavedFanChannel channel)
    {
        if (channel.Calibration is not { IsValid: true } calibration ||
            channel.IdleTemperatureC is not double idle || channel.ThermalTrials.Count == 0)
            return channel;

        try
        {
            FanCurvePlan plan = FanCurvePlanner.Build(new FanCurvePlanRequest
            {
                Source = channel.Source,
                Calibration = calibration,
                Trials = channel.ThermalTrials,
                IdleTemperatureC = idle,
                ThermalLimitC = 90,
                SafetyMarginC = 12
            });
            bool followsAutomatic = CurvesEqual(channel.Curve, channel.AutomaticCurve);
            return channel with
            {
                AutomaticCurve = plan.Points,
                Curve = followsAutomatic ? plan.Points : channel.Curve,
                CurveGeneratedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            AppLog.ErrorOnce("fan-curve-refresh-" + channel.Id, "Ventilation : recalcul de courbe ignore", ex);
            return channel;
        }
    }

    private static bool CurvesEqual(IReadOnlyList<FanCurvePoint> left, IReadOnlyList<FanCurvePoint> right)
    {
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (Math.Abs(left[index].TemperatureC - right[index].TemperatureC) > 0.01 ||
                Math.Abs(left[index].DutyPercent - right[index].DutyPercent) > 0.01)
                return false;
        }
        return true;
    }

    private static void ValidateChannel(SavedFanChannel channel, ICollection<string> errors)
    {
        if (!Enum.IsDefined(channel.Role) ||
            !Enum.IsDefined(channel.DriveMode) ||
            !Enum.IsDefined(channel.Source))
            errors.Add($"canal {channel.Id} : type inconnu");

        if (channel.Calibration is { } calibration)
        {
            if (!Enum.IsDefined(calibration.DriveMode) ||
                !double.IsFinite(calibration.MinimumStableDutyPercent) ||
                !double.IsFinite(calibration.RestartDutyPercent) ||
                !double.IsFinite(calibration.MaximumObservedRpm))
            {
                errors.Add($"canal {channel.Id} : calibration invalide");
            }
            else if (calibration.IsValid &&
                     (calibration.MinimumStableDutyPercent is < 1 or > 100 ||
                      calibration.RestartDutyPercent < calibration.MinimumStableDutyPercent ||
                      calibration.RestartDutyPercent > 100 ||
                      calibration.MaximumObservedRpm <= 0))
            {
                errors.Add($"canal {channel.Id} : limites de calibration incoherentes");
            }
        }

        if (channel.IdleTemperatureC is double idle &&
            (!double.IsFinite(idle) || idle is < -20 or > 120))
            errors.Add($"canal {channel.Id} : temperature au repos invalide");
        if (channel.ThermalTrials is null ||
            channel.ThermalTrials.Any(static trial =>
                trial is null ||
                !Enum.IsDefined(trial.Workload) ||
                !double.IsFinite(trial.DutyPercent) ||
                trial.DutyPercent is < 0 or > 100 ||
                !double.IsFinite(trial.MaximumTemperatureC) ||
                trial.MaximumTemperatureC is < -20 or > 120 ||
                trial.ObservedRpm is double rpm && (!double.IsFinite(rpm) || rpm < 0)))
            errors.Add($"canal {channel.Id} : mesure thermique invalide");

        ValidateCurve(channel.Id, "automatique", channel.AutomaticCurve, errors);
        ValidateCurve(channel.Id, "active", channel.Curve, errors);
        if ((channel.AutomaticCurve?.Count > 0 || channel.Curve?.Count > 0) &&
            channel.Calibration is not { IsValid: true })
            errors.Add($"canal {channel.Id} : courbe sans calibration valide");
    }

    private static void ValidateCurve(
        string channelId,
        string name,
        IReadOnlyList<FanCurvePoint>? curve,
        ICollection<string> errors)
    {
        if (curve is null)
        {
            errors.Add($"canal {channelId} : courbe {name} absente");
            return;
        }
        if (curve.Count == 0)
            return;
        if (curve.Count is < 2 or > 32)
        {
            errors.Add($"canal {channelId} : taille de courbe {name} invalide");
            return;
        }

        for (int index = 0; index < curve.Count; index++)
        {
            FanCurvePoint point = curve[index];
            if (point is null ||
                !double.IsFinite(point.TemperatureC) ||
                point.TemperatureC is < -20 or > 120 ||
                !double.IsFinite(point.DutyPercent) ||
                point.DutyPercent is < 0 or > 100)
            {
                errors.Add($"canal {channelId} : point de courbe {name} invalide");
                return;
            }
            if (index > 0 &&
                (point.TemperatureC <= curve[index - 1].TemperatureC ||
                 point.DutyPercent < curve[index - 1].DutyPercent))
            {
                errors.Add($"canal {channelId} : courbe {name} non monotone");
                return;
            }
        }

        if (curve[^1].DutyPercent < 99.9)
            errors.Add($"canal {channelId} : courbe {name} sans point de securite a 100 %");
    }

    private static void InRange(
        double value,
        double minimum,
        double maximum,
        string name,
        ICollection<string> errors)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            errors.Add($"{name} hors limites");
    }
}
