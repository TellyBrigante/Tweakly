using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class PageSpecs : UserControl
    {
        private readonly MainWindow _main;
        private bool   _loaded   = false;
        private string _biosMfr  = "";
        private string _biosModel = "";

        public PageSpecs(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        // ── Chargement ────────────────────────────────────────────────────────

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded) return;
            _loaded = true;

            // Matériel + score en parallèle au premier affichage
            await Task.WhenAll(LoadHardwareAsync(), LoadScoreAsync());
        }

        // ── Matériel ──────────────────────────────────────────────────────────

        private async Task LoadHardwareAsync()
        {
            var d = await Task.Run(CollectHardware);

            TxtCPU.Text  = d.GetValueOrDefault("cpu",  "Erreur");
            TxtRAM.Text  = d.GetValueOrDefault("ram",  "Erreur");
            TxtGPU.Text  = d.GetValueOrDefault("gpu",  "Erreur");
            TxtOS.Text   = d.GetValueOrDefault("os",   "Erreur");
            TxtDisk.Text = d.GetValueOrDefault("disk", "Erreur");
            TxtMB.Text   = d.GetValueOrDefault("mb",   "Erreur");

            // Stocker fabricant + modèle pour le bouton BIOS
            _biosMfr   = d.GetValueOrDefault("mb_mfr",   "");
            _biosModel = d.GetValueOrDefault("mb_model",  "");
            BtnBios.IsEnabled = _biosModel.Length > 0;

            // Teinte les valeurs chargées
            foreach (var tb in new[] { TxtCPU, TxtRAM, TxtGPU, TxtOS, TxtDisk, TxtMB })
                tb.ClearValue(TextBlock.ForegroundProperty);

            _main.Log("Informations matérielles chargées.");
        }

        private void BtnBios_Click(object sender, RoutedEventArgs e)
        {
            var url = BuildBiosUrl();
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { _main.Log($"BIOS URL : erreur — {ex.Message}"); }
        }

        private string BuildBiosUrl()
        {
            var mfr   = _biosMfr.Trim();
            var model = _biosModel.Trim();
            var mfrUp = mfr.ToUpperInvariant();

            // ── MSI / Micro-Star ─────────────────────────────────────────────
            // Page BIOS directe : msi.com/Motherboard/MAG-Z790-TOMAHAWK-WIFI/support#bios
            if (mfrUp.Contains("MSI") || mfrUp.Contains("MICRO-STAR"))
                return $"https://www.msi.com/Motherboard/{MakeSlug(model)}/support#bios";

            // ── Gigabyte / AORUS ─────────────────────────────────────────────
            // Page BIOS directe : gigabyte.com/{pays}/Motherboard/Z790-AORUS-MASTER/support#bios
            if (mfrUp.Contains("GIGABYTE"))
            {
                var cc = GetCountryCode("GIGABYTE");
                var cp = cc.Length > 0 ? $"{cc}/" : "";
                return $"https://www.gigabyte.com/{cp}Motherboard/{MakeSlug(model)}/support#bios";
            }

            // ── ASUS / ASUSTeK ───────────────────────────────────────────────
            // URL directe produit : asus.com/{pays}/motherboards-components/motherboards/{line}/{SLUG}/HelpDesk_BIOS/
            if (mfrUp.Contains("ASUS") || mfrUp.Contains("ASUSTEK"))
            {
                var slug = MakeSlug(model).ToUpperInvariant();
                var line = GetAsusProductLine(model);
                var cc   = GetCountryCode("ASUS");
                var cp   = cc.Length > 0 ? $"/{cc}" : "";
                return $"https://www.asus.com{cp}/motherboards-components/motherboards/{line}/{slug}/HelpDesk_BIOS/";
            }

            // ── ASRock ───────────────────────────────────────────────────────
            // URL directe : asrock.com/mb/Intel/Z790%20Taichi/index.asp#BIOS
            // La plateforme (Intel/AMD) se déduit du chipset présent dans le modèle
            if (mfrUp.Contains("ASROCK"))
            {
                var platform = DetectMbPlatform(model);
                return $"https://www.asrock.com/mb/{platform}/{Uri.EscapeDataString(model)}/index.asp#BIOS";
            }

            // ── Biostar ──────────────────────────────────────────────────────
            if (mfrUp.Contains("BIOSTAR"))
                return $"https://www.biostar.com.tw/app/en/mb/introduction.php?S_ID={Uri.EscapeDataString(model)}";

            // ── Supermicro ───────────────────────────────────────────────────
            if (mfrUp.Contains("SUPERMICRO"))
                return $"https://www.supermicro.com/en/support/resources/downloadcenter/firmware?q={Uri.EscapeDataString(model)}";

            // ── Fallback universel ───────────────────────────────────────────
            return "https://www.google.com/search?q="
                 + Uri.EscapeDataString($"{mfr} {model} BIOS update download");
        }

        /// <summary>
        /// Retourne le segment pays à insérer dans l'URL du fabricant,
        /// basé sur les paramètres régionaux Windows de l'utilisateur (RegionInfo).
        /// Retourne "" si le pays n'est pas reconnu (→ URL globale sans pays).
        /// </summary>
        private static string GetCountryCode(string brand)
        {
            string iso;
            try
            {
                iso = System.Globalization.RegionInfo.CurrentRegion
                            .TwoLetterISORegionName.ToUpperInvariant();
            }
            catch { return ""; }

            var bUp = brand.ToUpperInvariant();

            // ── ASUS ─────────────────────────────────────────────────────────
            // asus.com/{pays}/... — codes spéciaux : GB→uk, CA→ca-en, Golfe→me
            if (bUp.Contains("ASUS"))
            {
                // Pays avec segment régional sur asus.com (whitelist)
                var asusMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // Europe
                    {"FR","fr"}, {"DE","de"}, {"IT","it"}, {"ES","es"},
                    {"GB","uk"}, {"NL","nl"}, {"BE","be"}, {"PL","pl"},
                    {"PT","pt"}, {"SE","se"}, {"NO","no"}, {"DK","dk"},
                    {"FI","fi"}, {"AT","at"}, {"CH","ch"}, {"CZ","cz"},
                    {"SK","sk"}, {"HU","hu"}, {"RO","ro"}, {"BG","bg"},
                    {"GR","gr"}, {"HR","hr"}, {"RS","rs"}, {"SI","si"},
                    {"TR","tr"}, {"UA","ua"}, {"RU","ru"}, {"IL","il"},
                    // Amériques
                    {"US","us"}, {"CA","ca-en"}, {"MX","mx"}, {"BR","br"},
                    // Asie-Pacifique
                    {"AU","au"}, {"NZ","nz"}, {"JP","jp"}, {"KR","kr"},
                    {"CN","cn"}, {"TW","tw"}, {"HK","hk"}, {"SG","sg"},
                    {"MY","my"}, {"TH","th"}, {"ID","id"}, {"PH","ph"},
                    {"VN","vn"}, {"IN","in"},
                    // Moyen-Orient / Afrique
                    {"ZA","za"},
                    {"AE","me"}, {"SA","me"}, {"KW","me"},
                    {"QA","me"}, {"BH","me"}, {"OM","me"},
                };
                return asusMap.TryGetValue(iso, out var ac) ? ac : "";
            }

            // ── Gigabyte ─────────────────────────────────────────────────────
            // gigabyte.com/{pays}/Motherboard/... — codes ISO standard (gb, fr, de…)
            if (bUp.Contains("GIGABYTE"))
            {
                var gigaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // Europe
                    {"FR","fr"}, {"DE","de"}, {"IT","it"}, {"ES","es"},
                    {"GB","gb"}, {"NL","nl"}, {"BE","be"}, {"PL","pl"},
                    {"PT","pt"}, {"SE","se"}, {"NO","no"}, {"DK","dk"},
                    {"FI","fi"}, {"AT","at"}, {"CH","ch"}, {"CZ","cz"},
                    {"SK","sk"}, {"HU","hu"}, {"RO","ro"}, {"BG","bg"},
                    {"GR","gr"}, {"HR","hr"}, {"RS","rs"}, {"TR","tr"},
                    {"UA","ua"}, {"RU","ru"}, {"IL","il"},
                    // Amériques
                    {"US","us"}, {"CA","ca"}, {"MX","mx"}, {"BR","br"},
                    // Asie-Pacifique
                    {"AU","au"}, {"NZ","nz"}, {"JP","jp"}, {"KR","kr"},
                    {"CN","cn"}, {"TW","tw"}, {"HK","hk"}, {"SG","sg"},
                    {"MY","my"}, {"TH","th"}, {"ID","id"}, {"PH","ph"},
                    {"VN","vn"}, {"IN","in"},
                    // Moyen-Orient / Afrique
                    {"ZA","za"}, {"AE","ae"}, {"SA","sa"},
                };
                return gigaMap.TryGetValue(iso, out var gc) ? gc : "";
            }

            // MSI et ASRock : pages produit identiques dans le monde entier — pas de segment pays
            return "";
        }

        /// <summary>
        /// Espaces → tirets, parenthèses retirées.
        /// "ROG STRIX Z790-F GAMING WIFI" → "ROG-STRIX-Z790-F-GAMING-WIFI"
        /// </summary>
        private static string MakeSlug(string model)
        {
            var s = Regex.Replace(model, @"\s+", "-");
            return Regex.Replace(s, @"[()\\\/]", "").Trim('-');
        }

        /// <summary>
        /// Détermine la ligne de produit ASUS à partir du préfixe du modèle.
        /// Utilisé pour construire l'URL ASUS correcte (segment {line} dans le chemin).
        /// </summary>
        private static string GetAsusProductLine(string model)
        {
            var mu = model.ToUpperInvariant();

            // ROG = Republic of Gamers : préfixes ROG, STRIX (sans ROG), MAXIMUS, CROSSHAIR, APEX, FORMULA
            if (mu.StartsWith("ROG")        ||
                mu.StartsWith("STRIX")      ||
                mu.Contains("MAXIMUS")      ||
                mu.Contains("CROSSHAIR")    ||
                mu.Contains("APEX")         ||
                mu.Contains("FORMULA"))
                return "rog";

            // TUF Gaming
            if (mu.StartsWith("TUF"))       return "tuf-gaming";

            // PRIME
            if (mu.StartsWith("PRIME"))     return "prime";

            // ProArt
            if (mu.StartsWith("PROART"))    return "proart";

            // Expert Boards (workstation)
            if (mu.StartsWith("PRO WS") || mu.StartsWith("PROWS")) return "expert-boards";

            // Fallback : catégorie générique sur laquelle ASUS redirige correctement
            return "all-series";
        }

        /// <summary>
        /// Détermine la plateforme CPU (Intel / AMD) à partir du chipset dans le nom du modèle.
        /// Utilisé pour les URLs ASRock qui incluent la plateforme dans le chemin.
        /// </summary>
        private static string DetectMbPlatform(string model)
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

        private static Dictionary<string, string> CollectHardware()
        {
            var d = new Dictionary<string, string>();

            // ── CPU ──────────────────────────────────────────────────────────
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, ThreadCount, NumberOfLogicalProcessors, MaxClockSpeed, L3CacheSize FROM Win32_Processor");
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
                    var l3kb    = o["L3CacheSize"] != null ? Convert.ToDouble(o["L3CacheSize"]) : 0;
                    var cache   = l3kb > 0 ? $"  |  L3 : {Math.Round(l3kb / 1024.0, 0)} Mo" : "";

                    d["cpu"] = $"{name}\n{cores}C / {threads}T{cache}\nFréq. max : {freq}";
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

                    // Part number (S/N)
                    var pn = first["PartNumber"]?.ToString()?.Trim() ?? "";
                    pn = Regex.Replace(pn, @"\s{2,}", " ");
                    if (pn.Length > 1 && pn != "Array Handle")
                    {
                        if (pn.Length > 30) pn = pn[..30];
                        rt += $"\nS/N : {pn}";
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

                d["gpu"] = $"{name2}{vramStr}{drvStr}";
            }
            catch { d["gpu"] = "Indisponible"; }

            // ── OS ───────────────────────────────────────────────────────────
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Caption, BuildNumber, OSArchitecture, LastBootUpTime FROM Win32_OperatingSystem");
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

                    d["os"] = $"{name}\nBuild {build}{ubr}  |  {arch}\nPC : {pcName}{upLine}";
                    o.Dispose();
                    break;
                }
            }
            catch { d["os"] = "Indisponible"; }

            // ── Disques ──────────────────────────────────────────────────────
            try
            {
                var lines = new List<string>();
                using var s = new ManagementObjectSearcher(
                    "SELECT Model, Size FROM Win32_DiskDrive");
                foreach (ManagementObject o in s.Get())
                {
                    var model = o["Model"]?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrEmpty(model)) { o.Dispose(); continue; }
                    long sizeB  = o["Size"] != null ? Convert.ToInt64(o["Size"].ToString()) : 0L;
                    var  sizeGb = sizeB > 0 ? $"  {Math.Round(sizeB / 1_000_000_000.0, 0)} Go" : "";
                    lines.Add($"{model}{sizeGb}");
                    o.Dispose();
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
                if (biosLine.Length > 0) lines.Add(biosLine);

                d["mb"]       = lines.Count > 0 ? string.Join("\n", lines) : "Indisponible";
                d["mb_mfr"]   = mfr;
                d["mb_model"] = prod;
            }
            catch { d["mb"] = "Indisponible"; d["mb_mfr"] = ""; d["mb_model"] = ""; }

            return d;
        }

        // ── Score ─────────────────────────────────────────────────────────────

        private async Task LoadScoreAsync()
        {
            TxtScoreStatus.Text = "Calcul en cours…";

            var (sys, wmt) = await Task.Run(CalcScore);
            int total = sys + wmt;

            TxtScoreTotal.Text  = total.ToString();
            TxtSysVal.Text      = $"{sys} / 70";
            TxtWmtVal.Text      = $"{wmt} / 30";
            TxtScoreStatus.Text = ScoreLabel(total);

            var scoreColor = total switch
            {
                >= 80 => Color.FromRgb(0x2E, 0xC4, 0x6A),  // vert
                >= 60 => Color.FromRgb(0x3B, 0x82, 0xE0),  // bleu
                >= 40 => Color.FromRgb(0xE0, 0x9A, 0x28),  // orange
                _     => Color.FromRgb(0xE0, 0x4B, 0x3C)   // rouge
            };
            var lighter = Lighten(scoreColor, 40);

            // Barre dégradée
            FillStop0.Color = scoreColor;
            FillStop1.Color = lighter;

            var pct = Math.Max(0.0, Math.Min(100.0, (double)total));
            ScoreProgressBar.ColumnDefinitions[0].Width = new GridLength(pct,       GridUnitType.Star);
            ScoreProgressBar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);

            // Anneau circulaire (jauge donut)
            RingFill.Stroke = new SolidColorBrush(lighter);
            RingGlow.Color   = scoreColor;
            RingGlow.Opacity = 0.6;
            SetRing(pct);
        }

        /// <summary>Pilote le remplissage de l'anneau via StrokeDashArray (0–100 %).</summary>
        private void SetRing(double pct)
        {
            const double size = 94.0, thick = 9.0;          // Ellipse 94px (grid 104 - marge 5), trait 9
            double r     = (size - thick) / 2.0;
            double circ  = 2 * Math.PI * r;
            double units = circ / thick;                    // longueur totale en multiples du trait
            double on    = units * (Math.Max(0, Math.Min(100, pct)) / 100.0);
            RingFill.StrokeDashArray = new DoubleCollection { on, 1000 };
        }

        private static Color Lighten(Color c, int amt) => Color.FromRgb(
            (byte)Math.Min(255, c.R + amt),
            (byte)Math.Min(255, c.G + amt),
            (byte)Math.Min(255, c.B + amt));

        private static string ScoreLabel(int score) => score switch
        {
            >= 85 => "Excellent — Système bien optimisé",
            >= 65 => "Bon — Quelques améliorations possibles",
            >= 40 => "Moyen — Optimisations recommandées",
            _     => "Faible — Optimisations nécessaires"
        };

        // ── Calcul du score (50 pts Système + 50 pts Tweaks WMT) ─────────────

        // GUIDs des plans d'alimentation haute performance (locale-indépendants)
        private static readonly string[] PerfPlanGuids =
        {
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",   // Haute performance
            "e9a42b02-d5df-448d-aa00-03f14749eb61",   // Performances ultra
        };

        // Lecture DWORD registre, null si absent/erreur
        private static int? Dw(string path, string name)
        {
            try { var v = Registry.GetValue(path, name, null); return v == null ? null : Convert.ToInt32(v); }
            catch { return null; }
        }

        private static bool IsPerfPowerPlanActive()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                });
                if (p == null) return false;
                var output = p.StandardOutput.ReadToEnd().ToLowerInvariant();
                p.WaitForExit(3000);
                return PerfPlanGuids.Any(g => output.Contains(g))
                    || output.Contains("ultim") || output.Contains("haute performance")
                    || output.Contains("hautes performances");
            }
            catch { return false; }
        }

        // Retourne (Performance /70, Confidentialité /30) — pondéré par impact RÉEL.
        private static (int sys, int wmt) CalcScore()
        {
            int perf = 0, priv = 0;

            // ═══════════ PERFORMANCE (70 pts) — impact mesurable ════════════════

            // HVCI / Memory Integrity désactivé — 16  (overhead virtualisation, 5-10% jeux)
            var hvci = Dw(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
            if (hvci == null || hvci == 0) perf += 16;

            // Plan alimentation Haute perf / Ultimate — 16  (empêche le downclock CPU)
            if (IsPerfPowerPlanActive()) perf += 16;

            // Power Throttling désactivé — 12  (pas de bridage CPU en arrière-plan)
            if (Dw(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff") == 1) perf += 12;

            // HAGS — 9  (variable selon GPU, mais réel sur frame pacing)
            var hags = Dw(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
            if (hags == 2 || (hags == null && Environment.OSVersion.Version.Build >= 22621)) perf += 9;

            // Nagle désactivé — 7  (latence online / ping compétitif)
            if (Dw(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpAckFrequency") == 1) perf += 7;

            // System Responsiveness = 0 — 4  (marginal, réserve multimédia)
            if (Dw(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness") == 0) perf += 4;

            // GPU Priority MMCSS >= 8 — 3  (marginal)
            var gpuPrio = Dw(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority");
            if (gpuPrio != null && gpuPrio >= 8) perf += 3;

            // DVR en arrière-plan désactivé — 3  (overhead capture si actif)
            if (Dw(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled") == 0) perf += 3;

            // ═══════════ CONFIDENTIALITÉ (30 pts) — impact perf ~nul, vie privée ═

            // Télémétrie (AllowTelemetry=0) — 8
            if (Dw(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry") == 0) priv += 8;

            // Identifiant publicitaire (AdvertisingInfo\Enabled=0) — 4
            if (Dw(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled") == 0) priv += 4;

            // Localisation désactivée (DisableLocation=1) — 3
            if (Dw(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation") == 1) priv += 3;

            // Rapport d'erreurs Windows (WER Disabled=1) — 3
            if (Dw(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled") == 1) priv += 3;

            // Historique d'activité (EnableActivityFeed=0) — 3
            if (Dw(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed") == 0) priv += 3;

            // Bing Search désactivé (BingSearchEnabled=0) — 3
            if (Dw(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled") == 0) priv += 3;

            // Télémétrie applications (AppCompat\DisableInventory=1) — 3
            if (Dw(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisableInventory") == 1) priv += 3;

            // Game Bar désactivée (GameDVR\AppCaptureEnabled=0) — 3
            if (Dw(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled") == 0) priv += 3;

            return (Math.Min(perf, 70), Math.Min(priv, 30));
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

        private async void BtnRefreshScore_Click(object sender, RoutedEventArgs e)
        {
            BtnRefreshScore.IsEnabled = false;
            TxtScoreTotal.Text  = "—";
            TxtSysVal.Text      = "— / 70";
            TxtWmtVal.Text      = "— / 30";
            TxtScoreStatus.Text = "Calcul en cours…";
            RingGlow.Opacity    = 0;
            SetRing(0);
            ScoreProgressBar.ColumnDefinitions[0].Width = new GridLength(0,   GridUnitType.Star);
            ScoreProgressBar.ColumnDefinitions[1].Width = new GridLength(100, GridUnitType.Star);
            await LoadScoreAsync();
            BtnRefreshScore.IsEnabled = true;
        }
    }
}
