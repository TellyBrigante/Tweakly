using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Snapshot HARDWARE + ÉTAT WINDOWS pris UNE FOIS au démarrage de la capture.
    /// L'analyseur utilise ces infos pour ÉLIMINER les fausses pistes au lieu de
    /// poser des questions du style « ton jeu est-il sur un SSD ? » alors qu'on
    /// peut le savoir tout seul.
    ///
    /// Coût : ~50-150 ms (WMI MSFT_PhysicalDisk + scan process une fois). Appelé
    /// UNE seule fois au début de la capture, donc invisible pour la perf en jeu.
    /// </summary>
    public sealed class SystemContextSnap
    {
        public double TotalRamGb;                // RAM totale installée
        public string ActivePowerPlan = "";      // ex. "Ultimate Performance" / "Balanced"
        public string ActivePowerPlanGuid = "";
        public bool HagsEnabled;                 // Hardware-accelerated GPU Scheduling
        public bool HvciEnabled;                 // Memory Integrity
        public bool GameModeEnabled;
        public bool VbsRunning;                  // VBS/hyperviseur réellement actif (taxe virtu)
        public string CpuName = "";
        public string GpuName = "";
        public int MonitorRefreshRate;   // Hz du moniteur principal (60, 144, 240, 320…)

        /// <summary>Mapping exe (sans dossier) → chemin complet de l'exécutable.</summary>
        public Dictionary<string, string> ExePaths = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Type de disque par lettre (C: → NVMe, D: → HDD, …).</summary>
        public Dictionary<char, StorageKind> DiskByLetter = new();

        public enum StorageKind { Unknown, NVMe, SsdSata, HDD }

        public static SystemContextSnap Capture()
        {
            var ctx = new SystemContextSnap();
            try { ctx.TotalRamGb = TotalPhysicalRamGb(); } catch { }
            try
            {
                var (planName, planGuid) = QueryActivePowerPlan();
                ctx.ActivePowerPlan = planName;
                ctx.ActivePowerPlanGuid = planGuid;
            }
            catch { }
            try { ctx.HagsEnabled = QueryHags(); } catch { }
            try { ctx.HvciEnabled = QueryHvci(); } catch { }
            try { ctx.VbsRunning = QueryVbsRunning(); } catch { }
            try { ctx.GameModeEnabled = QueryGameMode(); } catch { }
            try { ctx.CpuName = QueryCpuName(); } catch { }
            try { ctx.GpuName = QueryGpuName(); } catch { }
            try { ctx.MonitorRefreshRate = QueryMonitorRefreshRate(); } catch { }
            try { ctx.ExePaths = SnapshotExePaths(); } catch { ctx.ExePaths = new(); }
            try { ctx.DiskByLetter = SnapshotDisks(); } catch { ctx.DiskByLetter = new(); }
            return ctx;
        }

        // ───────────────────────── Storage (réutilise le pattern HealthCheck) ─────────────────────────
        private static Dictionary<char, StorageKind> SnapshotDisks()
        {
            var letterToDisk = new Dictionary<char, int>();
            using (var ms = new ManagementObjectSearcher(@"\\.\ROOT\Microsoft\Windows\Storage",
                                                         "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
            {
                foreach (ManagementObject p in ms.Get())
                {
                    var dlObj = p["DriveLetter"];
                    if (dlObj == null) continue;
                    char dl = Convert.ToChar(dlObj);
                    if (dl == '\0') continue;
                    int dn = Convert.ToInt32(p["DiskNumber"]);
                    letterToDisk[char.ToUpperInvariant(dl)] = dn;
                }
            }

            var diskKind = new Dictionary<int, StorageKind>();
            using (var ms = new ManagementObjectSearcher(@"\\.\ROOT\Microsoft\Windows\Storage",
                                                         "SELECT DeviceId, BusType, MediaType FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject d in ms.Get())
                {
                    if (!int.TryParse(d["DeviceId"]?.ToString(), out int devId)) continue;
                    ushort bus = Convert.ToUInt16(d["BusType"]);
                    ushort media = Convert.ToUInt16(d["MediaType"]);   // 3=HDD, 4=SSD, 5=SCM
                    var kind = bus == 17 ? StorageKind.NVMe
                             : (media == 4 ? StorageKind.SsdSata
                             : (media == 3 ? StorageKind.HDD : StorageKind.Unknown));
                    diskKind[devId] = kind;
                }
            }

            var result = new Dictionary<char, StorageKind>();
            foreach (var (letter, dn) in letterToDisk)
                if (diskKind.TryGetValue(dn, out var k)) result[letter] = k;
            return result;
        }

        public StorageKind StorageOf(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || fullPath.Length < 2 || fullPath[1] != ':')
                return StorageKind.Unknown;
            return DiskByLetter.TryGetValue(char.ToUpperInvariant(fullPath[0]), out var k) ? k : StorageKind.Unknown;
        }

        // ───────────────────────── Process paths ─────────────────────────
        private static Dictionary<string, string> SnapshotExePaths()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (string.IsNullOrEmpty(path)) continue;
                    string exe = Path.GetFileName(path);
                    if (!dict.ContainsKey(exe)) dict[exe] = path;
                }
                catch { /* accès refusé sur certains process système — pas grave */ }
                finally { try { p.Dispose(); } catch { } }
            }
            return dict;
        }

        // ───────────────────────── États Windows ─────────────────────────
        private static double TotalPhysicalRamGb()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref m) ? m.ullTotalPhys / 1024.0 / 1024.0 / 1024.0 : 0;
        }

        private static (string, string) QueryActivePowerPlan()
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return ("", "");
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                // Output : "Power Scheme GUID: <guid>  (Nom du plan)"
                var m = System.Text.RegularExpressions.Regex.Match(output, @"GUID:\s*([0-9a-f-]+)\s*\((.+?)\)");
                return m.Success ? (m.Groups[2].Value.Trim(), m.Groups[1].Value.Trim()) : ("", "");
            }
            catch { return ("", ""); }
        }

        private static bool QueryHags()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
                return k?.GetValue("HwSchMode") is int v && v == 2;
            }
            catch { return false; }
        }

        private static bool QueryHvci()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
                return k?.GetValue("Enabled") is int v && v == 1;
            }
            catch { return false; }
        }

        /// <summary>
        /// VBS/hyperviseur réellement EN COURS d'exécution (la « taxe de virtualisation »).
        /// Win32_DeviceGuard.VirtualizationBasedSecurityStatus == 2 = VBS running. Validé en
        /// réel le 2026-06-13 : status 2 sur la machine de l'utilisateur (hyperviseur Auto)
        /// même avec HVCI off. Ajoute du jitter de scheduling sur le thread principal des jeux.
        /// </summary>
        private static bool QueryVbsRunning()
        {
            try
            {
                using var ms = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\DeviceGuard",
                    "SELECT VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard");
                foreach (ManagementObject o in ms.Get())
                    return Convert.ToInt32(o["VirtualizationBasedSecurityStatus"]) == 2;
            }
            catch { }
            return false;
        }

        private static bool QueryGameMode()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
                return !(k?.GetValue("AutoGameModeEnabled") is int v && v == 0);
            }
            catch { return true; }
        }

        private static string QueryCpuName()
        {
            try
            {
                using var ms = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (ManagementObject p in ms.Get()) return (p["Name"]?.ToString() ?? "").Trim();
            }
            catch { }
            return "";
        }

        private static string QueryGpuName()
        {
            try
            {
                using var ms = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_VideoController WHERE PNPDeviceID LIKE 'PCI\\\\%'");
                string best = "";
                foreach (ManagementObject c in ms.Get())
                {
                    string name = (c["Name"]?.ToString() ?? "").Trim();
                    if (name.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("radeon", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("geforce", StringComparison.OrdinalIgnoreCase))
                        return name;
                    if (string.IsNullOrEmpty(best)) best = name;
                }
                return best;
            }
            catch { return ""; }
        }

        /// <summary>
        /// Refresh rate effectif du moniteur principal (Hz). EnumDisplaySettings avec
        /// ENUM_CURRENT_SETTINGS donne la valeur réellement configurée par l'utilisateur,
        /// pas la valeur native du moniteur. C'est CETTE valeur qui définit ce que
        /// l'utilisateur vise (un 320 Hz à 320 Hz = il veut 320 fps).
        /// </summary>
        private static int QueryMonitorRefreshRate()
        {
            var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode))
                return (int)mode.dmDisplayFrequency;
            return 0;
        }

        // ───────────────────────── P/Invoke ─────────────────────────
        private const int ENUM_CURRENT_SETTINGS = -1;
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency,
                        dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2,
                        dmPanningWidth, dmPanningHeight;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    }
}
