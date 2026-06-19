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
            bg.Background  = active ? ThemeManager.Brush("ThTabSel")
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

            // Le score d'optimisation a été retiré : le Tweakly Score (page d'accueil) couvre ce rôle.
            await LoadHardwareAsync();
        }

        // ── Matériel ──────────────────────────────────────────────────────────

        private async Task LoadHardwareAsync()
        {
            var d = await Task.Run(Helpers.HardwareInfo.Collect);

            // Compat ascendante : les TxtXxx historiques restent populés au cas où d'autres
            // bouts de code y accèdent (ils contiennent le bloc texte complet).
            TxtCPU.Text  = d.GetValueOrDefault("cpu",  "Erreur");
            TxtRAM.Text  = d.GetValueOrDefault("ram",  "Erreur");
            TxtGPU.Text  = d.GetValueOrDefault("gpu",  "Erreur");
            TxtOS.Text   = d.GetValueOrDefault("os",   "Erreur");
            TxtDisk.Text = d.GetValueOrDefault("disk", "Erreur");
            TxtMB.Text   = d.GetValueOrDefault("mb",   "Erreur");

            // ── Refonte Bento 2026 : on extrait HEADLINE (gros) + DETAILS (petit)
            // pour chaque catégorie, afin d'avoir une vraie hiérarchie typographique.
            // On parse les chaînes existantes (1ère ligne = headline, reste = details)
            // sauf pour CPU/RAM/GPU où on extrait spécifiquement le "big number" clé.
            SplitHeadline(TxtCpuHead, TxtCpuBig, TxtCpuDetails,  d.GetValueOrDefault("cpu",  ""), CpuExtract);
            SplitHeadline(TxtRamHead, TxtRamBig, TxtRamDetails,  d.GetValueOrDefault("ram",  ""), RamExtract);
            SplitHeadline(TxtGpuHead, TxtGpuBig, TxtGpuDetails,  d.GetValueOrDefault("gpu",  ""), GpuExtract);
            SplitHeadline(TxtOsHead,  null,      TxtOsDetails,   d.GetValueOrDefault("os",   ""), null);
            SplitHeadline(TxtDiskHead,TxtDiskBig,TxtDiskDetails, d.GetValueOrDefault("disk", ""), DiskExtract);
            SplitHeadline(TxtMbHead,  null,      TxtMbDetails,   d.GetValueOrDefault("mb",   ""), null);

            // Stocker fabricant + modèle pour le bouton BIOS
            _biosMfr   = d.GetValueOrDefault("mb_mfr",   "");
            _biosModel = d.GetValueOrDefault("mb_model",  "");
            BtnBios.IsEnabled = _biosModel.Length > 0;

            // ── Carte mère illustrée (v1.3.3) : données vivantes sur le dessin ──
            // Slots RAM : remplir les barrettes du schéma selon la config RÉELLE
            // (parse « Slots : 2 / 4 » produit par CollectHardware). L'illustration
            // dessine toujours 4 slots (ATX standard) — on en allume min(occupés, 4).
            try
            {
                var m = Regex.Match(d.GetValueOrDefault("ram", ""), @"Slots\s*:\s*(\d+)\s*/\s*(\d+)");
                if (m.Success)
                {
                    int used  = int.Parse(m.Groups[1].Value);
                    int total = int.Parse(m.Groups[2].Value);
                    TxtRamSlots.Text = $"{used} / {total} slots occupés";
                    var sticks = new[] { RamStick0, RamStick1, RamStick2, RamStick3 };
                    // Remplissage réaliste : 2 barrettes sur 4 → slots 2 et 4 (dual channel A2/B2)
                    var pattern = used switch
                    {
                        1 => new[] { 1 },
                        2 => new[] { 1, 3 },
                        3 => new[] { 0, 1, 3 },
                        _ => new[] { 0, 1, 2, 3 },
                    };
                    foreach (var idx in pattern.Where(i => i < sticks.Length).Take(Math.Min(used, 4)))
                    {
                        sticks[idx].Background  = new SolidColorBrush(Color.FromArgb(0x55, 0x5B, 0xA0, 0xFF));
                        sticks[idx].BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0xA0, 0xFF));
                    }
                }
            }
            catch { }

            // Sérigraphie : le VRAI modèle de la carte mère imprimé sur le PCB
            try
            {
                if (_biosModel.Length > 0)
                    TxtMbSilk.Text = _biosModel.ToUpperInvariant();
            }
            catch { }

            // Teinte les valeurs chargées (compat)
            foreach (var tb in new[] { TxtCPU, TxtRAM, TxtGPU, TxtOS, TxtDisk, TxtMB })
                tb.ClearValue(TextBlock.ForegroundProperty);

            _main.Log("Informations matérielles chargées.");
        }

        // ── Helpers d'extraction "big number" pour le layout Bento 2026 ──────────

        /// <summary>Pose Head = 1ère ligne, Big = extracteur (peut être null), Details = reste.</summary>
        private static void SplitHeadline(System.Windows.Controls.TextBlock? head,
                                          System.Windows.Controls.TextBlock? big,
                                          System.Windows.Controls.TextBlock? details,
                                          string raw, Func<string, (string big, string details)>? extractor)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                if (head != null)    head.Text    = "—";
                if (big != null)     big.Text     = "";
                if (details != null) details.Text = "";
                return;
            }
            var lines = raw.Replace("\r", "").Split('\n');
            string firstLine = lines.Length > 0 ? lines[0].Trim() : "";
            string rest = lines.Length > 1
                ? string.Join("\n", lines.Skip(1).Select(s => s.Trim()).Where(s => s.Length > 0))
                : "";

            if (extractor != null)
            {
                var (bigVal, det) = extractor(raw);
                if (head != null)    head.Text    = firstLine;
                if (big != null)     big.Text     = bigVal;
                if (details != null) details.Text = det;
            }
            else
            {
                if (head != null)    head.Text    = firstLine;
                if (details != null) details.Text = rest;
            }
        }

        // CPU : "Intel Core Ultra 7 265K\n20C / 20T | L3 : 30 Mo | L2 : 36 Mo\nFréq. max : 3,9 GHz\nArrow Lake | Socket : LGA1851"
        //   big = la fréquence max (genre "3,9 GHz")
        //   details = MULTI-LIGNE : toutes les lignes après le nom SAUF la fréq. max (qui est dans big)
        private static (string big, string details) CpuExtract(string raw)
        {
            var lines = raw.Replace("\r", "").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"(\d+[,.]?\d*)\s*GHz");
            string big = m.Success ? m.Groups[1].Value.Replace('.', ',') + " GHz" : "";
            // Détails = toutes les lignes après la 1ère (nom), sauf "Fréq. max" déjà dans big
            var detailLines = lines.Skip(1)
                .Where(l => !l.StartsWith("Fréq.", StringComparison.OrdinalIgnoreCase));
            string details = string.Join("\n", detailLines);
            return (big, details);
        }

        // RAM : "Total : 64 Go (2 x 32 Go)\nType : DDR5 @ 6400 MHz | 1,4V\n..."
        //   big = "64 Go" (le total)
        //   details = type DDR5 @ MHz + nb slots etc.
        private static (string big, string details) RamExtract(string raw)
        {
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"Total\s*:\s*(\d+\s*Go)");
            string big = m.Success ? m.Groups[1].Value : "";
            // Detail : les lignes après la 1ère
            var lines = raw.Replace("\r", "").Split('\n').Skip(1)
                .Select(s => s.Trim()).Where(s => s.Length > 0);
            return (big, string.Join("\n", lines));
        }

        // GPU : "NVIDIA RTX 4070 SUPER | 12 Go VRAM\nDriver : 32.0.15.9649\nArchitecture : Ada Lovelace"
        //   big = "12 Go" (la VRAM)
        //   details = MULTI-LIGNE : driver + architecture
        private static (string big, string details) GpuExtract(string raw)
        {
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"(\d+\s*Go)\s*VRAM");
            string big = m.Success ? m.Groups[1].Value : "";
            var lines = raw.Replace("\r", "").Split('\n').Skip(1)
                .Select(s => s.Trim()).Where(s => s.Length > 0);
            return (big, string.Join("\n", lines));
        }

        // Stockage : "KINGSTON ... 480 Go\nPNY ... 500 Go\nSamsung ... 1000 Go"
        //   big = nombre de disques (genre "3")
        //   details = la liste complète
        private static (string big, string details) DiskExtract(string raw)
        {
            var lines = raw.Replace("\r", "").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            // Le compteur « N DISQUES » ne doit PAS inclure la ligne récapitulative
            // « Capacité totale : … » (sinon il affiche 1 disque de trop). Le détail, lui,
            // garde toutes les lignes (total compris).
            int diskCount = lines.Count(l => !l.StartsWith("Capacité totale", StringComparison.OrdinalIgnoreCase));
            return (diskCount > 0 ? diskCount.ToString() : "", string.Join("\n", lines));
        }

        // ── Schéma machine (v1.3.3) : détail au clic sur un nœud ────────────────
        // Un seul panneau ouvert à la fois ; re-cliquer le même nœud referme la zone.

        private System.Windows.Controls.StackPanel? _openSpecDetail;

        private void ToggleSpecDetail(System.Windows.Controls.StackPanel panel)
        {
            foreach (var p in new[] { DetCpu, DetRam, DetGpu, DetSto, DetMb, DetOs })
                p.Visibility = Visibility.Collapsed;

            if (_openSpecDetail == panel)
            {
                _openSpecDetail = null;
                SpecDetailZone.Visibility = Visibility.Collapsed;
                TxtSchemaHint.Visibility  = Visibility.Visible;
                return;
            }
            _openSpecDetail = panel;
            panel.Visibility          = Visibility.Visible;
            SpecDetailZone.Visibility = Visibility.Visible;
            TxtSchemaHint.Visibility  = Visibility.Collapsed;
        }

        private void NodeCpu_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSpecDetail(DetCpu);
        private void NodeRam_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSpecDetail(DetRam);
        private void NodeGpu_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSpecDetail(DetGpu);
        private void NodeSto_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSpecDetail(DetSto);
        private void NodeMb_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)  => ToggleSpecDetail(DetMb);
        private void NodeOs_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)  => ToggleSpecDetail(DetOs);

        private void BtnBios_Click(object sender, RoutedEventArgs e)
        {
            var url = Helpers.BiosUrl.Build(_biosMfr, _biosModel);
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { _main.Log($"BIOS URL : erreur — {ex.Message}"); }
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
                    col = Optimisation_Tool.Helpers.ThemeManager.C("ThOk");
                    status = $"OPTIMAL — le GPU tourne à pleine vitesse ({pct:F0} %)";
                }
                else if (g.atRest)
                {
                    // Driver Nvidia downclock le lien PCIe quand le GPU est inactif (ASPM). Comportement
                    // NORMAL : la vraie vitesse revient en charge. On l'affiche en bleu informatif,
                    // PAS en jaune alerte — sinon on inquiète l'utilisateur pour rien.
                    col = Optimisation_Tool.Helpers.ThemeManager.C("ThAccentIcon");
                    status = $"GPU AU REPOS — le lien descend à {GenName[g.curGen]} x{g.curWidth} pour économiser. " +
                             $"En charge, il revient à {GenName[g.maxGen]} x{g.maxWidth}.";
                    // On gonfle la barre au max pour refléter la capacité réelle, pas le repos
                    SetBarPct(CompatGpuBar, 100);
                }
                else if (g.curGen < g.maxGen)
                {
                    col = Optimisation_Tool.Helpers.ThemeManager.C("ThWarn");
                    status = $"SLOT LIMITÉ — actuel {GenName[g.curGen]} x{g.curWidth}, le GPU supporte {GenName[g.maxGen]} x{g.maxWidth} ({pct:F0} %)";
                }
                else
                {
                    col = Optimisation_Tool.Helpers.ThemeManager.C("ThWarn");
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
        // Vitesse PCIe via nvidia-smi. ⚠️ PIÈGE : au repos, le driver Nvidia downclock le lien PCIe
        // (ASPM) → on lit alors PCIe 1.x ou 2.x au lieu du max, alors que la carte mère n'est PAS bridée.
        // GPU-Z et HWiNFO contournent ça avec un « Render Test » qui réveille la carte. Nous : on fait
        // jusqu'à 3 mesures espacées et on garde le MAXIMUM observé + on lit le pstate (P0..P8 = du plus
        // chargé au plus idle) pour distinguer « GPU au repos = normal » de « slot réellement bridé ».
        private static (string name, int maxGen, int curGen, int maxWidth, int curWidth, bool atRest) GetGpuPcie()
        {
            string name = "";
            int maxGen = 0, maxWidth = 0;
            int bestCurGen = 0, bestCurWidth = 0;
            int lastPstate = -1;   // P-state observé à la DERNIÈRE mesure

            for (int i = 0; i < 3; i++)
            {
                var (n, mg, cg, mw, cw, ps) = QueryGpuPcieOnce();
                if (n.Length > 0) name = n;
                if (mg > maxGen)  maxGen = mg;
                if (mw > maxWidth) maxWidth = mw;
                if (cg > bestCurGen)   bestCurGen   = cg;
                if (cw > bestCurWidth) bestCurWidth = cw;
                if (ps >= 0) lastPstate = ps;

                // Tant qu'on n'est pas au max, on retente jusqu'à 3 fois (le GPU peut se réveiller entre 2 mesures).
                if (bestCurGen >= maxGen && bestCurWidth >= maxWidth) break;
                if (i < 2) System.Threading.Thread.Sleep(180);
            }

            // « Au repos » = on n'a pas atteint le max ET le P-state est élevé (≥ P5 = idle).
            bool atRest = (bestCurGen < maxGen || bestCurWidth < maxWidth) && lastPstate >= 5;
            return (name, maxGen, bestCurGen, maxWidth, bestCurWidth, atRest);
        }

        // Une mesure nvidia-smi : renvoie aussi le pstate (P0=0, P1=1, …, P8=8). pstate=-1 si indispo.
        private static (string, int, int, int, int, int) QueryGpuPcieOnce()
        {
            try
            {
                foreach (var genField in new[] { "pcie.link.gen.gpumax", "pcie.link.gen.max" })
                {
                    var args = $"--query-gpu=name,{genField},pcie.link.gen.current,pcie.link.width.max,pcie.link.width.current,pstate " +
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
                    var mg = I(parts[1]); var cg = I(parts[2]);
                    var mw = I(parts[3]); var cw = I(parts[4]);
                    int ps = -1;
                    if (parts.Length >= 6)
                    {
                        var psRaw = parts[5].Trim().TrimStart('P', 'p');
                        if (int.TryParse(psRaw, out var n)) ps = n;
                    }
                    if (mg > 0 && cg > 0)
                        return (name, mg, cg, mw, cw, ps);
                    // nom récupéré mais gen=0 → tenter l'autre champ
                    if (genField == "pcie.link.gen.max") return (name, 0, 0, 0, 0, ps);
                }
            }
            catch { }
            return ("", 0, 0, 0, 0, -1);
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
                        5 => Optimisation_Tool.Helpers.ThemeManager.C("ThWarn"),
                        4 => Optimisation_Tool.Helpers.ThemeManager.C("ThOk"),
                        3 => Optimisation_Tool.Helpers.ThemeManager.C("ThAccentIcon"),
                        _ => Optimisation_Tool.Helpers.ThemeManager.C("ThTextDim"),
                    });
                }
                else
                {
                    item.Info  = "Modèle non répertorié — consulter les specs du fabricant";
                    item.Badge = "?";
                    item.BadgeColor = FrozenBrush(Optimisation_Tool.Helpers.ThemeManager.C("ThTextDim"));
                }
                list.Add(item);
            }
            return list;
        }
    }
}
