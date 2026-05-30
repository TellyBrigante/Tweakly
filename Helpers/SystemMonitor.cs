using System;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Collecte des métriques système en temps réel (sans driver kernel).
    /// CPU : usage + fréquence live (estimée). RAM : usage. GPU NVIDIA : via nvidia-smi.
    /// </summary>
    public sealed class MonSnapshot
    {
        public double CpuUsage;     // %
        public double CpuMHz;       // fréquence live
        public int    CpuBaseMHz;   // fréquence de base
        public string CpuName     = "";
        public int    CpuCores;
        public int    CpuThreads;
        public int    Processes;    // nombre de processus actifs
        public string TopCpuName  = "";   // process le + gourmand CPU
        public double TopCpuPct;
        public string TopRamName  = "";   // process le + gourmand RAM
        public double TopRamMB;

        public double RamUsedGB;
        public double RamFreeGB;
        public double RamTotalGB;       // utilisable (vue OS)
        public double RamInstalledGB;   // physiquement installée (somme barrettes)
        public double RamPct;
        public string RamType   = "";   // DDR4 / DDR5
        public int    RamSpeed;         // MHz
        public int    RamSticks;

        public bool   GpuOk;
        public string GpuName        = "";
        public double GpuUsage;      // %
        public double GpuVramUsedMB;
        public double GpuVramTotalMB;
        public double GpuTemp;       // °C
        public double GpuWatts;
        public double GpuMHz;
    }

    public static class SystemMonitor
    {
        // ── RAM (GlobalMemoryStatusEx) ────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint  dwLength;
            public uint  dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // ── Collecte complète (à appeler sur un thread de fond) ──────────────
        public static MonSnapshot Collect()
        {
            var s = new MonSnapshot();
            CollectCpu(s);
            CollectProcesses(s);
            CollectRam(s);
            CollectGpu(s);
            return s;
        }

        // ── Processus : compte + plus gros consommateurs CPU & RAM ────────────
        private static System.Collections.Generic.Dictionary<int, TimeSpan> _prevCpu = new();
        private static DateTime _prevStamp;

        private static void CollectProcesses(MonSnapshot s)
        {
            try
            {
                var procs = Process.GetProcesses();
                s.Processes = procs.Length;

                var now     = DateTime.UtcNow;
                double secs = (now - _prevStamp).TotalSeconds;
                int    cores = Environment.ProcessorCount;
                var    cur   = new System.Collections.Generic.Dictionary<int, TimeSpan>(procs.Length);

                double bestCpu = 0;  string bestCpuName = "";
                long   bestRam = 0;  string bestRamName = "";

                foreach (var p in procs)
                {
                    try
                    {
                        if (p.Id == 0) continue;   // "Idle" fausse le CPU

                        // RAM (working set physique)
                        long ws = p.WorkingSet64;
                        if (ws > bestRam) { bestRam = ws; bestRamName = p.ProcessName; }

                        // CPU : delta de temps processeur / temps écoulé / cœurs
                        var ct = p.TotalProcessorTime;
                        cur[p.Id] = ct;
                        if (secs > 0 && _prevCpu.TryGetValue(p.Id, out var prev))
                        {
                            double pct = (ct - prev).TotalSeconds / (secs * cores) * 100.0;
                            if (pct > bestCpu) { bestCpu = pct; bestCpuName = p.ProcessName; }
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }

                _prevCpu   = cur;
                _prevStamp = now;

                s.TopCpuName = Short(bestCpuName);
                s.TopCpuPct  = Math.Max(0, Math.Min(100, bestCpu));
                s.TopRamName = Short(bestRamName);
                s.TopRamMB   = bestRam / (1024.0 * 1024.0);
            }
            catch { }
        }

        private static string Short(string n)
            => string.IsNullOrEmpty(n) ? "" : (n.Length > 16 ? n.Substring(0, 15) + "…" : n);

        // Infos CPU statiques (lues une seule fois)
        private static string _cpuName = "";
        private static int    _cpuCores, _cpuThreads;

        private static void EnsureCpuStatic()
        {
            if (_cpuName.Length > 0) return;
            try
            {
                using var q = new ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                foreach (ManagementObject o in q.Get())
                {
                    _cpuName    = (o["Name"]?.ToString() ?? "").Trim();
                    _cpuCores   = Convert.ToInt32(o["NumberOfCores"] ?? 0);
                    _cpuThreads = Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
                    o.Dispose();
                    break;
                }
            }
            catch { }
            if (_cpuName.Length == 0) _cpuName = "Processeur";
            // Nettoyer le nom (retirer (R), (TM), CPU @ x.xGHz)
            _cpuName = _cpuName
                .Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "")
                .Replace("CPU", "").Trim();
            var at = _cpuName.IndexOf('@');
            if (at > 0) _cpuName = _cpuName.Substring(0, at).Trim();
        }

        private static void CollectCpu(MonSnapshot s)
        {
            EnsureCpuStatic();
            s.CpuName    = _cpuName;
            s.CpuCores   = _cpuCores;
            s.CpuThreads = _cpuThreads;

            // Utilisation %
            try
            {
                using var q = new ManagementObjectSearcher(
                    "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");
                foreach (ManagementObject o in q.Get())
                {
                    s.CpuUsage = Convert.ToDouble(o["PercentProcessorTime"]);
                    o.Dispose();
                    break;
                }
            }
            catch { }

            // Fréquence live = base × (% performance / 100) → capture le turbo
            try
            {
                using var q = new ManagementObjectSearcher(
                    "SELECT PercentProcessorPerformance, ProcessorFrequency FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name LIKE '%_Total'");
                foreach (ManagementObject o in q.Get())
                {
                    var baseMhz = Convert.ToDouble(o["ProcessorFrequency"]);
                    var perf    = Convert.ToDouble(o["PercentProcessorPerformance"]);
                    s.CpuBaseMHz = (int)baseMhz;
                    s.CpuMHz     = perf > 0 ? baseMhz * perf / 100.0 : baseMhz;
                    o.Dispose();
                    break;
                }
            }
            catch { }
        }

        // Infos RAM statiques (type, fréquence, nb de barrettes, capacité) — lues une fois
        private static string _ramType = "";
        private static int    _ramSpeed, _ramSticks;
        private static double _ramInstalledGB;

        private static void EnsureRamStatic()
        {
            if (_ramSticks > 0) return;
            try
            {
                using var q = new ManagementObjectSearcher(
                    "SELECT Capacity, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
                var typeMap = new System.Collections.Generic.Dictionary<int, string>
                    { {20,"DDR2"}, {24,"DDR3"}, {26,"DDR4"}, {34,"DDR5"} };
                ulong totalBytes = 0;
                foreach (ManagementObject o in q.Get())
                {
                    _ramSticks++;
                    if (o["Capacity"] != null) totalBytes += Convert.ToUInt64(o["Capacity"]);
                    if (_ramSpeed == 0 && o["Speed"] != null) _ramSpeed = Convert.ToInt32(o["Speed"]);
                    if (_ramType.Length == 0)
                        typeMap.TryGetValue(Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0), out _ramType);
                    o.Dispose();
                }
                _ramInstalledGB = totalBytes / (1024.0 * 1024.0 * 1024.0);
            }
            catch { }
            if (_ramType == null || _ramType.Length == 0) _ramType = "DDR";
        }

        private static void CollectRam(MonSnapshot s)
        {
            EnsureRamStatic();
            s.RamType        = _ramType;
            s.RamSpeed       = _ramSpeed;
            s.RamSticks      = _ramSticks;
            s.RamInstalledGB = _ramInstalledGB;
            try
            {
                var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref mem))
                {
                    double gb = 1024.0 * 1024.0 * 1024.0;
                    s.RamTotalGB = mem.ullTotalPhys / gb;
                    s.RamFreeGB  = mem.ullAvailPhys / gb;
                    s.RamUsedGB  = (mem.ullTotalPhys - mem.ullAvailPhys) / gb;
                    s.RamPct     = mem.dwMemoryLoad;
                }
            }
            catch { }
        }

        private static void CollectGpu(MonSnapshot s)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,clocks.gr " +
                    "--format=csv,noheader,nounits")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                });
                if (p == null) return;

                var line = p.StandardOutput.ReadLine();
                p.WaitForExit(4000);
                if (string.IsNullOrWhiteSpace(line)) return;

                var parts = line.Split(',');
                if (parts.Length < 7) return;

                double D(string v)
                {
                    v = v.Trim();
                    return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
                }

                s.GpuName        = parts[0].Trim();
                s.GpuUsage       = D(parts[1]);
                s.GpuVramUsedMB  = D(parts[2]);
                s.GpuVramTotalMB = D(parts[3]);
                s.GpuTemp        = D(parts[4]);
                s.GpuWatts       = D(parts[5]);
                s.GpuMHz         = D(parts[6]);
                s.GpuOk          = true;
            }
            catch { s.GpuOk = false; }
        }
    }
}
