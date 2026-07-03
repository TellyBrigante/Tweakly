using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Optimisation_Tool.Helpers
{
    public sealed class BatterySnapshot
    {
        public bool HasBattery { get; init; }
        public string Source { get; init; } = "Aucune";
        public string Name { get; init; } = "";
        public string Manufacturer { get; init; } = "";
        public string Chemistry { get; init; } = "";
        public int? ChargePercent { get; init; }
        public int? RemainingCapacityMWh { get; init; }
        public int? FullChargeCapacityMWh { get; init; }
        public int? DesignCapacityMWh { get; init; }
        public int? CycleCount { get; init; }
        public int? VoltageMv { get; init; }
        public double? VoltageV => VoltageMv is > 0 ? VoltageMv.Value / 1000.0 : null;
        public int? RateMw { get; init; }
        public double? PowerW => RateMw.HasValue ? RateMw.Value / 1000.0 : null;
        public double? CurrentA =>
            RateMw.HasValue && VoltageMv is > 0
                ? RateMw.Value / (double)VoltageMv.Value
                : null;
        public double? TemperatureC { get; init; }
        public bool? OnAcPower { get; init; }
        public bool? IsCharging { get; init; }
        public bool? IsDischarging { get; init; }
        public bool? IsCritical { get; init; }
        public double? HealthPercent =>
            FullChargeCapacityMWh is > 0 && DesignCapacityMWh is > 0
                ? FullChargeCapacityMWh.Value * 100.0 / DesignCapacityMWh.Value
                : null;

        public string StateText
        {
            get
            {
                if (!HasBattery) return "Aucune batterie";
                if (IsCritical == true) return "Critique";
                if (IsCharging == true) return "En charge";
                if (IsDischarging == true) return "En décharge";
                if (OnAcPower == true) return "Branché";
                return "Batterie détectée";
            }
        }
    }

    public static class BatteryProbe
    {
        private static readonly Guid BatteryInterfaceGuid = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

        private const int DIGCF_PRESENT = 0x00000002;
        private const int DIGCF_DEVICEINTERFACE = 0x00000010;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        private const uint FILE_DEVICE_BATTERY = 0x00000029;
        private const uint FILE_READ_ACCESS = 0x0001;
        private const uint METHOD_BUFFERED = 0x0000;
        private static readonly uint IOCTL_BATTERY_QUERY_TAG = CtlCode(FILE_DEVICE_BATTERY, 0x10, METHOD_BUFFERED, FILE_READ_ACCESS);
        private static readonly uint IOCTL_BATTERY_QUERY_INFORMATION = CtlCode(FILE_DEVICE_BATTERY, 0x11, METHOD_BUFFERED, FILE_READ_ACCESS);
        private static readonly uint IOCTL_BATTERY_QUERY_STATUS = CtlCode(FILE_DEVICE_BATTERY, 0x13, METHOD_BUFFERED, FILE_READ_ACCESS);

        private const uint BATTERY_CAPACITY_RELATIVE = 0x40000000;
        private const uint BATTERY_UNKNOWN_CAPACITY = 0xFFFFFFFF;
        private const uint BATTERY_UNKNOWN_VOLTAGE = 0xFFFFFFFF;
        private const int BATTERY_UNKNOWN_RATE = unchecked((int)0x80000000);
        private const uint BATTERY_POWER_ON_LINE = 0x00000001;
        private const uint BATTERY_DISCHARGING = 0x00000002;
        private const uint BATTERY_CHARGING = 0x00000004;
        private const uint BATTERY_CRITICAL = 0x00000008;

        public static bool HasBattery() => Read().HasBattery;

        public static BatterySnapshot Read()
        {
            var acpiWmi = ReadAcpiWmi();
            var win32Wmi = ReadWmi();

            foreach (var devicePath in EnumerateBatteryDevicePaths())
            {
                var low = ReadBatteryDevice(devicePath);
                if (low.HasBattery)
                    return MergeSnapshots(low, acpiWmi, win32Wmi);
            }

            return MergeSnapshots(acpiWmi, win32Wmi);
        }

        private static BatterySnapshot MergeSnapshots(params BatterySnapshot[] snapshots)
        {
            var usable = snapshots.Where(s => s.HasBattery).ToArray();
            if (usable.Length == 0) return new BatterySnapshot();

            return new BatterySnapshot
            {
                HasBattery = true,
                Source = string.Join(" + ", usable
                    .Select(s => s.Source)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
                Name = FirstNonEmpty(usable.Select(s => s.Name).ToArray()),
                Manufacturer = FirstNonEmpty(usable.Select(s => s.Manufacturer).ToArray()),
                Chemistry = FirstNonEmpty(usable.Select(s => s.Chemistry).ToArray()),
                ChargePercent = FirstNullable(usable.Select(s => s.ChargePercent)),
                RemainingCapacityMWh = FirstNullable(usable.Select(s => s.RemainingCapacityMWh)),
                FullChargeCapacityMWh = FirstNullable(usable.Select(s => s.FullChargeCapacityMWh)),
                DesignCapacityMWh = FirstNullable(usable.Select(s => s.DesignCapacityMWh)),
                CycleCount = FirstNullable(usable.Select(s => s.CycleCount)),
                VoltageMv = FirstNullable(usable.Select(s => s.VoltageMv)),
                RateMw = FirstNullable(usable.Select(s => s.RateMw)),
                TemperatureC = FirstNullable(usable.Select(s => s.TemperatureC)),
                OnAcPower = FirstNullable(usable.Select(s => s.OnAcPower)),
                IsCharging = FirstNullable(usable.Select(s => s.IsCharging)),
                IsDischarging = FirstNullable(usable.Select(s => s.IsDischarging)),
                IsCritical = FirstNullable(usable.Select(s => s.IsCritical)),
            };
        }

        private static BatterySnapshot ReadBatteryDevice(string devicePath)
        {
            using var handle = OpenBatteryHandle(devicePath);
            if (handle == null || handle.IsInvalid)
                return new BatterySnapshot();

            if (!QueryBatteryTag(handle, out var tag) || tag == 0)
                return new BatterySnapshot();

            var info = QueryBatteryInformation(handle, tag);
            var status = QueryBatteryStatus(handle, tag);
            var temp = QueryBatteryTemperature(handle, tag);
            var name = QueryBatteryString(handle, tag, BatteryQueryInformationLevel.BatteryDeviceName);
            var manufacturer = QueryBatteryString(handle, tag, BatteryQueryInformationLevel.BatteryManufactureName);
            var serial = QueryBatteryString(handle, tag, BatteryQueryInformationLevel.BatterySerialNumber);

            bool relative = info.HasValue && (info.Value.Capabilities & BATTERY_CAPACITY_RELATIVE) != 0;
            int? remainingMWh = !relative && Known(status.Capacity) ? (int)status.Capacity : null;
            int? fullMWh = !relative && info.HasValue && Known(info.Value.FullChargedCapacity) ? (int)info.Value.FullChargedCapacity : null;
            int? designMWh = !relative && info.HasValue && Known(info.Value.DesignedCapacity) ? (int)info.Value.DesignedCapacity : null;
            int? percent = remainingMWh.HasValue && fullMWh is > 0
                ? Math.Clamp((int)Math.Round(remainingMWh.Value * 100.0 / fullMWh.Value), 0, 100)
                : null;
            int? rateMw = !relative && status.Rate != BATTERY_UNKNOWN_RATE ? status.Rate : null;

            var powerState = status.PowerState;
            var chemistry = info.HasValue ? ChemistryToString(info.Value.Chemistry) : "";
            var displayName = FirstNonEmpty(name, serial, "Batterie");

            return new BatterySnapshot
            {
                HasBattery = true,
                Source = "Battery API",
                Name = displayName,
                Manufacturer = manufacturer,
                Chemistry = chemistry,
                ChargePercent = percent,
                RemainingCapacityMWh = remainingMWh,
                FullChargeCapacityMWh = fullMWh,
                DesignCapacityMWh = designMWh,
                CycleCount = info.HasValue && info.Value.CycleCount > 0 ? (int)info.Value.CycleCount : null,
                VoltageMv = KnownVoltage(status.Voltage) ? (int)status.Voltage : null,
                RateMw = rateMw,
                TemperatureC = temp,
                OnAcPower = (powerState & BATTERY_POWER_ON_LINE) != 0,
                IsCharging = (powerState & BATTERY_CHARGING) != 0,
                IsDischarging = (powerState & BATTERY_DISCHARGING) != 0,
                IsCritical = (powerState & BATTERY_CRITICAL) != 0,
            };
        }

        private static BatterySnapshot ReadWmi()
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
                var batteries = s.Get().Cast<ManagementObject>().ToList();
                if (batteries.Count == 0) return new BatterySnapshot();

                var b = batteries[0];
                var statusCode = ToInt(b["BatteryStatus"]);
                int? percent = ToInt(b["EstimatedChargeRemaining"]);
                int? designMWh = ToInt(b["DesignCapacity"]);
                int? fullMWh = ToInt(b["FullChargeCapacity"]);

                return new BatterySnapshot
                {
                    HasBattery = true,
                    Source = "Win32_Battery",
                    Name = b["Name"]?.ToString() ?? b["DeviceID"]?.ToString() ?? "Batterie",
                    Manufacturer = b["Manufacturer"]?.ToString() ?? "",
                    Chemistry = BatteryChemistryName(ToInt(b["Chemistry"])),
                    ChargePercent = percent,
                    DesignCapacityMWh = designMWh,
                    FullChargeCapacityMWh = fullMWh,
                    VoltageMv = null,
                    OnAcPower = statusCode is 2 or 6 or 7 or 8 or 9 or 10 or 11,
                    IsCharging = statusCode is 6 or 7 or 8 or 9,
                    IsDischarging = statusCode == 1,
                    IsCritical = statusCode == 4,
                };
            }
            catch
            {
                return new BatterySnapshot();
            }
        }

        private static BatterySnapshot ReadAcpiWmi()
        {
            try
            {
                var status = ReadFirstRootWmiObject("BatteryStatus");
                var full = ReadFirstRootWmiObject("BatteryFullChargedCapacity");
                var staticData = ReadFirstRootWmiObject("BatteryStaticData");
                var cycles = ReadFirstRootWmiObject("BatteryCycleCount");
                var temp = ReadFirstRootWmiObject("BatteryTemperature");

                if (status.Count == 0 && full.Count == 0 && staticData.Count == 0 && cycles.Count == 0 && temp.Count == 0)
                    return new BatterySnapshot();

                int? remaining = Positive(ToInt(Value(status, "RemainingCapacity")));
                int? fullCapacity = Positive(ToInt(Value(full, "FullChargedCapacity")));
                int? design = Positive(ToInt(Value(staticData, "DesignedCapacity")));
                int? voltage = Positive(ToInt(Value(status, "Voltage")));
                int? rate = ToInt(Value(status, "Rate"));
                if (rate == BATTERY_UNKNOWN_RATE) rate = null;
                int? cycleCount = Positive(ToInt(Value(cycles, "CycleCount")));
                int? tempTenthsKelvin = Positive(ToInt(Value(temp, "Temperature")));
                double? temperatureC = tempTenthsKelvin.HasValue
                    ? Math.Round(tempTenthsKelvin.Value / 10.0 - 273.15, 1)
                    : null;

                int? percent = remaining.HasValue && fullCapacity is > 0
                    ? Math.Clamp((int)Math.Round(remaining.Value * 100.0 / fullCapacity.Value), 0, 100)
                    : null;

                return new BatterySnapshot
                {
                    HasBattery = true,
                    Source = "ACPI WMI",
                    Name = FirstNonEmpty(ToText(Value(staticData, "DeviceName")), ToText(Value(staticData, "Tag")), "Batterie"),
                    Manufacturer = ToText(Value(staticData, "ManufactureName")),
                    ChargePercent = percent,
                    RemainingCapacityMWh = remaining,
                    FullChargeCapacityMWh = fullCapacity,
                    DesignCapacityMWh = design,
                    CycleCount = cycleCount,
                    VoltageMv = voltage,
                    RateMw = rate,
                    TemperatureC = temperatureC,
                    OnAcPower = ToBool(Value(status, "PowerOnline")),
                    IsCharging = ToBool(Value(status, "Charging")),
                    IsDischarging = ToBool(Value(status, "Discharging")),
                    IsCritical = ToBool(Value(status, "Critical"))
                };
            }
            catch
            {
                return new BatterySnapshot();
            }
        }

        private static IEnumerable<string> EnumerateBatteryDevicePaths()
        {
            var batteryGuid = BatteryInterfaceGuid;
            IntPtr info = SetupDiGetClassDevs(ref batteryGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (info == IntPtr.Zero || info.ToInt64() == -1)
                yield break;

            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA
                    {
                        cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
                    };

                    if (!SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref batteryGuid, index, ref data))
                        yield break;

                    var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA
                    {
                        cbSize = IntPtr.Size == 8 ? 8 : 6
                    };

                    if (SetupDiGetDeviceInterfaceDetail(
                            info,
                            ref data,
                            ref detail,
                            Marshal.SizeOf<SP_DEVICE_INTERFACE_DETAIL_DATA>(),
                            out _,
                            IntPtr.Zero)
                        && !string.IsNullOrWhiteSpace(detail.DevicePath))
                    {
                        yield return detail.DevicePath;
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(info);
            }
        }

        private static SafeFileHandle? OpenBatteryHandle(string path)
        {
            var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            handle.Dispose();

            handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            handle.Dispose();
            return null;
        }

        private static bool QueryBatteryTag(SafeFileHandle handle, out uint tag)
        {
            tag = 0;
            var input = BitConverter.GetBytes(0u);
            var output = new byte[4];
            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG, input, input.Length, output, output.Length, out var bytes, IntPtr.Zero) || bytes < 4)
                return false;
            tag = BitConverter.ToUInt32(output, 0);
            return true;
        }

        private static BATTERY_INFORMATION? QueryBatteryInformation(SafeFileHandle handle, uint tag)
        {
            var bytes = QueryBatteryInformationRaw(handle, tag, BatteryQueryInformationLevel.BatteryInformation, 0, Marshal.SizeOf<BATTERY_INFORMATION>());
            return bytes.Length >= Marshal.SizeOf<BATTERY_INFORMATION>() ? BytesToStruct<BATTERY_INFORMATION>(bytes) : null;
        }

        private static BATTERY_STATUS QueryBatteryStatus(SafeFileHandle handle, uint tag)
        {
            var wait = new BATTERY_WAIT_STATUS { BatteryTag = tag };
            var input = StructToBytes(wait);
            var output = new byte[Marshal.SizeOf<BATTERY_STATUS>()];
            return DeviceIoControl(handle, IOCTL_BATTERY_QUERY_STATUS, input, input.Length, output, output.Length, out var bytes, IntPtr.Zero) && bytes >= output.Length
                ? BytesToStruct<BATTERY_STATUS>(output)
                : new BATTERY_STATUS();
        }

        private static double? QueryBatteryTemperature(SafeFileHandle handle, uint tag)
        {
            var bytes = QueryBatteryInformationRaw(handle, tag, BatteryQueryInformationLevel.BatteryTemperature, 0, 4);
            if (bytes.Length < 4) return null;
            uint tenthsKelvin = BitConverter.ToUInt32(bytes, 0);
            if (tenthsKelvin == 0 || tenthsKelvin == 0xFFFFFFFF) return null;
            return Math.Round(tenthsKelvin / 10.0 - 273.15, 1);
        }

        private static string QueryBatteryString(SafeFileHandle handle, uint tag, BatteryQueryInformationLevel level)
        {
            var bytes = QueryBatteryInformationRaw(handle, tag, level, 0, 512);
            if (bytes.Length < 2) return "";
            var value = Encoding.Unicode.GetString(bytes).TrimEnd('\0', ' ', '\r', '\n', '\t');
            return value.Trim();
        }

        private static byte[] QueryBatteryInformationRaw(SafeFileHandle handle, uint tag, BatteryQueryInformationLevel level, int atRate, int outputSize)
        {
            var query = new BATTERY_QUERY_INFORMATION
            {
                BatteryTag = tag,
                InformationLevel = level,
                AtRate = atRate
            };
            var input = StructToBytes(query);
            var output = new byte[outputSize];

            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, input, input.Length, output, output.Length, out var bytes, IntPtr.Zero) || bytes <= 0)
                return Array.Empty<byte>();

            if (bytes >= output.Length) return output;
            Array.Resize(ref output, bytes);
            return output;
        }

        private static byte[] StructToBytes<T>(T value) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            var bytes = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, ptr, false);
                Marshal.Copy(ptr, bytes, 0, size);
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private static T BytesToStruct<T>(byte[] bytes) where T : struct
        {
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                return Marshal.PtrToStructure<T>(ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private static bool Known(uint value) => value != 0 && value != BATTERY_UNKNOWN_CAPACITY;
        private static bool KnownVoltage(uint value) => value != 0 && value != BATTERY_UNKNOWN_VOLTAGE;

        private static Dictionary<string, object?> ReadFirstRootWmiObject(string className)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", $"SELECT * FROM {className}");
                using var results = searcher.Get();
                var obj = results.Cast<ManagementObject>().FirstOrDefault();
                if (obj == null) return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData property in obj.Properties)
                        values[property.Name] = property.Value;
                    return values;
                }
                finally
                {
                    obj.Dispose();
                }
            }
            catch
            {
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static object? Value(Dictionary<string, object?> values, string name)
            => values.TryGetValue(name, out var value) ? value : null;

        private static int? Positive(int? value)
            => value is > 0 ? value : null;

        private static T? FirstNullable<T>(IEnumerable<T?> values) where T : struct
            => values.FirstOrDefault(v => v.HasValue);

        private static int? ToInt(object? value)
        {
            if (value == null) return null;
            if (int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                return i;
            return null;
        }

        private static bool? ToBool(object? value)
        {
            if (value is bool b) return b;
            if (value == null) return null;
            if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsedBool))
                return parsedBool;
            if (int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                return parsedInt != 0;
            return null;
        }

        private static string ToText(object? value)
            => value?.ToString()?.Trim() ?? "";

        private static string FirstNonEmpty(params string[] values)
            => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

        private static string ChemistryToString(byte[]? chemistry)
        {
            if (chemistry == null || chemistry.Length == 0) return "";
            return Encoding.ASCII.GetString(chemistry).Trim('\0', ' ', '\t');
        }

        private static string BatteryChemistryName(int? code) => code switch
        {
            2 => "Lithium-ion",
            3 => "Plomb-acide",
            4 => "Nickel-cadmium",
            5 => "Nickel métal hydrure",
            6 => "Zinc-air",
            7 => "Lithium polymère",
            _ => ""
        };

        private static uint CtlCode(uint deviceType, uint function, uint method, uint access)
            => (deviceType << 16) | (access << 14) | (function << 2) | method;

        private enum BatteryQueryInformationLevel : uint
        {
            BatteryInformation = 0,
            BatteryTemperature = 2,
            BatteryDeviceName = 4,
            BatteryManufactureName = 6,
            BatterySerialNumber = 8,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            public int cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
            public string DevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_QUERY_INFORMATION
        {
            public uint BatteryTag;
            public BatteryQueryInformationLevel InformationLevel;
            public int AtRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_INFORMATION
        {
            public uint Capabilities;
            public byte Technology;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public byte[] Reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] Chemistry;
            public uint DesignedCapacity;
            public uint FullChargedCapacity;
            public uint DefaultAlert1;
            public uint DefaultAlert2;
            public uint CriticalBias;
            public uint CycleCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_WAIT_STATUS
        {
            public uint BatteryTag;
            public uint Timeout;
            public uint PowerState;
            public uint LowCapacity;
            public uint HighCapacity;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_STATUS
        {
            public uint PowerState;
            public uint Capacity;
            public uint Voltage;
            public int Rate;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            string? enumerator,
            IntPtr hwndParent,
            int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            ref SP_DEVICE_INTERFACE_DETAIL_DATA deviceInterfaceDetailData,
            int deviceInterfaceDetailDataSize,
            out int requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            byte[]? inBuffer,
            int inBufferSize,
            byte[]? outBuffer,
            int outBufferSize,
            out int bytesReturned,
            IntPtr overlapped);
    }
}
