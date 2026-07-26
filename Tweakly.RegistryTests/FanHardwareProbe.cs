using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Optimisation_Tool.Helpers;

internal static class FanHardwareProbe
{
    public static int Run()
    {
        SetDllDirectory(PathLayout.DataDrv);
        var computer = new Computer
        {
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsCpuEnabled = true,
            IsGpuEnabled = true
        };

        try
        {
            computer.Open();
            foreach (IHardware hardware in computer.Hardware)
                PrintHardware(hardware, 0);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Fan probe failed: " + ex.GetBaseException().Message);
            return 1;
        }
        finally
        {
            try { computer.Close(); } catch { }
        }
    }

    private static void PrintHardware(IHardware hardware, int depth)
    {
        string indent = new(' ', depth * 2);
        try { hardware.Update(); }
        catch (Exception ex)
        {
            Console.WriteLine($"{indent}[{hardware.HardwareType}] {hardware.Name} | update error: {ex.Message}");
            return;
        }

        Console.WriteLine($"{indent}[{hardware.HardwareType}] {hardware.Name} | {hardware.Identifier}");
        foreach (ISensor sensor in hardware.Sensors
                     .Where(static sensor => sensor.SensorType is SensorType.Temperature
                         or SensorType.Fan
                         or SensorType.Control
                         or SensorType.Power)
                     .OrderBy(static sensor => sensor.SensorType)
                     .ThenBy(static sensor => sensor.Index))
        {
            string value = sensor.Value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "N/A";
            string control = sensor.Control == null
                ? "none"
                : $"mode={sensor.Control.ControlMode}, software={sensor.Control.SoftwareValue:0.###}";
            Console.WriteLine($"{indent}  {sensor.SensorType,-11} #{sensor.Index,-2} {sensor.Name} = {value} | control={control} | {sensor.Identifier}");
        }

        foreach (IHardware child in hardware.SubHardware)
            PrintHardware(child, depth + 1);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
}
