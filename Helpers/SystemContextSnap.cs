using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
            ctx.TotalRamGb = ReadOrDefault("ram", "mémoire totale", TotalPhysicalRamGb, 0d);
            if (PowerPlanManager.TryReadActivePlan(
                out string planName, out string planGuid, out string planError))
            {
                ctx.ActivePowerPlan = planName;
                ctx.ActivePowerPlanGuid = planGuid;
            }
            else
            {
                AppLog.WriteOnce("context-power-plan", "Contexte système : " + planError);
            }

            ctx.HagsEnabled = ReadOrDefault("hags", "état HAGS", QueryHags, false);
            ctx.HvciEnabled = ReadOrDefault("hvci", "état HVCI", QueryHvci, false);
            ctx.VbsRunning = ReadOrDefault("vbs", "état VBS", QueryVbsRunning, false);
            ctx.GameModeEnabled = ReadOrDefault("game-mode", "Mode Jeu", QueryGameMode, true);
            ctx.CpuName = ReadOrDefault("cpu", "nom du CPU", QueryCpuName, "");
            ctx.GpuName = ReadOrDefault("gpu", "nom du GPU", QueryGpuName, "");
            ctx.MonitorRefreshRate = ReadOrDefault(
                "monitor-refresh", "fréquence de l'écran", QueryMonitorRefreshRate, 0);
            ctx.ExePaths = ReadOrDefault(
                "process-paths", "chemins des processus", SnapshotExePaths,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            ctx.DiskByLetter = ReadOrDefault(
                "storage", "type des disques", SnapshotDisks, new Dictionary<char, StorageKind>());
            return ctx;
        }

        private static T ReadOrDefault<T>(string key, string label, Func<T> read, T fallback)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("context-" + key, "Contexte système : " + label, ex);
                return fallback;
            }
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
                    using (p)
                    {
                        var dlObj = p["DriveLetter"];
                        if (dlObj == null) continue;
                        char dl = Convert.ToChar(dlObj);
                        if (dl == '\0') continue;
                        int dn = Convert.ToInt32(p["DiskNumber"]);
                        letterToDisk[char.ToUpperInvariant(dl)] = dn;
                    }
                }
            }

            var diskKind = new Dictionary<int, StorageKind>();
            using (var ms = new ManagementObjectSearcher(@"\\.\ROOT\Microsoft\Windows\Storage",
                                                         "SELECT DeviceId, BusType, MediaType FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject d in ms.Get())
                {
                    using (d)
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
                using (p)
                {
                    try
                    {
                        string? path = p.MainModule?.FileName;
                        if (string.IsNullOrEmpty(path)) continue;
                        string exe = Path.GetFileName(path);
                        if (!dict.ContainsKey(exe)) dict[exe] = path;
                    }
                    catch { /* accès refusé sur certains process système — attendu */ }
                }
            }
            return dict;
        }

        // ───────────────────────── États Windows ─────────────────────────
        private static double TotalPhysicalRamGb()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref m))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return m.ullTotalPhys / 1024.0 / 1024.0 / 1024.0;
        }

        private static bool QueryHags()
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            return k?.GetValue("HwSchMode") is int v && v == 2;
        }

        private static bool QueryHvci()
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            return k?.GetValue("Enabled") is int v && v == 1;
        }

        /// <summary>
        /// VBS/hyperviseur réellement EN COURS d'exécution (la « taxe de virtualisation »).
        /// Win32_DeviceGuard.VirtualizationBasedSecurityStatus == 2 = VBS running. Validé en
        /// réel le 2026-06-13 : status 2 sur la machine de l'utilisateur (hyperviseur Auto)
        /// même avec HVCI off. Ajoute du jitter de scheduling sur le thread principal des jeux.
        /// </summary>
        private static bool QueryVbsRunning()
        {
            using var ms = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard");
            foreach (ManagementObject o in ms.Get())
            {
                using (o)
                    return Convert.ToInt32(o["VirtualizationBasedSecurityStatus"]) == 2;
            }
            return false;
        }

        private static bool QueryGameMode()
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
            return !(k?.GetValue("AutoGameModeEnabled") is int v && v == 0);
        }

        private static string QueryCpuName()
        {
            using var ms = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject p in ms.Get())
            {
                using (p)
                    return (p["Name"]?.ToString() ?? "").Trim();
            }
            return "";
        }

        private static string QueryGpuName()
        {
            using var ms = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_VideoController WHERE PNPDeviceID LIKE 'PCI\\\\%'");
            string best = "";
            foreach (ManagementObject c in ms.Get())
            {
                using (c)
                {
                    string name = (c["Name"]?.ToString() ?? "").Trim();
                    if (name.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("radeon", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("geforce", StringComparison.OrdinalIgnoreCase))
                        return name;
                    if (string.IsNullOrEmpty(best)) best = name;
                }
            }
            return best;
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
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // ───────────────────────── P/Invoke ─────────────────────────
        private const int ENUM_CURRENT_SETTINGS = -1;
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
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
