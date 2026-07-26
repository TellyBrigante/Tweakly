using System.Diagnostics;
using System.Management;
using Optimisation_Tool.Helpers;

internal static class MonitoringTimingProbe
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await MeasureOnceAsync("cold-all", MonCollectParts.All);

            await MeasureGroupAsync("cpu", MonCollectParts.Cpu);
            await MeasureGroupAsync("ram", MonCollectParts.Ram);
            await MeasureGroupAsync("gpu", MonCollectParts.Gpu);
            await MeasureGroupAsync("nvme", MonCollectParts.Nvme);
            MeasureWmiQuery("cpu-usage-wmi",
                "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");
            MeasureWmiQuery("cpu-frequency-wmi",
                "SELECT PercentProcessorPerformance, ProcessorFrequency FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name LIKE '%_Total'");

            for (int cycle = 1; cycle <= 8; cycle++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                await MeasureOnceAsync($"cycle-{cycle}", MonCollectParts.Light | MonCollectParts.Nvme);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"monitor-probe: échec contrôlé — {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task MeasureGroupAsync(string name, MonCollectParts parts)
    {
        for (int sample = 1; sample <= 3; sample++)
            await MeasureOnceAsync($"{name}-{sample}", parts);
    }

    private static void MeasureWmiQuery(string name, string query)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            for (int sample = 1; sample <= 3; sample++)
            {
                var stopwatch = Stopwatch.StartNew();
                using ManagementObjectCollection collection = searcher.Get();
                foreach (ManagementObject item in collection)
                {
                    item.Dispose();
                    break;
                }
                stopwatch.Stop();
                Console.WriteLine($"{name}-{sample}: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{name}: non mesuré ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static async Task MeasureOnceAsync(string name, MonCollectParts parts)
    {
        var stopwatch = Stopwatch.StartNew();
        MonSnapshot snapshot = await SystemMonitor.CollectAsync(parts);
        stopwatch.Stop();

        Console.WriteLine(
            $"{name}: {stopwatch.Elapsed.TotalMilliseconds:F1} ms | " +
            $"CPU {snapshot.CpuUsage:F0} % | GPU {snapshot.GpuUsage:F0} % | " +
            $"RAM {snapshot.RamPct:F0} % | NVMe {snapshot.Nvmes.Count}");
    }
}
