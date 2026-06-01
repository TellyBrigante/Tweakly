using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    public enum HStatus { Ok, Warning, Critical, Info }

    /// <summary>Un point du bilan de santé : catégorie, intitulé, message, verdict.</summary>
    public sealed class HealthItem
    {
        public string  Category = "";
        public string  Title    = "";       // colonne 1 (nom)
        public string  Message  = "";       // colonne 2 (ex. espace libre, ou message principal)
        public HStatus Status;              // statut de Message + pastille
        public string  Detail       = "";   // colonne 3 optionnelle (ex. état : Sain)
        public HStatus DetailStatus;        // statut de Detail
        public string  Extra        = "";   // colonne 4 optionnelle (ex. Santé du SSD : 66%)
        public HStatus ExtraStatus;         // statut de Extra
    }

    /// <summary>
    /// Bilan de santé du PC (axe DIAGNOSTIC). Chaque sous-vérification est isolée dans
    /// un try/catch : si l'une échoue (droits, matériel absent), le scan continue.
    /// </summary>
    public static class HealthCheck
    {
        public static List<HealthItem> Scan()
        {
            var items = new List<HealthItem>();
            try { CheckStorageHealth(items); }       catch { }
            try { CheckMemory(items); }              catch { }
            try { CheckGpuLink(items); }             catch { }
            try { CheckDevices(items); }             catch { }
            try { CheckUnexpectedShutdowns(items); } catch { }
            try { CheckBsod(items); }                catch { }
            try { CheckWhea(items); }                catch { }
            try { CheckDiskErrors(items); }          catch { }
            try { CheckPowerPlan(items); }           catch { }
            return items;
        }

        // ── Stockage : 1 ligne par disque = nom | espace libre | santé+usure ────
        private static void CheckStorageHealth(List<HealthItem> items)
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            // Map numéro de disque → lettres de lecteur (MSFT_Partition)
            var diskLetters = new Dictionary<int, List<string>>();
            try
            {
                using var parts = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition"));
                foreach (ManagementObject p in parts.Get())
                {
                    try
                    {
                        var dlObj = p["DriveLetter"];
                        if (dlObj == null) continue;
                        char dl = Convert.ToChar(dlObj);
                        if (!char.IsLetter(dl)) continue;
                        int dn = Convert.ToInt32(p["DiskNumber"]);
                        if (!diskLetters.TryGetValue(dn, out var list)) { list = new List<string>(); diskLetters[dn] = list; }
                        list.Add(char.ToUpperInvariant(dl) + ":");
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }

            // Map lettre → (libre, total) depuis Win32_LogicalDisk
            var letterSpace = new Dictionary<string, (double free, double size)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var ld = new ManagementObjectSearcher(
                    "SELECT DeviceID, FreeSpace, Size FROM Win32_LogicalDisk WHERE DriveType = 3");
                foreach (ManagementObject d in ld.Get())
                {
                    try
                    {
                        var id = d["DeviceID"]?.ToString() ?? "";
                        double size = d["Size"]      != null ? Convert.ToDouble(d["Size"])      : 0;
                        double free = d["FreeSpace"] != null ? Convert.ToDouble(d["FreeSpace"]) : 0;
                        if (!string.IsNullOrEmpty(id)) letterSpace[id] = (free, size);
                    }
                    catch { }
                    finally { d.Dispose(); }
                }
            }
            catch { }

            // Disques physiques
            var q = new ObjectQuery("SELECT DeviceId, FriendlyName, HealthStatus, BusType FROM MSFT_PhysicalDisk");
            using var searcher = new ManagementObjectSearcher(scope, q);

            foreach (ManagementObject disk in searcher.Get())
            {
                try
                {
                    var name = disk["FriendlyName"]?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(name)) name = "Disque";
                    int devId   = disk["DeviceId"]     != null ? Convert.ToInt32(disk["DeviceId"])     : -1;
                    int health  = disk["HealthStatus"] != null ? Convert.ToInt32(disk["HealthStatus"]) : 0;
                    int busType = disk["BusType"]      != null ? Convert.ToInt32(disk["BusType"])      : 0;

                    // % de vie restante du SSD :
                    //  - NVMe (BusType 17) : 100 − « Percentage Used » (IOCTL SMART)
                    //  - SATA (BusType 11) : attribut SMART normalisé de vie restante
                    int? life = null;
                    if (devId >= 0)
                    {
                        if (busType == 17)
                        {
                            var pu = NvmeSmart.GetPercentageUsed(devId);
                            if (pu.HasValue) life = Math.Max(0, 100 - pu.Value);
                        }
                        else if (busType == 11)
                        {
                            life = AtaSmart.GetLifeRemaining(devId);
                        }
                    }

                    // Santé (colonne 3) — HealthStatus : 0 Healthy, 1 Warning, 2 Unhealthy
                    var (healthSt, label) = health switch
                    {
                        0 => (HStatus.Ok,       "Sain"),
                        1 => (HStatus.Warning,  "Avertissement"),
                        2 => (HStatus.Critical, "Défaillant"),
                        _ => (HStatus.Info,     "État inconnu"),
                    };
                    var    detail  = label;        // colonne « état » : Sain / Avertissement / Défaillant
                    string extra   = "";           // colonne « Santé du SSD »
                    var    extraSt = HStatus.Ok;
                    if (life.HasValue)
                    {
                        extra   = $"Santé du SSD : {life}%";
                        extraSt = life <= 10 ? HStatus.Critical : life <= 25 ? HStatus.Warning : HStatus.Ok;
                    }

                    // Espace cumulé des partitions de ce disque (colonne 2)
                    double free = 0, size = 0;
                    if (devId >= 0 && diskLetters.TryGetValue(devId, out var letters))
                        foreach (var l in letters)
                            if (letterSpace.TryGetValue(l, out var sp)) { free += sp.free; size += sp.size; }

                    string spaceMsg; HStatus spaceSt;
                    if (size > 0)
                    {
                        double pct    = free / size * 100.0;
                        double freeGB = free / (1024.0 * 1024 * 1024);
                        spaceSt  = pct < 5 ? HStatus.Critical : pct < 10 ? HStatus.Warning : HStatus.Ok;
                        spaceMsg = $"{freeGB:F0} Go libres ({pct:F0} %)";
                    }
                    else { spaceSt = HStatus.Info; spaceMsg = "Pas de partition"; }

                    items.Add(new HealthItem
                    {
                        Category     = "Stockage",
                        Title        = name,
                        Message      = spaceMsg,
                        Status       = spaceSt,
                        Detail       = detail,
                        DetailStatus = healthSt,
                        Extra        = extra,
                        ExtraStatus  = extraSt,
                    });
                }
                catch { }
                finally { disk.Dispose(); }
            }
        }

        // ── Mémoire : dual channel + vitesse ────────────────────────────────────
        private static void CheckMemory(List<HealthItem> items)
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Capacity, ConfiguredClockSpeed, DeviceLocator FROM Win32_PhysicalMemory");

            var channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int sticks = 0; double totalGB = 0; int speed = 0;

            foreach (ManagementObject m in s.Get())
            {
                sticks++;
                if (m["Capacity"] != null) totalGB += Convert.ToDouble(m["Capacity"]) / (1024.0 * 1024 * 1024);
                if (speed == 0 && m["ConfiguredClockSpeed"] != null) speed = Convert.ToInt32(m["ConfiguredClockSpeed"]);
                var loc = m["DeviceLocator"]?.ToString() ?? "";
                var mt  = Regex.Match(loc, @"Channel\s*([A-Z0-9]+)", RegexOptions.IgnoreCase);
                if (mt.Success) channels.Add(mt.Groups[1].Value);
                m.Dispose();
            }
            if (sticks == 0) return;

            bool dual    = channels.Count >= 2 || sticks >= 2;
            var  st      = dual ? HStatus.Ok : HStatus.Warning;
            var  chTxt   = channels.Count >= 2 ? "Dual channel" : (sticks >= 2 ? "Multi-barrettes" : "Single channel");
            var  speedTxt = speed > 0 ? $" · {speed} MHz" : "";

            items.Add(new HealthItem
            {
                Category = "Mémoire",
                Title    = "Configuration RAM",
                Message  = $"{chTxt} · {Math.Round(totalGB)} Go · {sticks} barrette(s){speedTxt}",
                Status   = st,
            });
        }

        // ── Carte graphique : largeur du lien PCIe ──────────────────────────────
        private static void CheckGpuLink(List<HealthItem> items)
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,pcie.link.width.current,pcie.link.width.max --format=csv,noheader,nounits")
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
            });
            if (p == null) return;

            var line = p.StandardOutput.ReadLine();
            p.WaitForExit(4000);
            if (string.IsNullOrWhiteSpace(line)) return;

            var parts = line.Split(',');
            if (parts.Length < 3) return;

            var name = parts[0].Trim();
            int cur  = ParseInt(parts[1]);
            int max  = ParseInt(parts[2]);
            if (cur <= 0 || max <= 0) return;

            var st  = cur < max ? HStatus.Warning : HStatus.Ok;
            var msg = cur < max
                ? $"Lien PCIe x{cur} (max x{max}) — sous-optimal"
                : $"Lien PCIe x{cur} (optimal)";

            items.Add(new HealthItem { Category = "Carte graphique", Title = name, Message = msg, Status = st });
        }

        // ── Périphériques en erreur / conflit / désactivés ──────────────────────
        private static void CheckDevices(List<HealthItem> items)
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");

            int found = 0;
            foreach (ManagementObject d in s.Get())
            {
                try
                {
                    int code = Convert.ToInt32(d["ConfigManagerErrorCode"]);
                    var name = d["Name"]?.ToString() ?? "Périphérique";
                    // 22 = désactivé (volontaire le plus souvent) → avertissement ; autres = problème
                    var st = code == 22 ? HStatus.Warning : HStatus.Warning;
                    items.Add(new HealthItem
                    {
                        Category = "Matériel",
                        Title    = name,
                        Message  = DeviceErrorLabel(code),
                        Status   = st,
                    });
                    found++;
                }
                catch { }
                finally { d.Dispose(); }
            }

            if (found == 0)
                items.Add(new HealthItem
                {
                    Category = "Matériel",
                    Title    = "Périphériques",
                    Message  = "Aucun périphérique en erreur",
                    Status   = HStatus.Ok,
                });
        }

        private static string DeviceErrorLabel(int code) => code switch
        {
            22 => "Périphérique désactivé",
            28 => "Pilote non installé",
            10 => "Impossible de démarrer le périphérique",
            43 => "Périphérique signalé en panne par Windows",
            _  => $"Erreur (code {code})",
        };

        // ── Événements système (30 derniers jours) via le journal Windows ──────
        // Compte les événements correspondant au filtre XPath. Retourne -1 si inaccessible.
        private static int CountEvents(string xpath)
        {
            try
            {
                var query = new EventLogQuery("System", PathType.LogName, xpath);
                using var reader = new EventLogReader(query);
                int count = 0;
                for (EventRecord? ev = reader.ReadEvent(); ev != null; ev = reader.ReadEvent())
                {
                    count++;
                    ev.Dispose();
                    if (count >= 500) break;
                }
                return count;
            }
            catch { return -1; }
        }

        private static void AddEventCheck(List<HealthItem> items, string title,
            string xpath, string okMsg, string koMsgFmt, HStatus koStatus)
        {
            int n = CountEvents(xpath);
            if (n < 0) return;   // journal inaccessible → on n'affiche rien
            items.Add(new HealthItem
            {
                Category = "Système",
                Title    = title,
                Message  = n == 0 ? okMsg : string.Format(koMsgFmt, n),
                Status   = n == 0 ? HStatus.Ok : koStatus,
            });
        }

        private static void CheckUnexpectedShutdowns(List<HealthItem> items) => AddEventCheck(items,
            "Stabilité",
            "*[System[(EventID=41 or EventID=6008) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Aucun arrêt inattendu (30 derniers jours)", "{0} arrêt(s) inattendu(s) sur 30 jours", HStatus.Warning);

        private static void CheckBsod(List<HealthItem> items) => AddEventCheck(items,
            "Écrans bleus",
            "*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Aucun écran bleu (30 derniers jours)", "{0} écran(s) bleu(s) sur 30 jours", HStatus.Warning);

        private static void CheckWhea(List<HealthItem> items) => AddEventCheck(items,
            "Erreurs matérielles",
            // Level 1 = Critical, 2 = Error (on ignore les erreurs corrigées bénignes)
            "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Aucune erreur matérielle (WHEA)", "{0} erreur(s) matérielle(s) WHEA sur 30 jours", HStatus.Warning);

        private static void CheckDiskErrors(List<HealthItem> items) => AddEventCheck(items,
            "Erreurs disque",
            "*[System[(Provider[@Name='disk'] or Provider[@Name='Disk'] or Provider[@Name='Ntfs']) and (Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Aucune erreur disque (30 derniers jours)", "{0} erreur(s) disque/NTFS sur 30 jours", HStatus.Warning);

        // ── Mode d'alimentation Windows ─────────────────────────────────────────
        private static void CheckPowerPlan(List<HealthItem> items)
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
            });
            if (p == null) return;

            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);

            var m = Regex.Match(outp, @"([0-9a-fA-F]{8}-[0-9a-fA-F-]{27})");
            if (!m.Success) return;

            var guid = m.Groups[1].Value.ToLowerInvariant();
            // Hautes performances OU Performances ultimes
            bool high = guid == "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"
                     || guid == "e9a42b02-d5df-448d-aa00-03f14749eb61";

            var st  = high ? HStatus.Ok : HStatus.Warning;
            var msg = high
                ? "Mode hautes performances actif"
                : "Mode équilibré / économie — performances potentiellement bridées";

            items.Add(new HealthItem { Category = "Système", Title = "Alimentation", Message = msg, Status = st });
        }

        private static int ParseInt(string s)
            => int.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
