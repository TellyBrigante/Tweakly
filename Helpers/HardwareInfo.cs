using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Collecte des informations materielles (CPU/RAM/GPU/OS/disques/carte mere) +
    /// helpers de deduction (architecture, socket, channels RAM, chipset, type disque).
    /// EXTRAIT de Pages/PageSpecs.xaml.cs en v1.3.3 (audit M-6 : fichier > 1300 lignes)
    /// — logique pure sans UI, deplacee telle quelle.
    /// </summary>
    internal static class HardwareInfo
    {
        public static Dictionary<string, string> Collect()
        {
            var d = new Dictionary<string, string>();

            // ── CPU ──────────────────────────────────────────────────────────
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, ThreadCount, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor");
                foreach (ManagementObject o in s.Get())
                {
                    // Nettoyage du nom : supprimer (R), (TM), "CPU", "@ X.XXGHz"
                    var raw = o["Name"]?.ToString()?.Trim() ?? "N/A";
                    var name = raw;
                    name = name.Replace("(R)", "").Replace("(TM)", "").Replace("(C)", "");
                    name = Regex.Replace(name, @"\s*@\s*[\d.]+\s*GHz", "", RegexOptions.IgnoreCase);
                    name = Regex.Replace(name, @"\bCPU\b", "", RegexOptions.IgnoreCase);
                    name = Regex.Replace(name, @"\s{2,}", " ").Trim();

                    var cores   = o["NumberOfCores"]?.ToString() ?? "?";
                    var threads = o["ThreadCount"]?.ToString()
                               ?? o["NumberOfLogicalProcessors"]?.ToString() ?? "?";
                    var mhz     = o["MaxClockSpeed"] != null ? Convert.ToDouble(o["MaxClockSpeed"]) : 0;
                    var freq    = mhz > 0 ? $"{Math.Round(mhz / 1000.0, 2):0.##} GHz" : "N/A";
                    var l2kb    = o["L2CacheSize"] != null ? Convert.ToDouble(o["L2CacheSize"]) : 0;
                    var l3kb    = o["L3CacheSize"] != null ? Convert.ToDouble(o["L3CacheSize"]) : 0;
                    var cache3  = l3kb > 0 ? $"  |  L3 : {Math.Round(l3kb / 1024.0, 0)} Mo" : "";
                    var cache2  = l2kb > 0 ? $"  |  L2 : {Math.Round(l2kb / 1024.0, 0)} Mo" : "";
                    var arch    = DeriveCpuArch(name);
                    var socket  = o["SocketDesignation"]?.ToString()?.Trim() ?? "";
                    // SocketDesignation WMI = code interne (ex. "U3E1") qui ne dit rien à
                    // l'utilisateur. On le remplace par le vrai nom du socket déduit du nom
                    // commercial (LGA1851 pour Core Ultra 2xx, AM5 pour Ryzen 9000…).
                    var realSocket = DeriveCpuSocket(name);
                    if (realSocket.Length > 0) socket = realSocket;

                    // Hyper-Threading actif si threads > cores
                    int.TryParse(cores,   out int ic);
                    int.TryParse(threads, out int it);
                    var ht = (ic > 0 && it > 0)
                        ? (it > ic ? "Hyper-Threading actif" : "Hyper-Threading absent / désactivé")
                        : "";

                    // Lignes 2-4 du bloc CPU :
                    //   ligne 2 : cœurs/threads + L3 + L2
                    //   ligne 3 : fréq. max (extraite en big number côté UI)
                    //   ligne 4 : architecture + socket
                    //   ligne 5 : hyper-threading (si dispo)
                    var archLine = "";
                    if (arch.Length > 0 || socket.Length > 0)
                    {
                        var parts = new List<string>();
                        if (arch.Length > 0)   parts.Add(arch);
                        if (socket.Length > 0) parts.Add($"Socket : {socket}");
                        archLine = "\n" + string.Join("  |  ", parts);
                    }
                    var htLine = ht.Length > 0 ? "\n" + ht : "";

                    d["cpu"] = $"{name}\n{cores}C / {threads}T{cache3}{cache2}\nFréq. max : {freq}{archLine}{htLine}";
                    o.Dispose();
                    break;
                }
            }
            catch { d["cpu"] = "Indisponible"; }

            // ── RAM ──────────────────────────────────────────────────────────
            try
            {
                using var s = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory");
                var mods = new List<ManagementObject>();
                foreach (ManagementObject o in s.Get()) mods.Add(o);

                if (mods.Count > 0)
                {
                    var first   = mods[0];
                    long total  = 0;
                    foreach (var m in mods) total += Convert.ToInt64(m["Capacity"]);
                    var totalGb = Math.Round(total / (double)(1L << 30), 0);
                    var perGb   = Math.Round(Convert.ToInt64(first["Capacity"]) / (double)(1L << 30), 0);

                    var typeMap = new Dictionary<int, string>
                        { {20,"DDR2"}, {24,"DDR3"}, {26,"DDR4"}, {34,"DDR5"} };
                    typeMap.TryGetValue(Convert.ToInt32(first["SMBIOSMemoryType"] ?? 0), out var mtype);
                    if (string.IsNullOrEmpty(mtype)) mtype = "DDR";

                    var speed = first["Speed"]?.ToString() ?? "?";
                    var volt  = first["ConfiguredVoltage"] != null ? Convert.ToInt32(first["ConfiguredVoltage"]) : 0;
                    var vs    = volt > 0 ? $" | {Math.Round(volt / 1000.0, 2)}V" : "";

                    var rt = $"Total : {totalGb} Go ({mods.Count} x {perGb} Go)\nType : {mtype} @ {speed} MHz{vs}";

                    // Slots + capacité max
                    try
                    {
                        using var sa = new ManagementObjectSearcher(
                            "SELECT MemoryDevices, MaxCapacity FROM Win32_PhysicalMemoryArray");
                        foreach (ManagementObject arr in sa.Get())
                        {
                            var slots   = Convert.ToInt32(arr["MemoryDevices"]);
                            var maxKb   = Convert.ToInt64(arr["MaxCapacity"]);
                            var maxGb   = Math.Round(maxKb / 1_048_576.0, 0);
                            var perSlot = slots > 0 && maxGb > 0 && maxGb <= 2048
                                        ? $"{Math.Round(maxGb / slots, 0)} Go" : "N/A";
                            rt += $"\nSlots : {mods.Count} / {slots}  |  Max : {perSlot} par slot";
                            arr.Dispose();
                            break;
                        }
                    }
                    catch { }

                    // Configuration channels (déduit du nb de modules vs slots)
                    var channels = DeriveRamChannels(mods.Count);
                    if (channels.Length > 0) rt += $"\nConfiguration : {channels}";

                    // Part number (S/N) + déduire le fabricant
                    var pn = first["PartNumber"]?.ToString()?.Trim() ?? "";
                    pn = Regex.Replace(pn, @"\s{2,}", " ");
                    if (pn.Length > 1 && pn != "Array Handle")
                    {
                        var brand = DeriveRamBrand(pn);
                        if (pn.Length > 30) pn = pn[..30];
                        rt += brand.Length > 0
                            ? $"\nFabricant : {brand}  |  S/N : {pn}"
                            : $"\nS/N : {pn}";
                    }

                    d["ram"] = rt;
                    foreach (var m in mods) m.Dispose();
                }
                else d["ram"] = "Indisponible";
            }
            catch { d["ram"] = "Indisponible"; }

            // ── GPU ──────────────────────────────────────────────────────────
            try
            {
                // WMI : noms + version de pilote (l'AdapterRAM WMI est plafonné à 4 Go → inutilisable)
                var wmiList = new List<(string Name, string Driver)>();
                using (var s = new ManagementObjectSearcher(
                    "SELECT Name, DriverVersion FROM Win32_VideoController"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        var name = o["Name"]?.ToString()?.Trim() ?? "";
                        if (name.Length > 0)
                            wmiList.Add((name, o["DriverVersion"]?.ToString()?.Trim() ?? ""));
                        o.Dispose();
                    }
                }

                // DXGI : VRAM réelle 64-bit (DedicatedVideoMemory)
                var dxgi = GpuInfo.GetAdapters()
                    .Where(a => !a.IsSoftware && IsDiscreteGpu(a.Name))
                    .ToList();

                string name2; long vram;
                if (dxgi.Count > 0)
                {
                    var best = dxgi.OrderByDescending(a => a.VramBytes).First();
                    name2 = best.Name;
                    vram  = best.VramBytes;
                }
                else
                {
                    // Fallback : aucun GPU discret via DXGI → meilleur DXGI tout court, sinon WMI + registre
                    var allDxgi = GpuInfo.GetAdapters().Where(a => !a.IsSoftware).ToList();
                    if (allDxgi.Count > 0)
                    {
                        var best = allDxgi.OrderByDescending(a => a.VramBytes).First();
                        name2 = best.Name;
                        vram  = best.VramBytes;
                    }
                    else
                    {
                        var disc = wmiList.Where(g => IsDiscreteGpu(g.Name)).ToList();
                        if (disc.Count == 0) disc = wmiList;
                        name2 = disc.Count > 0 ? disc[0].Name : "Indisponible";
                        vram  = GetVramFromRegistry(name2);
                    }
                }

                // Version du pilote : retrouver la ligne WMI correspondante
                var drv = "";
                foreach (var w in wmiList)
                {
                    if (w.Name.Equals(name2, StringComparison.OrdinalIgnoreCase) ||
                        name2.Contains(w.Name, StringComparison.OrdinalIgnoreCase) ||
                        w.Name.Contains(name2, StringComparison.OrdinalIgnoreCase))
                    { drv = w.Driver; break; }
                }

                var vramStr = vram > 0
                    ? $"  |  {Math.Round(vram / (double)(1L << 30), 0)} Go VRAM"
                    : "";
                var drvStr = drv.Length > 0 ? $"\nDriver : {drv}" : "";

                // Architecture déduite du nom (RTX 4xxx = Ada Lovelace, etc.)
                var gpuArch = DeriveGpuArch(name2);
                var archStr = gpuArch.Length > 0 ? $"\nArchitecture : {gpuArch}" : "";

                d["gpu"] = $"{name2}{vramStr}{drvStr}{archStr}";
            }
            catch { d["gpu"] = "Indisponible"; }

            // ── OS ───────────────────────────────────────────────────────────
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Caption, BuildNumber, OSArchitecture, LastBootUpTime, InstallDate FROM Win32_OperatingSystem");
                foreach (ManagementObject o in s.Get())
                {
                    var name  = (o["Caption"]?.ToString() ?? "").Replace("Microsoft ", "").Trim();
                    var build = o["BuildNumber"]?.ToString() ?? "";
                    var arch  = o["OSArchitecture"]?.ToString() ?? "";

                    var ubr = "";
                    try
                    {
                        var v = Registry.GetValue(
                            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                            "UBR", null);
                        if (v != null) ubr = $".{v}";
                    }
                    catch { }

                    // Uptime à partir du dernier démarrage
                    var uptimeStr = "";
                    try
                    {
                        var rawBoot = o["LastBootUpTime"]?.ToString() ?? "";
                        if (rawBoot.Length >= 14)
                        {
                            var boot   = ManagementDateTimeConverter.ToDateTime(rawBoot);
                            var up     = DateTime.Now - boot;
                            uptimeStr  = up.TotalDays >= 1
                                       ? $"{(int)up.TotalDays}j {up.Hours}h {up.Minutes}min"
                                       : up.TotalHours >= 1
                                       ? $"{up.Hours}h {up.Minutes}min"
                                       : $"{up.Minutes}min";
                        }
                    }
                    catch { }

                    var pcName  = Environment.MachineName;
                    var upLine  = uptimeStr.Length > 0 ? $"\nUptime : {uptimeStr}" : "";

                    // Date d'installation Windows (WMI InstallDate)
                    var installLine = "";
                    try
                    {
                        var rawInstall = o["InstallDate"]?.ToString() ?? "";
                        if (rawInstall.Length >= 14)
                        {
                            var inst = ManagementDateTimeConverter.ToDateTime(rawInstall);
                            installLine = $"\nInstallé le : {inst:dd/MM/yyyy}";
                        }
                    }
                    catch { }

                    d["os"] = $"{name}\nBuild {build}{ubr}  |  {arch}\nPC : {pcName}{upLine}{installLine}";
                    o.Dispose();
                    break;
                }
            }
            catch { d["os"] = "Indisponible"; }

            // ── Disques ──────────────────────────────────────────────────────
            try
            {
                var lines    = new List<string>();
                long totalB  = 0;
                using var s = new ManagementObjectSearcher(
                    "SELECT Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive");
                foreach (ManagementObject o in s.Get())
                {
                    var model = o["Model"]?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrEmpty(model)) { o.Dispose(); continue; }
                    long sizeB  = o["Size"] != null ? Convert.ToInt64(o["Size"].ToString()) : 0L;
                    totalB += sizeB;
                    var  sizeGb = sizeB > 0 ? $"  {Math.Round(sizeB / 1_000_000_000.0, 0)} Go" : "";
                    // Type déduit du modèle + interface
                    var iface   = o["InterfaceType"]?.ToString() ?? "";
                    var diskType = DeriveDiskType(model, iface);
                    var typeTag  = diskType.Length > 0 ? $"  [{diskType}]" : "";
                    lines.Add($"{model}{sizeGb}{typeTag}");
                    o.Dispose();
                }
                if (lines.Count > 0 && totalB > 0)
                {
                    var totalStr = totalB >= 1_000_000_000_000L
                        ? $"{Math.Round(totalB / 1_000_000_000_000.0, 2)} To"
                        : $"{Math.Round(totalB / 1_000_000_000.0, 0)} Go";
                    lines.Add($"Capacité totale : {totalStr}");
                }
                d["disk"] = lines.Count > 0 ? string.Join("\n", lines) : "Indisponible";
            }
            catch { d["disk"] = "Indisponible"; }

            // ── Carte mère + BIOS ────────────────────────────────────────────
            try
            {
                var mfr  = "";
                var prod = "";
                using var s = new ManagementObjectSearcher(
                    "SELECT Manufacturer, Product FROM Win32_BaseBoard");
                foreach (ManagementObject o in s.Get())
                {
                    mfr  = o["Manufacturer"]?.ToString()?.Trim() ?? "";
                    prod = o["Product"]?.ToString()?.Trim() ?? "";
                    o.Dispose();
                    break;
                }

                var biosLine = "";
                using var sb = new ManagementObjectSearcher(
                    "SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
                foreach (ManagementObject o in sb.Get())
                {
                    var ver = o["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                    var raw = o["ReleaseDate"]?.ToString() ?? "";
                    var date = raw.Length >= 8
                             ? $"{raw[6..8]}/{raw[4..6]}/{raw[..4]}" : raw;
                    if (ver.Length > 0)
                        biosLine = $"BIOS : {ver}  |  {date}";
                    o.Dispose();
                    break;
                }

                // Afficher : fabricant / modèle / BIOS sur lignes séparées
                var lines = new List<string>();
                if (mfr.Length  > 0) lines.Add(mfr);
                if (prod.Length > 0) lines.Add(prod);

                // Chipset extrait du nom du modèle + plateforme
                var chipset = DeriveChipset(prod);
                if (chipset.Length > 0)
                {
                    var platform = DetectMbPlatform(prod);
                    lines.Add($"Chipset : {chipset}  |  Plateforme : {platform}");
                }

                if (biosLine.Length > 0) lines.Add(biosLine);

                d["mb"]       = lines.Count > 0 ? string.Join("\n", lines) : "Indisponible";
                d["mb_mfr"]   = mfr;
                d["mb_model"] = prod;
            }
            catch { d["mb"] = "Indisponible"; d["mb_mfr"] = ""; d["mb_model"] = ""; }

            return d;
        }

        // ── Score d'optimisation : SUPPRIMÉ en v1.2.6 ─────────────────────────
        // Remplacé par le Tweakly Score (Pages/PageBenchmark, page d'accueil) qui mesure pour de
        // vrai (CPU SHA512 + jitter système + ping) au lieu d'agréger des bits de registre. Toutes
        // les méthodes liées (LoadScoreAsync, CalcScore, AnimateRing, SetRing, Lighten, ScoreLabel,
        // PerfPlanGuids, Dw, IsPerfPowerPlanActive, BtnRefreshScore_Click) ont été retirées.

        // ── Helpers de déduction (v1.3.0) ────────────────────────────────────
        // Ces fonctions enrichissent les specs avec des infos déduites des chaînes
        // produites par WMI. Pas d'I/O supplémentaire, juste du pattern matching.

        /// <summary>Architecture CPU déduite du nom commercial (Arrow Lake, Zen 5, etc.).</summary>
        private static string DeriveCpuArch(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var u = name.ToUpperInvariant();

            // INTEL
            if (u.Contains("CORE ULTRA"))
            {
                // Core Ultra 2xx (200-series) = Arrow Lake desktop / Lunar Lake mobile
                if (Regex.IsMatch(u, @"ULTRA\s+\d+\s+2\d{2}"))
                {
                    // Suffixe H/U = Lunar Lake, sinon (K/F/KF) = Arrow Lake desktop
                    if (Regex.IsMatch(u, @"2\d{2}[HU]")) return "Lunar Lake";
                    return "Arrow Lake";
                }
                // Core Ultra 1xx = Meteor Lake
                if (Regex.IsMatch(u, @"ULTRA\s+\d+\s+1\d{2}")) return "Meteor Lake";
            }
            if (Regex.IsMatch(u, @"\bI\d-15\d{3}"))  return "Arrow Lake";
            if (Regex.IsMatch(u, @"\bI\d-14\d{3}"))  return "Raptor Lake Refresh";
            if (Regex.IsMatch(u, @"\bI\d-13\d{3}"))  return "Raptor Lake";
            if (Regex.IsMatch(u, @"\bI\d-12\d{3}"))  return "Alder Lake";
            if (Regex.IsMatch(u, @"\bI\d-11\d{3}"))  return "Rocket Lake";
            if (Regex.IsMatch(u, @"\bI\d-10\d{3}"))  return "Comet Lake";
            if (Regex.IsMatch(u, @"\bI\d-9\d{3}"))   return "Coffee Lake Refresh";
            if (Regex.IsMatch(u, @"\bI\d-8\d{3}"))   return "Coffee Lake";

            // AMD RYZEN
            if (Regex.IsMatch(u, @"RYZEN\s+\d+\s+9\d{3}"))  return "Zen 5";
            if (Regex.IsMatch(u, @"RYZEN\s+\d+\s+7\d{3}"))  return "Zen 4";
            if (Regex.IsMatch(u, @"RYZEN\s+\d+\s+5\d{3}"))  return "Zen 3";
            if (Regex.IsMatch(u, @"RYZEN\s+\d+\s+3\d{3}"))  return "Zen 2";
            if (Regex.IsMatch(u, @"RYZEN\s+\d+\s+2\d{3}"))  return "Zen+";
            if (Regex.IsMatch(u, @"RYZEN\s+\d+\s+1\d{3}"))  return "Zen";

            return "";
        }

        /// <summary>
        /// Socket CPU déduit du nom commercial (LGA1851, LGA1700, AM5, AM4…). WMI ne donne
        /// qu'un code interne opaque (ex. "U3E1") — on le remplace par le vrai nom de socket
        /// que les utilisateurs connaissent (et qui apparaît sur les fiches constructeur).
        /// </summary>
        private static string DeriveCpuSocket(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var u = name.ToUpperInvariant();

            // INTEL
            // Core Ultra 2xx (Arrow Lake) = LGA1851
            if (Regex.IsMatch(u, @"ULTRA\s+\d+\s+2\d{2}"))                       return "LGA1851";
            // Core i 12xxx / 13xxx / 14xxx = LGA1700
            if (Regex.IsMatch(u, @"\bI\d-1[234]\d{3}"))                          return "LGA1700";
            // Core i 10xxx / 11xxx = LGA1200
            if (Regex.IsMatch(u, @"\bI\d-1[01]\d{3}"))                           return "LGA1200";
            // Core i 8xxx / 9xxx = LGA1151
            if (Regex.IsMatch(u, @"\bI\d-[89]\d{3}"))                            return "LGA1151";

            // AMD
            // Ryzen 9000 / 8000 / 7000 = AM5
            if (Regex.IsMatch(u, @"RYZEN\s+\d+\s+[789]\d{3}"))                   return "AM5";
            // Ryzen 5000 / 4000 / 3000 / 2000 / 1000 = AM4
            if (Regex.IsMatch(u, @"RYZEN\s+\d+\s+[12345]\d{3}"))                 return "AM4";
            // Threadripper = TR4 / sTRX4 / sTR5 (variable, on laisse vide)

            return "";
        }

        /// <summary>Architecture GPU déduite du nom (Ada Lovelace, RDNA 3, Blackwell, etc.).</summary>
        private static string DeriveGpuArch(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var u = name.ToUpperInvariant();

            // NVIDIA
            if (Regex.IsMatch(u, @"RTX\s*50\d{2}"))     return "Blackwell";
            if (Regex.IsMatch(u, @"RTX\s*40\d{2}"))     return "Ada Lovelace";
            if (Regex.IsMatch(u, @"RTX\s*30\d{2}"))     return "Ampere";
            if (Regex.IsMatch(u, @"RTX\s*20\d{2}"))     return "Turing";
            if (Regex.IsMatch(u, @"GTX\s*16\d{2}"))     return "Turing";
            if (Regex.IsMatch(u, @"GTX\s*10\d{2}"))     return "Pascal";
            if (Regex.IsMatch(u, @"GTX\s*9\d{2}"))      return "Maxwell";

            // AMD
            if (Regex.IsMatch(u, @"RX\s*9\d{3}"))       return "RDNA 4";
            if (Regex.IsMatch(u, @"RX\s*7\d{3}"))       return "RDNA 3";
            if (Regex.IsMatch(u, @"RX\s*6\d{3}"))       return "RDNA 2";
            if (Regex.IsMatch(u, @"RX\s*5\d{3}"))       return "RDNA";
            if (Regex.IsMatch(u, @"RADEON.*VEGA"))      return "Vega (GCN 5)";

            // Intel Arc
            if (u.Contains("ARC B"))                    return "Battlemage";
            if (u.Contains("ARC A"))                    return "Alchemist";

            return "";
        }

        /// <summary>Configuration des canaux RAM selon le nombre de modules installés.</summary>
        private static string DeriveRamChannels(int modules)
        {
            return modules switch
            {
                1 => "Single channel",
                2 => "Dual channel",
                3 => "Triple channel",
                4 => "Quad channel",
                _ => modules > 0 ? $"{modules} modules" : "",
            };
        }

        /// <summary>Marque de la RAM déduite du préfixe du PartNumber (F4/F5 = G.Skill, etc.).</summary>
        private static string DeriveRamBrand(string pn)
        {
            if (string.IsNullOrEmpty(pn)) return "";
            var u = pn.ToUpperInvariant().TrimStart();

            // G.Skill : F3/F4/F5
            if (Regex.IsMatch(u, @"^F[3-5]-"))           return "G.Skill";
            // Corsair Vengeance : CMK / CMW / CMH / CMT (Dominator)
            if (Regex.IsMatch(u, @"^CM[KWHT]"))          return "Corsair";
            // Kingston Fury : KF / HX
            if (Regex.IsMatch(u, @"^KF\d") || u.StartsWith("HX")) return "Kingston";
            // Crucial Ballistix : BL / CT
            if (u.StartsWith("BL") || u.StartsWith("CT")) return "Crucial";
            // Team Group / T-Force
            if (u.StartsWith("TFORCE") || u.StartsWith("T-FORCE") || u.StartsWith("TLZ") || u.StartsWith("TF")) return "Team Group";
            // ADATA : AD / AX
            if (u.StartsWith("AX") || u.StartsWith("AD")) return "ADATA / XPG";
            // Patriot : PV
            if (u.StartsWith("PV"))                       return "Patriot";

            return "";
        }

        /// <summary>Chipset extrait du nom de la carte mère (B860, X870E, Z790, etc.).</summary>
        private static string DeriveChipset(string model)
        {
            if (string.IsNullOrEmpty(model)) return "";
            // Intel : Z/H/B/Q + 3 chiffres (ex. Z790, B760, H770)
            var m = Regex.Match(model, @"\b([ZHBQ]\d{3})\b", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            // AMD : A/B/X + 3 chiffres + suffixe E optionnel (ex. X670E, B650, X870)
            m = Regex.Match(model, @"\b([ABX]\d{3}E?)\b", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            return "";
        }

        /// <summary>Type de disque (NVMe / SSD / HDD) déduit du modèle + interface.</summary>
        private static string DeriveDiskType(string model, string iface)
        {
            var u = model.ToUpperInvariant();
            if (u.Contains("NVME") || u.Contains("PCIE") || u.Contains("M.2"))  return "NVMe";
            // Patterns NVMe communs (Samsung 980/990, WD Black SN, Crucial P3/P5, Sabrent Rocket)
            if (Regex.IsMatch(u, @"\b(SN\d{3}|P[1-5]|9[5689]0|MP\d{3}|ROCKET)\b")) return "NVMe";
            if (u.Contains("SSD"))                                              return "SSD";
            if (iface == "SCSI" || u.Contains("HDD") || Regex.IsMatch(u, @"\b(WD|SEAGATE|HITACHI|TOSHIBA).*\b(BLUE|BLACK|RED|PURPLE|GOLD|BARRACUDA|IRONWOLF)\b"))
                return "HDD";
            return "";
        }

        // ── Helpers GPU ───────────────────────────────────────────────────────

        /// <summary>
        /// Retourne true si le GPU est un GPU discret (pas iGPU Intel/AMD, pas Parsec, pas virtuel).
        /// </summary>
        private static bool IsDiscreteGpu(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.ToUpperInvariant();

            // GPU virtuels / logiciels
            if (n.Contains("PARSEC"))                   return false;
            if (n.Contains("MICROSOFT BASIC DISPLAY"))  return false;
            if (n.Contains("VMWARE"))                   return false;
            if (n.Contains("VIRTUALBOX"))               return false;
            if (n.Contains("HYPER-V"))                  return false;
            if (n.Contains("INDIRECT"))                 return false;
            if (n.Contains("VIRTUAL"))                  return false;

            // iGPU Intel : tout sauf Intel Arc (discret)
            if (n.Contains("INTEL") && !n.Contains("ARC")) return false;

            // iGPU AMD : les noms WMI des iGPU contiennent "(TM)" après "RADEON"
            // ex. "AMD Radeon(TM) Graphics", "AMD Radeon(TM) Vega 8 Graphics", "AMD Radeon(TM) 680M"
            // les GPU discrets AMD n'ont pas "(TM)" : "AMD Radeon RX 7900 XTX"
            if (n.Contains("AMD") && n.Contains("(TM)") && !n.Contains("RADEON RX"))
                return false;

            return true;
        }

        /// <summary>
        /// Lit la VRAM réelle depuis le registre (contourne la limite UInt32 ~4 Go de WMI).
        /// </summary>
        private static long GetVramFromRegistry(string gpuName)
        {
            try
            {
                const string path =
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
                using var root = Registry.LocalMachine.OpenSubKey(path);
                if (root == null) return 0;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k == null) continue;
                    var desc = k.GetValue("DriverDesc")?.ToString()?.Trim() ?? "";
                    if (!string.Equals(desc, gpuName, StringComparison.OrdinalIgnoreCase)) continue;
                    // qwMemorySize = QWORD (Int64), valeur précise
                    var qw = k.GetValue("HardwareInformation.qwMemorySize");
                    if (qw != null && Convert.ToInt64(qw) > 0) return Convert.ToInt64(qw);
                    // Fallback : MemorySize (DWORD ou QWORD selon le pilote)
                    var dw = k.GetValue("HardwareInformation.MemorySize");
                    if (dw != null) return Convert.ToInt64(dw);
                }
            }
            catch { }
            return 0;
        }

        public static string DetectMbPlatform(string model)
        {
            // Chipsets AMD listés explicitement — plus fiable que le pattern générique
            var amdChipsets = new[]
            {
                "A320","B350","X370",          // AM4 300-series
                "B450","X470",                 // AM4 400-series
                "A520","B550","X570",          // AM4 500-series
                "A620","B650","B650E",          // AM5 600-series
                "X650","X670","X670E",          // AM5 600-series high-end
                "A720","X870","X870E","B840",   // AM5 700/800-series
            };
            var mu = model.ToUpperInvariant();
            foreach (var c in amdChipsets)
                if (Regex.IsMatch(mu, $@"\b{Regex.Escape(c)}\b"))
                    return "AMD";

            return "Intel"; // Z/H/B 4xx–9xx non listés ci-dessus = Intel
        }
    }
}
