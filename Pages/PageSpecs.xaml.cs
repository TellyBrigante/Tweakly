using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    // Ligne d'un disque NVMe dans l'onglet Compatibilité
    public sealed class NvmeItem
    {
        public string Name      { get; set; } = "";
        public string Info      { get; set; } = "";
        public string Badge     { get; set; } = "";
        public Brush  BadgeColor { get; set; } = Brushes.Gray;
    }

    public partial class PageSpecs : UserControl
    {
        private readonly MainWindow _main;
        private bool   _loaded   = false;
        private bool   _compatLoaded = false;
        private string _biosMfr  = "";
        private string _biosModel = "";

        public PageSpecs(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        // ── Sous-onglets Spécifications / Compatibilité ─────────────────────────

        private void BtnTabSpecs_Click(object sender, RoutedEventArgs e)
        {
            PanelSpecs.Visibility  = Visibility.Visible;
            PanelCompat.Visibility = Visibility.Collapsed;
            StyleTab(BtnTabSpecs, true);
            StyleTab(BtnTabCompat, false);
        }

        private void BtnTabCompat_Click(object sender, RoutedEventArgs e)
        {
            PanelSpecs.Visibility  = Visibility.Collapsed;
            PanelCompat.Visibility = Visibility.Visible;
            StyleTab(BtnTabSpecs, false);
            StyleTab(BtnTabCompat, true);
            if (!_compatLoaded) { _compatLoaded = true; _ = LoadCompatAsync(); }
        }

        private static void StyleTab(Button btn, bool active)
        {
            if (btn.Template.FindName("Bg",  btn) is not Border bg)     return;
            if (btn.Template.FindName("Lbl", btn) is not TextBlock lbl) return;
            bg.Background  = active ? new SolidColorBrush(Color.FromRgb(0x25, 0x4E, 0x8C))
                                    : new SolidColorBrush(Colors.Transparent);
            lbl.Foreground = active ? new SolidColorBrush(Colors.White)
                                    : ThemeManager.Brush("ThTextDim");
        }

        // ── Chargement ────────────────────────────────────────────────────────

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded) return;
            _loaded = true;

            StyleTab(BtnTabSpecs, true);   // onglet Spécifications actif par défaut

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

            Anim.CountUp(TxtScoreTotal, total, 800);
            Anim.CountUp(TxtSysVal, sys, 800, " / 70");
            Anim.CountUp(TxtWmtVal, wmt, 800, " / 30");
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

            // Barre dégradée — glisse de 0 jusqu'au score
            var dur = new Duration(TimeSpan.FromMilliseconds(800));
            ScoreProgressBar.ColumnDefinitions[0].BeginAnimation(ColumnDefinition.WidthProperty,
                new GridLengthAnimation { From = 0, To = pct, Duration = dur });
            ScoreProgressBar.ColumnDefinitions[1].BeginAnimation(ColumnDefinition.WidthProperty,
                new GridLengthAnimation { From = 100, To = 100 - pct, Duration = dur });

            // Anneau circulaire (jauge donut) — remplissage animé
            RingFill.Stroke = new SolidColorBrush(lighter);
            RingGlow.Color   = scoreColor;
            RingGlow.Opacity = 0.6;
            AnimateRing(pct, 800);
        }

        /// <summary>Remplit l'anneau de 0 % jusqu'à la cible (easeOut).</summary>
        private void AnimateRing(double targetPct, int ms)
        {
            var sw = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                double t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / ms);
                double e = 1 - Math.Pow(1 - t, 3);
                SetRing(targetPct * e);
                if (t >= 1.0) { SetRing(targetPct); timer.Stop(); }
            };
            timer.Start();
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

        // ── Compatibilité PCIe / M.2 ────────────────────────────────────────────

        private static readonly Dictionary<int, string> GenName = new()
            { {1,"PCIe 1.0"}, {2,"PCIe 2.0"}, {3,"PCIe 3.0"}, {4,"PCIe 4.0"}, {5,"PCIe 5.0"} };
        private static readonly Dictionary<int, double> BwLane = new()
            { {1,0.25}, {2,0.50}, {3,0.985}, {4,1.969}, {5,3.938} };  // Go/s par voie

        // Table de specs constructeur des SSD NVMe courants (regex → gen, voies)
        private static readonly (string pat, int gen, int w)[] NvmeLut =
        {
            ("990 ?PRO",4,4), ("980 ?PRO",4,4), ("970 ?EVO ?Plus",3,4), ("970 ?EVO",3,4),
            ("970 ?PRO",3,4), ("Samsung.*980",3,4),
            ("SN850X",4,4), ("SN850",4,4), ("SN770",4,4), ("SN750",3,4), ("SN570",3,4), ("SN530",3,4),
            ("CS3030",3,4), ("CS2230",3,4), ("CS1030",3,4),
            ("FireCuda 530",4,4), ("FireCuda 520",4,4), ("FireCuda 510",3,4),
            ("T700",5,4), ("T500",4,4), ("P5 ?Plus",4,4), ("P3 ?Plus",4,4), ("P5",3,4), ("P3",3,4), ("P2",3,4),
            ("Platinum P41",4,4), ("Gold P31",3,4), ("P41",4,4), ("P31",3,4),
            ("S70",4,4), ("S50 ?Lite",4,4), ("S50",4,4), ("S40G",3,4),
            ("Rocket 4 ?Plus",4,4), ("Rocket 4",4,4), ("Rocket ?Plus",4,4), ("Rocket",3,4),
            ("MP700",5,4), ("MP600",4,4), ("MP510",3,4),
            ("Kioxia",3,4), ("Lexar NM790",4,4), ("Lexar",4,4), ("VP4300",4,4), ("US70",4,4),
        };

        private async Task LoadCompatAsync()
        {
            // ── GPU PCIe (nvidia-smi) ──
            var g = await Task.Run(GetGpuPcie);
            TxtCompatGpuName.Text = string.IsNullOrEmpty(g.name) ? "Aucune carte graphique détectée" : g.name;

            if (g.maxGen > 0 && g.curGen > 0)
            {
                double maxBw = Math.Round(BwLane.GetValueOrDefault(g.maxGen) * g.maxWidth, 1);
                double curBw = Math.Round(BwLane.GetValueOrDefault(g.curGen) * g.curWidth, 1);
                double pct   = maxBw > 0 ? Math.Min(100, curBw / maxBw * 100) : 0;

                TxtCompatGpuGen.Text = $"Actuel : {GenName[g.curGen]} x{g.curWidth}     •     GPU max : {GenName[g.maxGen]} x{g.maxWidth}";
                TxtCompatGpuBw.Text  = $"Bande passante : {curBw} Go/s  /  max théorique {maxBw} Go/s";
                SetBarPct(CompatGpuBar, pct);

                Color col; string status;
                if (g.curGen == g.maxGen && g.curWidth == g.maxWidth)
                {
                    col = Color.FromRgb(0x2E, 0xC4, 0x6A);
                    status = $"OPTIMAL — le GPU tourne à pleine vitesse ({pct:F0} %)";
                }
                else if (g.curGen < g.maxGen)
                {
                    col = Color.FromRgb(0xE0, 0x9A, 0x28);
                    status = $"SLOT LIMITÉ — actuel {GenName[g.curGen]} x{g.curWidth}, le GPU supporte {GenName[g.maxGen]} x{g.maxWidth} ({pct:F0} %)";
                }
                else
                {
                    col = Color.FromRgb(0xE0, 0x9A, 0x28);
                    status = $"VOIES RÉDUITES — x{g.curWidth} actuel / max x{g.maxWidth} ({pct:F0} %)";
                }
                CompatGpuFill.Background = new SolidColorBrush(col);
                TxtCompatGpuStatus.Foreground = new SolidColorBrush(col);
                TxtCompatGpuStatus.Text = status;
            }
            else
            {
                TxtCompatGpuGen.Text = "Données PCIe indisponibles (GPU NVIDIA + pilotes requis).";
                TxtCompatGpuBw.Text  = "";
                TxtCompatGpuStatus.Text = "";
                SetBarPct(CompatGpuBar, 0);
            }

            // ── NVMe (WMI + table de specs) ──
            var disks = await Task.Run(GetNvmeCompat);
            if (disks.Count == 0)
            {
                TxtCompatNvmeEmpty.Text = "Aucun disque NVMe détecté.";
                TxtCompatNvmeEmpty.Visibility = Visibility.Visible;
            }
            else
            {
                TxtCompatNvmeEmpty.Visibility = Visibility.Collapsed;
                CompatNvmeList.ItemsSource = disks;
            }
        }

        private static void SetBarPct(Grid bar, double pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            bar.ColumnDefinitions[0].Width = new GridLength(pct,       GridUnitType.Star);
            bar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
        }

        // GPU PCIe via nvidia-smi (name + gen/width max & current)
        private static (string name, int maxGen, int curGen, int maxWidth, int curWidth) GetGpuPcie()
        {
            try
            {
                foreach (var genField in new[] { "pcie.link.gen.gpumax", "pcie.link.gen.max" })
                {
                    var args = $"--query-gpu=name,{genField},pcie.link.gen.current,pcie.link.width.max,pcie.link.width.current " +
                               "--format=csv,noheader,nounits";
                    using var p = Process.Start(new ProcessStartInfo("nvidia-smi", args)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    });
                    if (p == null) continue;
                    var line = p.StandardOutput.ReadLine();
                    p.WaitForExit(4000);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(',');
                    if (parts.Length < 5) continue;
                    int I(string v) => int.TryParse(v.Trim(), out var n) ? n : 0;
                    var name = parts[0].Trim();
                    var maxGen = I(parts[1]); var curGen = I(parts[2]);
                    var maxW = I(parts[3]);   var curW = I(parts[4]);
                    if (maxGen > 0 && curGen > 0)
                        return (name, maxGen, curGen, maxW, curW);
                    // nom récupéré mais gen=0 → tenter l'autre champ
                    if (genField == "pcie.link.gen.max") return (name, 0, 0, 0, 0);
                }
            }
            catch { }
            return ("", 0, 0, 0, 0);
        }

        // Brush figé → utilisable depuis un thread de fond (cross-thread safe)
        private static Brush FrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // Disques NVMe + correspondance table de specs
        private static List<NvmeItem> GetNvmeCompat()
        {
            var list  = new List<NvmeItem>();
            var names = new List<string>();
            try
            {
                using var q = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage",
                    "SELECT FriendlyName, BusType FROM MSFT_PhysicalDisk");
                foreach (ManagementObject o in q.Get())
                {
                    // BusType 17 = NVMe
                    if (Convert.ToInt32(o["BusType"] ?? 0) == 17)
                    {
                        var n = o["FriendlyName"]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(n)) names.Add(n);
                    }
                    o.Dispose();
                }
            }
            catch { }

            // Fallback : Win32_DiskDrive si MSFT_PhysicalDisk indispo
            if (names.Count == 0)
            {
                try
                {
                    using var q = new ManagementObjectSearcher("SELECT Model FROM Win32_DiskDrive");
                    foreach (ManagementObject o in q.Get())
                    {
                        var m = o["Model"]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(m) && Regex.IsMatch(m, "NVMe|NVM", RegexOptions.IgnoreCase))
                            names.Add(m);
                        o.Dispose();
                    }
                }
                catch { }
            }

            foreach (var name in names.Take(4))
            {
                var item = new NvmeItem { Name = name };
                var spec = NvmeLut.FirstOrDefault(e => Regex.IsMatch(name, e.pat, RegexOptions.IgnoreCase));
                if (spec.gen > 0)
                {
                    double bw = Math.Round(BwLane.GetValueOrDefault(spec.gen) * spec.w, 1);
                    item.Info  = $"Spec constructeur : {GenName[spec.gen]} x{spec.w}  ({bw} Go/s théorique)";
                    item.Badge = $"{GenName[spec.gen]} x{spec.w}";
                    item.BadgeColor = FrozenBrush(spec.gen switch
                    {
                        5 => Color.FromRgb(0xFF, 0xC8, 0x00),
                        4 => Color.FromRgb(0x2E, 0xC4, 0x6A),
                        3 => Color.FromRgb(0x3B, 0x82, 0xE0),
                        _ => Color.FromRgb(0x9C, 0xA3, 0xCC),
                    });
                }
                else
                {
                    item.Info  = "Modèle non répertorié — consulter les specs du fabricant";
                    item.Badge = "?";
                    item.BadgeColor = FrozenBrush(Color.FromRgb(0x9C, 0xA3, 0xCC));
                }
                list.Add(item);
            }
            return list;
        }
    }
}
