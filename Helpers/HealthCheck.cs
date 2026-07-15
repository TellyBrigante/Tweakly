using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
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
            RunCheck(items, "Stockage", "État des disques", CheckStorageHealth);
            RunCheck(items, "Mémoire", "Configuration RAM", CheckMemory);
            RunCheck(items, "Carte graphique", "Lien PCIe", CheckGpuLink);
            RunCheck(items, "Carte graphique", "Température GPU", CheckGpuTemp);
            RunCheck(items, "Carte graphique", "Plantages pilote GPU", CheckGpuCrashes);
            RunCheck(items, "Matériel", "Périphériques", CheckDevices);
            RunCheck(items, "Système", "Stabilité", CheckUnexpectedShutdowns);
            RunCheck(items, "Système", "Écrans bleus", CheckBsod);
            RunCheck(items, "Système", "Erreurs matérielles", CheckWhea);
            RunCheck(items, "Système", "Erreurs disque", CheckDiskErrors);
            RunCheck(items, "Système", "Intégrité fichiers", CheckFsCorruption);
            RunCheck(items, "Système", "Services Windows", CheckServices);
            RunCheck(items, "Système", "Pilotes système", CheckDriverLoad);
            RunCheck(items, "Système", "Plantages de logiciels", CheckAppCrashes);
            return items;
        }

        private static void RunCheck(
            List<HealthItem> items,
            string category,
            string title,
            Action<List<HealthItem>> check)
        {
            try
            {
                check(items);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Bilan de santé : {title}", ex);
                items.Add(new HealthItem
                {
                    Category = category,
                    Title = title,
                    Message = "Contrôle indisponible",
                    Status = HStatus.Info,
                });
            }
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
                    catch (Exception ex)
                    {
                        AppLog.ErrorOnce("health-storage-partition", "Bilan de santé : partition ignorée", ex);
                    }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("health-storage-partition-map", "Bilan de santé : association disque/partition indisponible", ex);
            }

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
                    catch (Exception ex)
                    {
                        AppLog.ErrorOnce("health-storage-logical-disk", "Bilan de santé : volume logique ignoré", ex);
                    }
                    finally { d.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("health-storage-space-map", "Bilan de santé : espace disque indisponible", ex);
            }

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

                    // Dégradation fine (signes avant-coureurs de panne) : erreurs média NVMe / secteurs réalloués SATA.
                    // Garde-fous : on ignore les valeurs aberrantes (offset/lecture douteux) pour éviter les faux positifs.
                    long degradation = 0;
                    if (busType == 17 && devId >= 0)
                    {
                        var me = NvmeSmart.GetMediaErrors(devId);
                        if (me.HasValue && me.Value > 0 && me.Value < 1_000_000_000UL) degradation = (long)me.Value;
                    }
                    else if (busType == 11 && devId >= 0)
                    {
                        var rs = AtaSmart.GetReallocatedSectors(devId);
                        if (rs.HasValue && rs.Value > 0 && rs.Value < 1_000_000) degradation = rs.Value;
                    }
                    if (degradation > 0 && healthSt == HStatus.Ok)
                    {
                        healthSt = HStatus.Warning;
                        detail   = busType == 17 ? $"Erreurs média : {degradation}" : $"Secteurs réalloués : {degradation}";
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
                catch (Exception ex)
                {
                    AppLog.ErrorOnce("health-storage-physical-disk", "Bilan de santé : disque physique ignoré", ex);
                }
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
            if (p == null)
                throw new InvalidOperationException("nvidia-smi n'a pas démarré");

            var line = p.StandardOutput.ReadLine();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(entireProcessTree: true); }
                catch (Exception ex) { AppLog.ErrorOnce("health-gpu-link-kill", "Bilan de santé : arrêt de nvidia-smi impossible", ex); }
                throw new TimeoutException("nvidia-smi n'a pas répondu en 4 s");
            }
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidDataException("nvidia-smi n'a retourné aucun lien PCIe");

            var parts = line.Split(',');
            if (parts.Length < 3)
                throw new InvalidDataException("réponse PCIe de nvidia-smi incomplète");

            var name = parts[0].Trim();
            int cur  = ParseInt(parts[1]);
            int max  = ParseInt(parts[2]);
            if (cur <= 0 || max <= 0)
                throw new InvalidDataException("largeur du lien PCIe invalide");

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
                catch (Exception ex)
                {
                    AppLog.ErrorOnce("health-device-entry", "Bilan de santé : périphérique ignoré", ex);
                }
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
        // Compte les événements correspondant au filtre XPath.
        private static int CountEvents(string log, string xpath)
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath);
            using var reader = new EventLogReader(query);
            int count = 0;
            for (EventRecord? ev = reader.ReadEvent(); ev != null; ev = reader.ReadEvent())
            {
                count++;
                ev.Dispose();
                if (count >= 999) break;
            }
            return count;
        }

        private static void AddEventCheck(List<HealthItem> items, string category, string log, string title,
            string xpath, string okMsg, string koMsgFmt, HStatus koStatus)
        {
            int n = CountEvents(log, xpath);
            items.Add(new HealthItem
            {
                Category = category,
                Title    = title,
                Message  = n == 0 ? okMsg : string.Format(koMsgFmt, n),
                Status   = n == 0 ? HStatus.Ok : koStatus,
            });
        }

        // Variante qui, en cas de problème, nomme les éléments concernés (nom = 1re propriété de l'événement).
        private static void AddEventCheckNamed(List<HealthItem> items, string category, string log, string title,
            string xpath, string okMsg, string koMsgFmt, HStatus koStatus)
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath);
            using var reader = new EventLogReader(query);

            int total = 0;
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (EventRecord? ev = reader.ReadEvent(); ev != null; ev = reader.ReadEvent())
            {
                try
                {
                    total++;
                    if (ev.Properties != null && ev.Properties.Count > 0)
                    {
                        var name = CleanName(ev.Properties[0].Value?.ToString());
                        if (name.Length > 0) { byName.TryGetValue(name, out int c); byName[name] = c + 1; }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.ErrorOnce("health-event-entry:" + log, "Bilan de santé : événement illisible", ex);
                }
                finally { ev.Dispose(); }
                if (total >= 999) break;
            }

            string  msg;
            HStatus st;
            if (total == 0) { msg = okMsg; st = HStatus.Ok; }
            else
            {
                st  = koStatus;
                msg = string.Format(koMsgFmt, total);
                if (byName.Count > 0 && byName.Count <= 3)
                {
                    msg += " : " + string.Join(", ", byName.Keys);   // peu d'éléments → on les liste
                }
                else if (byName.Count > 3)
                {
                    string topName = ""; int topCount = 0;            // beaucoup → on cite le pire
                    foreach (var kv in byName) if (kv.Value > topCount) { topCount = kv.Value; topName = kv.Key; }
                    msg += $" — surtout {topName} ({topCount} fois)";
                }
            }
            items.Add(new HealthItem { Category = category, Title = title, Message = msg, Status = st });
        }

        private static string CleanName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var s = raw.Trim();
            int nl = s.IndexOfAny(new[] { '\r', '\n' });   // event 7026 = liste multi-ligne → 1re entrée
            if (nl > 0) s = s.Substring(0, nl).Trim();
            if (s.Length > 40) s = s.Substring(0, 39) + "…";
            return s;
        }

        private static void CheckUnexpectedShutdowns(List<HealthItem> items) => AddEventCheck(items,
            "Système", "System", "Stabilité",
            "*[System[(EventID=41 or EventID=6008) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Aucun arrêt inattendu (30 derniers jours)", "{0} arrêt(s) inattendu(s) sur 30 jours", HStatus.Warning);

        private static void CheckBsod(List<HealthItem> items) => AddEventCheck(items,
            "Système", "System", "Écrans bleus",
            "*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Aucun écran bleu (30 derniers jours)", "{0} écran(s) bleu(s) sur 30 jours", HStatus.Warning);

        private static void CheckWhea(List<HealthItem> items) => AddEventCheck(items,
            "Système", "System", "Erreurs matérielles",
            // Level 1 = Critical, 2 = Error (on ignore les erreurs corrigées bénignes)
            "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Aucune erreur matérielle (WHEA)", "{0} erreur(s) matérielle(s) WHEA sur 30 jours", HStatus.Warning);

        private static void CheckDiskErrors(List<HealthItem> items) => AddEventCheck(items,
            "Système", "System", "Erreurs disque",
            "*[System[(Provider[@Name='disk'] or Provider[@Name='Disk'] or Provider[@Name='Ntfs']) and (Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Aucune erreur disque (30 derniers jours)", "{0} erreur(s) disque/NTFS sur 30 jours", HStatus.Warning);

        // Corruption du système de fichiers (NTFS event 55) — grave → critique si présent
        private static void CheckFsCorruption(List<HealthItem> items) => AddEventCheck(items,
            "Système", "System", "Intégrité fichiers",
            "*[System[Provider[@Name='Microsoft-Windows-Ntfs'] and EventID=55 and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Aucune corruption détectée (30 derniers jours)", "{0} corruption(s) du système de fichiers", HStatus.Critical);

        // Services Windows qui plantent / s'arrêtent anormalement (7034 = crash, 7031 = arrêt inattendu)
        private static void CheckServices(List<HealthItem> items) => AddEventCheckNamed(items,
            "Système", "System", "Services Windows",
            "*[System[Provider[@Name='Service Control Manager'] and (EventID=7034 or EventID=7031) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Aucun service planté (30 derniers jours)", "{0} service(s) Windows planté(s) sur 30 jours", HStatus.Warning);

        // Pilotes / services qui échouent à démarrer au boot (7000 = service KO, 7026 = pilote boot KO)
        private static void CheckDriverLoad(List<HealthItem> items) => AddEventCheckNamed(items,
            "Système", "System", "Pilotes système",
            "*[System[Provider[@Name='Service Control Manager'] and (EventID=7000 or EventID=7026) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Tous les pilotes ont démarré (30 derniers jours)", "{0} pilote(s)/service(s) en échec de démarrage", HStatus.Warning);

        // Plantages de logiciels (journal Application) : 1000 = Application Error, 1002 = Hang.
        // Nomme le(s) logiciel(s) le(s) plus instable(s).
        private static void CheckAppCrashes(List<HealthItem> items) => AddEventCheckNamed(items,
            "Système", "Application", "Plantages de logiciels",
            "*[System[(EventID=1000 or EventID=1002) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Aucun plantage de logiciel (30 derniers jours)", "{0} plantage(s) de logiciel sur 30 jours", HStatus.Warning);

        // Plantages du pilote GPU (TDR) : « Display driver stopped responding » (event 4101)
        private static void CheckGpuCrashes(List<HealthItem> items) => AddEventCheck(items,
            "Carte graphique", "System", "Plantages pilote GPU",
            "*[System[Provider[@Name='Display'] and EventID=4101 and TimeCreated[timediff(@SystemTime) <= 2592000000]]]",
            "Aucun plantage du pilote GPU (30 derniers jours)", "{0} plantage(s) du pilote GPU sur 30 jours", HStatus.Warning);

        // Surchauffe : température GPU instantanée vs seuils (refroidissement défaillant)
        private static void CheckGpuTemp(List<HealthItem> items)
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi",
                "--query-gpu=temperature.gpu --format=csv,noheader,nounits")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            });
            if (p == null)
                throw new InvalidOperationException("nvidia-smi n'a pas démarré");
            var line = p.StandardOutput.ReadLine();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(entireProcessTree: true); }
                catch (Exception ex) { AppLog.ErrorOnce("health-gpu-temperature-kill", "Bilan de santé : arrêt de nvidia-smi impossible", ex); }
                throw new TimeoutException("nvidia-smi n'a pas répondu en 4 s");
            }
            if (!int.TryParse((line ?? "").Trim(), out int t) || t <= 0 || t > 150)
                throw new InvalidDataException("température GPU invalide");

            var st  = t >= 90 ? HStatus.Critical : t >= 83 ? HStatus.Warning : HStatus.Ok;
            var msg = t >= 83 ? $"{t} °C — élevée (refroidissement à vérifier)" : $"{t} °C — normale";
            items.Add(new HealthItem { Category = "Carte graphique", Title = "Température GPU", Message = msg, Status = st });
        }

        private static int ParseInt(string s)
            => int.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
