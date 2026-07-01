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
        public DateTime? CompletedAt { get; set; }
        public int TargetBalanceHours { get; set; } = 2;
        public int TargetRestHours { get; set; } = 8;
        public int SampleIntervalSeconds { get; set; } = 5;
        public bool BalanceInterrupted { get; set; }
        public bool RechargeInterrupted { get; set; }
        public string LastWarning { get; set; } = "";
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

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static BatteryCalibrationSession Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new BatteryCalibrationSession();
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<BatteryCalibrationSession>(json, Options) ?? new BatteryCalibrationSession();
            }
            catch
            {
                return new BatteryCalibrationSession();
            }
        }

        public static void Save(BatteryCalibrationSession session)
        {
            try
            {
                Directory.CreateDirectory(PathLayout.Config);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(session, Options));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch { }
        }

        public static void Reset()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
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
