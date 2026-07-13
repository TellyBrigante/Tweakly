using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimisation_Tool.Helpers
{
    public enum BatteryCalibrationPhase
    {
        Idle,
        ChargeToFull,
        CellBalance,
        Drain,
        Rest,
        Recharge,
        Complete
    }

    public sealed class BatteryCalibrationSample
    {
        public DateTime Timestamp { get; set; }
        public BatteryCalibrationPhase Phase { get; set; }
        public int? ChargePercent { get; set; }
        public int? VoltageMv { get; set; }
        public double? VoltageV { get; set; }
        public int? RateMw { get; set; }
        public double? PowerW { get; set; }
        public double? CurrentA { get; set; }
        public double? TemperatureC { get; set; }
        public int? RemainingCapacityMWh { get; set; }
        public bool? OnAcPower { get; set; }
        public string State { get; set; } = "";
        public string Source { get; set; } = "";
    }

    public sealed class BatteryCalibrationSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public BatteryCalibrationPhase Phase { get; set; } = BatteryCalibrationPhase.Idle;
        public DateTime PhaseStartedAt { get; set; } = DateTime.Now;
        public DateTime LastSavedAt { get; set; } = DateTime.Now;
        public DateTime? CompletedAt { get; set; }
        public int TargetBalanceHours { get; set; } = 2;
        public int TargetRestHours { get; set; } = 8;
        public double VerifiedRestSeconds { get; set; }
        public int SampleIntervalSeconds { get; set; } = 5;
        public bool BalanceInterrupted { get; set; }
        public bool RechargeInterrupted { get; set; }
        public string LastWarning { get; set; } = "";
        public bool ChargeFullPromptShown { get; set; }
        public bool DrainPromptShown { get; set; }
        public bool RechargePromptShown { get; set; }
        public bool CompletePromptShown { get; set; }
        public bool PowerPlanGuardApplied { get; set; }
        public int? OriginalDcCriticalBatteryAction { get; set; }
        public int? OriginalDcLowBatteryAction { get; set; }
        public string PowerPlanGuardError { get; set; } = "";
        public string BatteryName { get; set; } = "";
        public string BatteryManufacturer { get; set; } = "";
        public string BatteryChemistry { get; set; } = "";
        public int? DesignCapacityMWh { get; set; }
        public int? FullChargeCapacityMWh { get; set; }
        public int? CycleCount { get; set; }
        public List<BatteryCalibrationSample> Samples { get; set; } = new();

        [JsonIgnore]
        public BatteryCalibrationSample? LastSample => Samples.Count > 0 ? Samples[^1] : null;
    }

    public static class BatteryCalibrationStore
    {
        public static string FilePath => Path.Combine(PathLayout.Config, "tweakly-battery-calibration.json");
        private static string BackupFilePath => FilePath + ".bak";
        private static string TempFilePath => FilePath + ".tmp";

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static BatteryCalibrationSession Load()
        {
            var candidates = new[]
            {
                TryLoad(FilePath, "principal"),
                TryLoad(BackupFilePath, "backup"),
                TryLoad(TempFilePath, "temporaire")
            }.Where(s => s != null).Cast<BatteryCalibrationSession>().ToList();

            if (candidates.Count == 0) return new BatteryCalibrationSession();
            return candidates
                .OrderByDescending(SessionSortDate)
                .ThenByDescending(s => s.Samples.Count)
                .First();
        }

        public static void Save(BatteryCalibrationSession session)
        {
            try
            {
                session.LastSavedAt = DateTime.Now;
                Directory.CreateDirectory(PathLayout.Config);
                File.WriteAllText(TempFilePath, JsonSerializer.Serialize(session, Options));

                if (File.Exists(FilePath))
                {
                    File.Replace(TempFilePath, FilePath, BackupFilePath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(TempFilePath, FilePath);
                    File.Copy(FilePath, BackupFilePath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("BatteryCalibrationStore.Save ERREUR : " + ex.Message + " | path=" + FilePath);
                TryCopyCurrentToBackup();
            }
        }

        public static void Reset()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                if (File.Exists(BackupFilePath)) File.Delete(BackupFilePath);
                if (File.Exists(TempFilePath)) File.Delete(TempFilePath);
            }
            catch { }
        }

        public static void RestorePowerPlanGuardIfNeeded()
        {
            try
            {
                var session = Load();
                if (!session.PowerPlanGuardApplied) return;

                if (BatteryPowerPlanGuard.RestoreDrainSettings(
                        session.OriginalDcCriticalBatteryAction,
                        session.OriginalDcLowBatteryAction,
                        out var error))
                {
                    session.PowerPlanGuardApplied = false;
                    session.PowerPlanGuardError = "";
                }
                else
                {
                    session.PowerPlanGuardError = error;
                }

                Save(session);
            }
            catch (Exception ex)
            {
                AppLog.Write("BatteryCalibrationStore.RestorePowerPlanGuardIfNeeded ERREUR : " + ex.Message);
            }
        }

        private static BatteryCalibrationSession? TryLoad(string path, string label)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                var session = JsonSerializer.Deserialize<BatteryCalibrationSession>(json, Options);
                if (session == null) return null;
                session.Samples ??= new List<BatteryCalibrationSample>();
                return session;
            }
            catch (Exception ex)
            {
                AppLog.Write($"BatteryCalibrationStore.Load {label} ERREUR : {ex.Message} | path={path}");
                return null;
            }
        }

        private static DateTime SessionSortDate(BatteryCalibrationSession session)
        {
            var sampleDate = session.LastSample?.Timestamp ?? DateTime.MinValue;
            var savedDate = session.LastSavedAt == default ? DateTime.MinValue : session.LastSavedAt;
            return savedDate > sampleDate ? savedDate : sampleDate;
        }

        private static void TryCopyCurrentToBackup()
        {
            try
            {
                if (File.Exists(FilePath))
                    File.Copy(FilePath, BackupFilePath, overwrite: true);
            }
            catch { }
        }

        public static BatteryCalibrationSample FromSnapshot(BatterySnapshot snapshot, BatteryCalibrationPhase phase)
            => new()
            {
                Timestamp = DateTime.Now,
                Phase = phase,
                ChargePercent = snapshot.ChargePercent,
                VoltageMv = snapshot.VoltageMv,
                VoltageV = snapshot.VoltageV,
                RateMw = snapshot.RateMw,
                PowerW = snapshot.PowerW,
                CurrentA = snapshot.CurrentA,
                TemperatureC = snapshot.TemperatureC,
                RemainingCapacityMWh = snapshot.RemainingCapacityMWh,
                OnAcPower = snapshot.OnAcPower,
                State = snapshot.StateText,
                Source = snapshot.Source
            };
    }
}
