using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Résolution des références « brutes » Windows (\Device\Harddisk1\DR1, slot DIMM A1, etc.)
    /// vers le NOM PHYSIQUE compréhensible par l'utilisateur (« Samsung 970 EVO 1 To », « DIMM_A1
    /// : Corsair Vengeance 16 Go DDR4-3200 »). Indispensable pour que les conseils d'analyse
    /// d'événements ne disent plus « le disque #1 » mais « ton Samsung 970 EVO, qui est ton C: ».
    ///
    /// Tout est mis en cache au premier appel (les composants physiques ne changent pas
    /// pendant une session). Aucun appel WMI répété.
    /// </summary>
    public static class HardwareNamer
    {
        // ── Disques physiques ────────────────────────────────────────────────────
        // Cache : Index (entier Windows, ex. 0, 1, 2) → modèle lisible.
        private static Dictionary<int, string>? _diskByIndex;
        // Cache : Index → lettres associées (ex. "C: D:" si le disque physique 0 contient C: ET D:).
        private static Dictionary<int, string>? _lettersByDisk;

        private static void EnsureDisks()
        {
            if (_diskByIndex != null) return;
            var byIndex   = new Dictionary<int, string>();
            var letters   = new Dictionary<int, List<string>>();

            // Tous les disques physiques (Index + Model)
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive");
                foreach (ManagementObject o in s.Get())
                {
                    try
                    {
                        if (o["Index"] == null) continue;
                        int idx = Convert.ToInt32(o["Index"]);
                        string mdl = (o["Model"] as string ?? "").Trim();
                        if (mdl.Length == 0) mdl = $"disque #{idx}";
                        byIndex[idx] = mdl;
                    }
                    catch (Exception ex)
                    {
                        AppLog.ErrorOnce("hardware-namer-disk-entry", "Matériel : disque WMI ignoré", ex);
                    }
                    finally { o.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("hardware-namer-disks", "Matériel : noms des disques indisponibles", ex);
            }

            // Mapping Disque → Partition → LogicalDisk (lettre). 2 jointures WMI.
            try
            {
                using var dpQ = new ManagementObjectSearcher(
                    "SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition");
                using var plQ = new ManagementObjectSearcher(
                    "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition");

                // Partition → Lettre
                var partToLetter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (ManagementObject o in plQ.Get())
                {
                    try
                    {
                        var part = (o["Antecedent"] as string) ?? "";   // ...Win32_DiskPartition.DeviceID="Disk #X, Partition #Y"
                        var ld   = (o["Dependent"] as string)  ?? "";   // ...Win32_LogicalDisk.DeviceID="C:"
                        var pId  = Regex.Match(part, @"DeviceID=""([^""]+)""").Groups[1].Value;
                        var let  = Regex.Match(ld,   @"DeviceID=""([^""]+)""").Groups[1].Value;
                        if (pId.Length > 0 && let.Length > 0) partToLetter[pId] = let;
                    }
                    catch (Exception ex)
                    {
                        AppLog.ErrorOnce("hardware-namer-partition-letter", "Matériel : lettre de partition ignorée", ex);
                    }
                    finally { o.Dispose(); }
                }

                // Disque → Partition (donc → lettre via le map précédent)
                foreach (ManagementObject o in dpQ.Get())
                {
                    try
                    {
                        var disk = (o["Antecedent"] as string) ?? "";   // ...Win32_DiskDrive.DeviceID="\\.\PHYSICALDRIVE1"
                        var part = (o["Dependent"] as string)  ?? "";
                        var diskIdMatch = Regex.Match(disk, @"PHYSICALDRIVE(\d+)");
                        var pId = Regex.Match(part, @"DeviceID=""([^""]+)""").Groups[1].Value;
                        if (!diskIdMatch.Success || pId.Length == 0) continue;
                        int idx = int.Parse(diskIdMatch.Groups[1].Value);
                        if (partToLetter.TryGetValue(pId, out var letter))
                        {
                            if (!letters.TryGetValue(idx, out var list)) { list = new(); letters[idx] = list; }
                            if (!list.Contains(letter)) list.Add(letter);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.ErrorOnce("hardware-namer-disk-partition", "Matériel : association disque/partition ignorée", ex);
                    }
                    finally { o.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("hardware-namer-volume-map", "Matériel : association des volumes indisponible", ex);
            }

            _diskByIndex   = byIndex;
            _lettersByDisk = letters.ToDictionary(
                kv => kv.Key,
                kv => string.Join(" ", kv.Value.OrderBy(l => l)));
        }

        /// <summary>Modèle physique d'un disque (« Samsung SSD 970 EVO 1TB »). Fallback : « disque #N ».</summary>
        public static string DiskModelByIndex(int index)
        {
            EnsureDisks();
            return _diskByIndex!.TryGetValue(index, out var m) ? m : $"disque #{index}";
        }

        /// <summary>Lettres associées à un disque physique (« C: D: »). Vide si le disque ne porte aucun volume Windows.</summary>
        public static string LettersForDisk(int index)
        {
            EnsureDisks();
            return _lettersByDisk!.TryGetValue(index, out var s) ? s : "";
        }

        /// <summary>
        /// Parse « \Device\Harddisk1\DR1 » ou « \Device\Harddisk2\Partition2 » et renvoie une description
        /// lisible (« Samsung 970 EVO 1 To — C: »). Renvoie une chaîne vide si le chemin n'est pas reconnu.
        /// </summary>
        public static string DescribeDiskDevicePath(string? devicePath)
        {
            if (string.IsNullOrWhiteSpace(devicePath)) return "";
            var m = Regex.Match(devicePath, @"Harddisk(\d+)");
            if (!m.Success) return devicePath;
            int idx = int.Parse(m.Groups[1].Value);
            string model   = DiskModelByIndex(idx);
            string letters = LettersForDisk(idx);
            return letters.Length > 0 ? $"{model} ({letters})" : model;
        }

        // ── Barrettes RAM ────────────────────────────────────────────────────────
        // Cache : nom du slot (« BANK 0 / DIMM_A1 ») → description lisible
        // (« Corsair Vengeance 16 Go DDR4-3200 dans DIMM_A1 »).
        private static Dictionary<string, string>? _ramBySlot;

        private static void EnsureRam()
        {
            if (_ramBySlot != null) return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT DeviceLocator, BankLabel, Manufacturer, PartNumber, Capacity, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
                var typeMap = new Dictionary<int, string>
                    { {20,"DDR2"}, {24,"DDR3"}, {26,"DDR4"}, {34,"DDR5"} };
                foreach (ManagementObject o in s.Get())
                {
                    try
                    {
                        string slot = ((o["DeviceLocator"] as string ?? "").Trim());
                        string bank = ((o["BankLabel"]     as string ?? "").Trim());
                        string mfg  = ((o["Manufacturer"]  as string ?? "").Trim());
                        string part = ((o["PartNumber"]    as string ?? "").Trim());
                        ulong cap   = o["Capacity"] != null ? Convert.ToUInt64(o["Capacity"]) : 0;
                        int   spd   = o["Speed"]    != null ? Convert.ToInt32(o["Speed"])    : 0;
                        string type = typeMap.TryGetValue(Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0), out var t) ? t : "DDR";

                        var bits = new List<string>();
                        if (!string.IsNullOrWhiteSpace(mfg) && mfg != "Unknown") bits.Add(mfg);
                        if (!string.IsNullOrWhiteSpace(part)) bits.Add(part);
                        if (cap > 0) bits.Add($"{cap / (1024 * 1024 * 1024)} Go");
                        if (spd > 0) bits.Add($"{type}-{spd}");
                        string desc = bits.Count > 0 ? string.Join(" ", bits) : type;

                        string key = !string.IsNullOrWhiteSpace(slot) ? slot : bank;
                        if (key.Length > 0) map[key] = $"{desc} dans {key}";
                    }
                    catch (Exception ex)
                    {
                        AppLog.ErrorOnce("hardware-namer-ram-entry", "Matériel : module mémoire ignoré", ex);
                    }
                    finally { o.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("hardware-namer-ram", "Matériel : noms des modules mémoire indisponibles", ex);
            }
            _ramBySlot = map;
        }

        /// <summary>Description lisible d'un slot DIMM. Fallback : juste le nom du slot.</summary>
        public static string DescribeRamSlot(string? slot)
        {
            if (string.IsNullOrWhiteSpace(slot)) return "";
            EnsureRam();
            return _ramBySlot!.TryGetValue(slot, out var d) ? d : slot;
        }

        // ── Aide humaine pour les codes BSOD ─────────────────────────────────────
        // Sous-ensemble des bugchecks les plus courants — utilisé par le narrateur pour
        // dire « BSOD KERNEL_DATA_INPAGE_ERROR (0x7A) » au lieu de « 0x7A » seul.
        private static readonly Dictionary<uint, string> BugCheckNames = new()
        {
            { 0x0A, "IRQL_NOT_LESS_OR_EQUAL" },
            { 0x1E, "KMODE_EXCEPTION_NOT_HANDLED" },
            { 0x3B, "SYSTEM_SERVICE_EXCEPTION" },
            { 0x50, "PAGE_FAULT_IN_NONPAGED_AREA" },
            { 0x4A, "IRQL_GT_ZERO_AT_SYSTEM_SERVICE" },
            { 0x4E, "PFN_LIST_CORRUPT" },
            { 0x7A, "KERNEL_DATA_INPAGE_ERROR" },
            { 0x7E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED" },
            { 0x9F, "DRIVER_POWER_STATE_FAILURE" },
            { 0xC1, "SPECIAL_POOL_DETECTED_MEMORY_CORRUPTION" },
            { 0xC2, "BAD_POOL_CALLER" },
            { 0xC4, "DRIVER_VERIFIER_DETECTED_VIOLATION" },
            { 0xC5, "DRIVER_CORRUPTED_EXPOOL" },
            { 0xCA, "PNP_DETECTED_FATAL_ERROR" },
            { 0xD1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL" },
            { 0xDA, "SYSTEM_PTE_MISUSE" },
            { 0xE3, "RESOURCE_NOT_OWNED" },
            { 0xEA, "THREAD_STUCK_IN_DEVICE_DRIVER" },
            { 0xF4, "CRITICAL_OBJECT_TERMINATION" },
            { 0x101, "CLOCK_WATCHDOG_TIMEOUT" },
            { 0x109, "CRITICAL_STRUCTURE_CORRUPTION" },
            { 0x116, "VIDEO_TDR_FAILURE" },
            { 0x117, "VIDEO_TDR_TIMEOUT_DETECTED" },
            { 0x119, "VIDEO_SCHEDULER_INTERNAL_ERROR" },
            { 0x124, "WHEA_UNCORRECTABLE_ERROR" },
            { 0x133, "DPC_WATCHDOG_VIOLATION" },
            { 0x139, "KERNEL_SECURITY_CHECK_FAILURE" },
            { 0x144, "BUGCODE_USB3_DRIVER" },
            { 0x1A, "MEMORY_MANAGEMENT" },
        };

        /// <summary>Nom symbolique d'un BugCheck Windows (« KERNEL_DATA_INPAGE_ERROR »). Vide si inconnu.</summary>
        public static string BugCheckName(uint code) => BugCheckNames.TryGetValue(code, out var n) ? n : "";

        /// <summary>Famille du BugCheck : « disk », « ram », « driver », « gpu », « cpu », « unknown ».</summary>
        public static string BugCheckFamily(uint code) => code switch
        {
            0x7A or 0x4A or 0x77 => "disk",                     // I/O pagination, IRQL+disque
            0x1A or 0x4E or 0x50 or 0xC1 or 0xC5 or 0x109 or 0x124 => "ram-or-driver",
            0x116 or 0x117 or 0x119 => "gpu",
            0x101 or 0x133 => "cpu",                            // watchdogs CPU
            0xD1 or 0x3B or 0x7E or 0xEA or 0x9F => "driver",   // drivers spécifiquement
            _ => "unknown"
        };
    }
}
