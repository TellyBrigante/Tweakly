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
        public string Role      { get; set; } = "ThTextDim";
    }

    // Ligne de l'onglet "Réglages BIOS" : lecture seule, preuve locale, aucune déduction agressive.
    public sealed class FirmwareSettingItem
    {
        public string Group  { get; set; } = "";
        public string Title  { get; set; } = "";
        public string Value  { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Source { get; set; } = "";
        public string Role   { get; set; } = "ThTextDim";
    }

    public sealed class FirmwareInsightItem
    {
        public string Title  { get; set; } = "";
        public string Value  { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Role   { get; set; } = "ThTextDim";
    }

    public sealed class FirmwareChangeItem
    {
        public string Title  { get; set; } = "";
        public string Before { get; set; } = "";
        public string After  { get; set; } = "";
        public string Role   { get; set; } = "ThTextDim";
    }

    public partial class PageSpecs : UserControl
    {
        private readonly MainWindow _main;
        private bool   _loaded   = false;
        private bool   _compatLoaded = false;
        private bool   _biosLoaded = false;
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
            PanelBios.Visibility   = Visibility.Collapsed;
            StyleTab(BtnTabSpecs, true);
            StyleTab(BtnTabCompat, false);
            StyleTab(BtnTabBios, false);
        }

        private void BtnTabCompat_Click(object sender, RoutedEventArgs e)
        {
            PanelSpecs.Visibility  = Visibility.Collapsed;
            PanelCompat.Visibility = Visibility.Visible;
            PanelBios.Visibility   = Visibility.Collapsed;
            StyleTab(BtnTabSpecs, false);
            StyleTab(BtnTabCompat, true);
            StyleTab(BtnTabBios, false);
            if (!_compatLoaded) { _compatLoaded = true; _ = LoadCompatAsync(); }
        }

        private void BtnTabBios_Click(object sender, RoutedEventArgs e)
        {
            PanelSpecs.Visibility  = Visibility.Collapsed;
            PanelCompat.Visibility = Visibility.Collapsed;
            PanelBios.Visibility   = Visibility.Visible;
            StyleTab(BtnTabSpecs, false);
            StyleTab(BtnTabCompat, false);
            StyleTab(BtnTabBios, true);
            if (!_biosLoaded) { _biosLoaded = true; _ = LoadBiosSettingsAsync(); }
        }

        private static void StyleTab(Button btn, bool active)
        {
            btn.ApplyTemplate();
            if (btn.Template.FindName("Bg",  btn) is not Border bg)     return;
            if (btn.Template.FindName("Lbl", btn) is not TextBlock lbl) return;

            if (active)
            {
                bg.SetResourceReference(Border.BackgroundProperty, "ThTabSel");
                lbl.Foreground = Brushes.White;
            }
            else
            {
                bg.Background = Brushes.Transparent;
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            }
        }

        // ── Chargement ────────────────────────────────────────────────────────

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded) return;
            _loaded = true;

            StyleTab(BtnTabSpecs, true);   // onglet Spécifications actif par défaut
            StyleTab(BtnTabCompat, false);
            StyleTab(BtnTabBios, false);

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
            try { using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { _main.Log($"BIOS URL : erreur — {ex.Message}"); }
        }

        // ── Réglages BIOS / firmware (lecture seule) ─────────────────────────

        private async Task LoadBiosSettingsAsync()
        {
            TxtBiosSummary.Text = "Lecture des informations firmware...";
            BiosSettingsList.ItemsSource = null;

            List<FirmwareSettingItem> items;
            try { items = await Task.Run(CollectFirmwareSettings); }
            catch (Exception ex)
            {
                items = new List<FirmwareSettingItem>
                {
                    FwItem("Lecture", "Analyse firmware", "Impossible", ex.Message, "Tweakly", "ThCrit")
                };
            }

            BiosSettingsList.ItemsSource = items;
            BiosInsightList.ItemsSource = BuildFirmwareInsights(items);

            var previous = Helpers.FirmwareSnapshotStore.Latest();
            var snapshot = CreateFirmwareSnapshot(items);
            RenderFirmwareChanges(previous, snapshot, items);
            Helpers.FirmwareSnapshotStore.Append(snapshot);

            int unavailable = items.Count(i => i.Value.Contains("Non expos", StringComparison.OrdinalIgnoreCase) ||
                                               i.Value.Contains("Impossible", StringComparison.OrdinalIgnoreCase));
            TxtBiosSummary.Text = unavailable > 0
                ? $"{items.Count} point(s) vérifié(s), {unavailable} information(s) non exposée(s) par Windows."
                : $"{items.Count} point(s) vérifié(s).";
            TxtBiosLastRead.Text = $"Lu {DateTime.Now:HH:mm:ss}";
            _main.Log($"Informations Système : réglages BIOS/firmware chargés — {items.Count} point(s).");
        }

        private static List<FirmwareSettingItem> CollectFirmwareSettings()
        {
            var items = new List<FirmwareSettingItem>();

            var board = QueryBoardAndBios();
            items.Add(FwItem("Carte mère", "Modèle", board.Board.Length > 0 ? board.Board : "Non exposé",
                board.Manufacturer.Length > 0 ? board.Manufacturer : "Fabricant non exposé.", "WMI", board.Board.Length > 0 ? "ThTextBody" : "ThTextDim"));
            items.Add(FwItem("Carte mère", "BIOS", board.Bios.Length > 0 ? board.Bios : "Non exposé",
                board.BiosDate.Length > 0 ? $"Date : {board.BiosDate}" : "Date non exposée.", "WMI", board.Bios.Length > 0 ? "ThAccentIcon" : "ThTextDim"));

            var firmware = QueryFirmwareMode();
            items.Add(FwItem("Démarrage", "Mode firmware", firmware.Value, firmware.Detail, firmware.Source, firmware.Role));

            var secureBoot = QuerySecureBoot();
            items.Add(FwItem("Démarrage", "Secure Boot", secureBoot.Value, secureBoot.Detail, secureBoot.Source, secureBoot.Role));

            var tpm = QueryTpm();
            items.Add(FwItem("Sécurité", "TPM", tpm.Value, tpm.Detail, tpm.Source, tpm.Role));

            var virt = QueryCpuVirtualization();
            items.Add(FwItem("CPU", "Virtualisation firmware", virt.Value, virt.Detail, virt.Source, virt.Role));

            var hv = QueryHypervisor();
            items.Add(FwItem("Windows", "Hyperviseur actif", hv.Value, hv.Detail, hv.Source, hv.Role));

            var vbs = QueryVbs();
            items.Add(FwItem("Windows", "VBS / Device Guard", vbs.Value, vbs.Detail, vbs.Source, vbs.Role));

            var hvci = QueryHvciState();
            items.Add(FwItem("Windows", "Intégrité mémoire (HVCI)", hvci.Value, hvci.Detail, hvci.Source, hvci.Role));

            var mem = QueryMemoryClock();
            items.Add(FwItem("Mémoire", "Fréquence RAM actuelle", mem.Value, mem.Detail, mem.Source, mem.Role));
            items.Add(FwItem("Mémoire", "Profil mémoire", mem.ProfileValue, mem.ProfileDetail, mem.Source, mem.ProfileRole));

            return items;
        }

        private static FirmwareSettingItem FwItem(string group, string title, string value, string detail, string source, string role)
            => new()
            {
                Group = group,
                Title = title,
                Value = value,
                Detail = detail,
                Source = source,
                Role = role,
            };

        private static List<FirmwareInsightItem> BuildFirmwareInsights(List<FirmwareSettingItem> items)
        {
            FirmwareSettingItem item(string title) =>
                items.FirstOrDefault(i => i.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                ?? FwItem("", title, "Non exposé", "", "Tweakly", "ThTextDim");

            var firmware = item("Mode firmware");
            var bios = item("BIOS");
            var secureBoot = item("Secure Boot");
            var tpm = item("TPM");
            var memClock = item("Fréquence RAM actuelle");
            var memProfile = item("Profil mémoire");
            var hv = item("Hyperviseur actif");
            var vbs = item("VBS / Device Guard");
            var hvci = item("Intégrité mémoire (HVCI)");

            bool uefi = firmware.Value.Equals("UEFI", StringComparison.OrdinalIgnoreCase);
            bool secure = StartsActive(secureBoot.Value);
            bool tpm20 = tpm.Value.Contains("2.0", StringComparison.OrdinalIgnoreCase);

            var missing = new List<string>();
            if (!uefi) missing.Add("UEFI");
            if (!secure) missing.Add("Secure Boot");
            if (!tpm20) missing.Add("TPM 2.0");

            var perfFlags = new List<string>();
            if (StartsActive(hv.Value)) perfFlags.Add("hyperviseur actif");
            if (StartsActive(vbs.Value) || vbs.Value.StartsWith("Configuré", StringComparison.OrdinalIgnoreCase)) perfFlags.Add("VBS actif/configuré");
            if (StartsActive(hvci.Value)) perfFlags.Add("HVCI actif");

            return new List<FirmwareInsightItem>
            {
                Insight("Plateforme", $"{firmware.Value} · BIOS {bios.Value}",
                    bios.Detail.Length > 0 ? bios.Detail : "Version BIOS non exposée.",
                    uefi ? "ThOk" : firmware.Role),

                Insight("Base Windows 11", missing.Count == 0 ? "Prête" : "À vérifier",
                    missing.Count == 0
                        ? "UEFI, Secure Boot et TPM 2.0 détectés."
                        : "Manquant ou non exposé : " + string.Join(", ", missing) + ".",
                    missing.Count == 0 ? "ThOk" : "ThWarn"),

                Insight("Mémoire", memProfile.Value,
                    $"{memClock.Value}. {Shorten(memProfile.Detail, 145)}",
                    memProfile.Role),

                Insight("Impact perf Windows", perfFlags.Count == 0 ? "Aucun frein détecté" : "Impact possible",
                    perfFlags.Count == 0
                        ? "Hyperviseur, VBS et HVCI inactifs."
                        : string.Join(", ", perfFlags) + ".",
                    perfFlags.Count == 0 ? "ThOk" : "ThWarn"),
            };
        }

        private static FirmwareInsightItem Insight(string title, string value, string detail, string role)
            => new()
            {
                Title = title,
                Value = value,
                Detail = detail,
                Role = role,
            };

        private static readonly string[] FirmwareTrackedKeys =
        {
            "Carte mère|BIOS",
            "Démarrage|Mode firmware",
            "Démarrage|Secure Boot",
            "Sécurité|TPM",
            "CPU|Virtualisation firmware",
            "Windows|Hyperviseur actif",
            "Windows|VBS / Device Guard",
            "Windows|Intégrité mémoire (HVCI)",
            "Mémoire|Fréquence RAM actuelle",
            "Mémoire|Profil mémoire",
        };

        private static Helpers.FirmwareSnapshot CreateFirmwareSnapshot(List<FirmwareSettingItem> items)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in items)
                values[SnapshotKey(i)] = i.Value;

            return new Helpers.FirmwareSnapshot
            {
                CapturedAtUtc = DateTime.UtcNow,
                Values = values,
            };
        }

        private void RenderFirmwareChanges(Helpers.FirmwareSnapshot? previous, Helpers.FirmwareSnapshot current, List<FirmwareSettingItem> items)
        {
            if (previous == null)
            {
                BiosChangeList.ItemsSource = Array.Empty<FirmwareChangeItem>();
                TxtBiosChangesTitle.Text = "Référence créée";
                TxtBiosChangesSub.Text = "La prochaine lecture affichera les changements BIOS/firmware détectés depuis cette base.";
                return;
            }

            var roleByKey = items.ToDictionary(SnapshotKey, i => i.Role, StringComparer.OrdinalIgnoreCase);
            var changes = new List<FirmwareChangeItem>();

            foreach (string key in FirmwareTrackedKeys)
            {
                previous.Values.TryGetValue(key, out string? before);
                current.Values.TryGetValue(key, out string? after);
                before = CleanSnapshotValue(before);
                after = CleanSnapshotValue(after);
                if (before.Length == 0 || after.Length == 0) continue;
                if (before.Equals(after, StringComparison.OrdinalIgnoreCase)) continue;

                string role = roleByKey.TryGetValue(key, out var r) ? r : "ThAccentIcon";
                changes.Add(new FirmwareChangeItem
                {
                    Title = SnapshotTitle(key),
                    Before = before,
                    After = after,
                    Role = role,
                });
            }

            BiosChangeList.ItemsSource = changes;
            string previousLocal = previous.CapturedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);

            if (changes.Count == 0)
            {
                TxtBiosChangesTitle.Text = "Aucun changement détecté";
                TxtBiosChangesSub.Text = $"Comparé à la lecture du {previousLocal}.";
            }
            else
            {
                TxtBiosChangesTitle.Text = $"{changes.Count} changement(s) détecté(s)";
                TxtBiosChangesSub.Text = $"Comparé à la lecture du {previousLocal}.";
            }
        }

        private static string SnapshotKey(FirmwareSettingItem item) => $"{item.Group}|{item.Title}";

        private static string SnapshotTitle(string key)
        {
            int idx = key.IndexOf('|');
            return idx >= 0 && idx + 1 < key.Length ? key[(idx + 1)..] : key;
        }

        private static string CleanSnapshotValue(string? value)
            => (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

        private static bool StartsActive(string value)
            => value.StartsWith("Activé", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Activée", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Actif", StringComparison.OrdinalIgnoreCase);

        private static string Shorten(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxChars) return value;
            return value[..Math.Max(0, maxChars - 1)].TrimEnd() + "…";
        }

        private static (string Value, string Detail, string Source, string Role) Info(string value, string detail, string source, string role)
            => (value, detail, source, role);

        private sealed class MemoryClockInfo
        {
            public string Value { get; init; } = "Non exposé";
            public string Detail { get; init; } = "Fréquence mémoire non lisible via SMBIOS.";
            public string Source { get; init; } = "SMBIOS";
            public string Role { get; init; } = "ThTextDim";
            public string ProfileValue { get; init; } = "Non vérifiable";
            public string ProfileDetail { get; init; } = "Windows ne donne pas le nom du profil XMP/EXPO actif.";
            public string ProfileRole { get; init; } = "ThTextDim";
        }

        private static (string Manufacturer, string Board, string Bios, string BiosDate) QueryBoardAndBios()
        {
            string manufacturer = "", board = "", bios = "", biosDate = "";
            try
            {
                using var q = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
                foreach (ManagementObject o in q.Get())
                {
                    manufacturer = (o["Manufacturer"]?.ToString() ?? "").Trim();
                    board = (o["Product"]?.ToString() ?? "").Trim();
                    o.Dispose();
                    break;
                }
            }
            catch { }

            try
            {
                using var q = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
                foreach (ManagementObject o in q.Get())
                {
                    bios = (o["SMBIOSBIOSVersion"]?.ToString() ?? "").Trim();
                    var raw = o["ReleaseDate"]?.ToString() ?? "";
                    biosDate = raw.Length >= 8 ? $"{raw[6..8]}/{raw[4..6]}/{raw[..4]}" : raw;
                    o.Dispose();
                    break;
                }
            }
            catch { }

            return (manufacturer, board, bios, biosDate);
        }

        private static (string Value, string Detail, string Source, string Role) QueryFirmwareMode()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
                if (k?.GetValue("PEFirmwareType") is int v)
                {
                    return v switch
                    {
                        2 => Info("UEFI", "Windows a démarré en mode UEFI.", "Registre", "ThOk"),
                        1 => Info("Legacy BIOS", "Windows a démarré en mode BIOS hérité.", "Registre", "ThWarn"),
                        _ => Info($"Inconnu ({v})", "Valeur firmware non reconnue.", "Registre", "ThTextDim"),
                    };
                }
            }
            catch { }

            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
                if (k?.GetValue("UEFISecureBootEnabled") is int)
                    return Info("UEFI", "PEFirmwareType absent, mais l'état Secure Boot UEFI est exposé par Windows.", "Registre", "ThOk");
            }
            catch { }

            return Info("Non exposé", "Windows ne fournit pas le type de firmware sur cette machine.", "Registre", "ThTextDim");
        }

        private static (string Value, string Detail, string Source, string Role) QuerySecureBoot()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
                if (k?.GetValue("UEFISecureBootEnabled") is int v)
                {
                    return v == 1
                        ? Info("Activé", "Le démarrage sécurisé UEFI est actif.", "Registre", "ThOk")
                        : Info("Désactivé", "Secure Boot est disponible mais désactivé.", "Registre", "ThWarn");
                }
            }
            catch { }
            return Info("Non exposé", "La valeur Secure Boot n'est pas lisible depuis Windows.", "Registre", "ThTextDim");
        }

        private static (string Value, string Detail, string Source, string Role) QueryTpm()
        {
            try
            {
                using var q = new ManagementObjectSearcher(
                    @"root\CIMV2\Security\MicrosoftTpm",
                    "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm");
                foreach (ManagementObject o in q.Get())
                {
                    bool enabled = Convert.ToBoolean(o["IsEnabled_InitialValue"] ?? false);
                    bool active  = Convert.ToBoolean(o["IsActivated_InitialValue"] ?? false);
                    string spec  = (o["SpecVersion"]?.ToString() ?? "").Trim();
                    o.Dispose();

                    if (enabled && active)
                    {
                        string version = spec.Contains("2.0", StringComparison.OrdinalIgnoreCase) ? "2.0" : spec;
                        return Info(version.Length > 0 ? $"Activé ({version})" : "Activé",
                            "TPM présent et activé côté firmware.", "WMI TPM", "ThOk");
                    }
                    return Info("Désactivé", "TPM détecté mais pas entièrement activé.", "WMI TPM", "ThWarn");
                }
            }
            catch { }
            return Info("Non détecté", "TPM non trouvé ou namespace TPM inaccessible.", "WMI TPM", "ThTextDim");
        }

        private static (string Value, string Detail, string Source, string Role) QueryCpuVirtualization()
        {
            try
            {
                using var q = new ManagementObjectSearcher(
                    "SELECT Name, VirtualizationFirmwareEnabled, SecondLevelAddressTranslationExtensions FROM Win32_Processor");
                foreach (ManagementObject o in q.Get())
                {
                    bool virt = Convert.ToBoolean(o["VirtualizationFirmwareEnabled"] ?? false);
                    bool slat = Convert.ToBoolean(o["SecondLevelAddressTranslationExtensions"] ?? false);
                    string cpu = (o["Name"]?.ToString() ?? "").Trim();
                    o.Dispose();
                    return virt
                        ? Info("Activée", $"{cpu}\nSLAT : {(slat ? "présent" : "non exposé")}.", "WMI CPU", "ThOk")
                        : Info("Désactivée", "La virtualisation CPU est désactivée dans le firmware/BIOS.", "WMI CPU", "ThWarn");
                }
            }
            catch { }
            return Info("Non exposé", "Windows ne fournit pas l'état de virtualisation firmware.", "WMI CPU", "ThTextDim");
        }

        private static (string Value, string Detail, string Source, string Role) QueryHypervisor()
        {
            try
            {
                using var q = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem");
                foreach (ManagementObject o in q.Get())
                {
                    bool present = Convert.ToBoolean(o["HypervisorPresent"] ?? false);
                    o.Dispose();
                    return present
                        ? Info("Actif", "L'hyperviseur Windows tourne actuellement.", "WMI", "ThAccentIcon")
                        : Info("Inactif", "Aucun hyperviseur Windows actif détecté.", "WMI", "ThOk");
                }
            }
            catch { }
            return Info("Non exposé", "État Hyper-V/hyperviseur non lisible.", "WMI", "ThTextDim");
        }

        private static (string Value, string Detail, string Source, string Role) QueryVbs()
        {
            try
            {
                using var q = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\DeviceGuard",
                    "SELECT VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard");
                foreach (ManagementObject o in q.Get())
                {
                    int status = Convert.ToInt32(o["VirtualizationBasedSecurityStatus"] ?? 0);
                    o.Dispose();
                    return status switch
                    {
                        2 => Info("Actif", "Virtualization-Based Security est en cours d'exécution.", "DeviceGuard", "ThAccentIcon"),
                        1 => Info("Configuré", "VBS est configuré mais pas forcément actif.", "DeviceGuard", "ThWarn"),
                        0 => Info("Inactif", "VBS n'est pas actif.", "DeviceGuard", "ThOk"),
                        _ => Info($"Inconnu ({status})", "Statut DeviceGuard non reconnu.", "DeviceGuard", "ThTextDim"),
                    };
                }
            }
            catch { }
            return Info("Non exposé", "DeviceGuard n'est pas lisible sur cette machine.", "DeviceGuard", "ThTextDim");
        }

        private static (string Value, string Detail, string Source, string Role) QueryHvciState()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
                if (k?.GetValue("Enabled") is int enabled)
                {
                    return enabled == 1
                        ? Info("Activée", "L'intégrité mémoire est activée côté Windows.", "Registre", "ThAccentIcon")
                        : Info("Désactivée", "L'intégrité mémoire est désactivée côté Windows.", "Registre", "ThOk");
                }
            }
            catch { }
            return Info("Désactivée", "La clé HVCI est absente : Windows n'expose pas d'intégrité mémoire active.", "Registre", "ThOk");
        }

        private static MemoryClockInfo QueryMemoryClock()
        {
            try
            {
                using var q = new ManagementObjectSearcher(
                    "SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, PartNumber FROM Win32_PhysicalMemory");
                int modules = 0, speed = 0, configured = 0, type = 0;
                long total = 0;
                var parts = new List<string>();
                foreach (ManagementObject o in q.Get())
                {
                    modules++;
                    total += Convert.ToInt64(o["Capacity"] ?? 0);
                    if (speed == 0) int.TryParse(o["Speed"]?.ToString(), out speed);
                    if (configured == 0) int.TryParse(o["ConfiguredClockSpeed"]?.ToString(), out configured);
                    if (type == 0) int.TryParse(o["SMBIOSMemoryType"]?.ToString(), out type);
                    var part = (o["PartNumber"]?.ToString() ?? "").Trim();
                    if (part.Length > 0) parts.Add(part);
                    o.Dispose();
                }

                if (modules > 0)
                {
                    var typeName = type switch { 20 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", _ => "DDR" };
                    int shown = configured > 0 ? configured : speed;
                    string totalGb = Math.Round(total / (double)(1L << 30), 0).ToString("F0", CultureInfo.InvariantCulture) + " Go";
                    int rated = ParseMemoryRatedMhz(parts);
                    string baseDetail = $"{modules} module(s), {totalGb}. Valeur lue par SMBIOS.";
                    var profile = BuildMemoryProfile(typeName, configured, speed, rated, parts);
                    return new MemoryClockInfo
                    {
                        Value = shown > 0 ? $"{typeName} @ {shown} MHz" : $"{typeName} détectée",
                        Detail = baseDetail,
                        Source = "SMBIOS",
                        Role = shown > 0 ? "ThAccentIcon" : "ThTextBody",
                        ProfileValue = profile.Value,
                        ProfileDetail = profile.Detail,
                        ProfileRole = profile.Role,
                    };
                }
            }
            catch { }
            return new MemoryClockInfo();
        }

        private static int ParseMemoryRatedMhz(IEnumerable<string> partNumbers)
        {
            foreach (string part in partNumbers.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var m = Regex.Match(part, @"(?:^|[^0-9])(?:F[45]-|DDR[45]?-?)?(\d{4,5})(?:[^0-9]|$)", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int mhz) && mhz >= 1600 && mhz <= 10000)
                    return mhz;
            }
            return 0;
        }

        private static (string Value, string Detail, string Role) BuildMemoryProfile(
            string typeName, int configuredMhz, int speedMhz, int ratedMhz, IReadOnlyCollection<string> partNumbers)
        {
            int shown = configuredMhz > 0 ? configuredMhz : speedMhz;
            string part = partNumbers.FirstOrDefault() ?? "";
            string moduleLine = part.Length > 0 ? $"Module : {part}" : "Module : non exposé par Windows";

            if (ratedMhz > 0 && shown > 0)
            {
                if (shown >= ratedMhz - 100)
                {
                    return ("Cohérent avec le kit",
                        $"{moduleLine}\nKit annoncé : {ratedMhz} MHz\nFréquence Windows : {shown} MHz\nLa RAM tourne à la bonne fréquence.",
                        "ThOk");
                }

                return ("Sous la fréquence du kit",
                    $"{moduleLine}\nKit annoncé : {ratedMhz} MHz\nFréquence Windows : {shown} MHz\nLe profil XMP/EXPO n'est probablement pas appliqué.",
                    "ThWarn");
            }

            if (shown <= 0)
            {
                return ("Non vérifiable",
                    "Windows ne donne ni fréquence configurée, ni référence exploitable du module.",
                    "ThTextDim");
            }

            if (typeName == "DDR5")
            {
                return shown >= 6000
                    ? ("Fréquence élevée détectée",
                        $"{moduleLine}\nFréquence Windows : {shown} MHz\nLa fréquence est élevée pour de la DDR5.",
                        "ThAccentIcon")
                    : ("Profil rapide non prouvé",
                        $"{moduleLine}\nFréquence Windows : {shown} MHz\nSi le kit est vendu au-dessus, le profil XMP/EXPO n'est probablement pas appliqué.",
                        "ThWarn");
            }

            if (typeName == "DDR4")
            {
                return shown >= 3000
                    ? ("Fréquence élevée détectée",
                        $"{moduleLine}\nFréquence Windows : {shown} MHz\nLa fréquence est élevée pour de la DDR4.",
                        "ThAccentIcon")
                    : ("Profil rapide non prouvé",
                        $"{moduleLine}\nFréquence Windows : {shown} MHz\nSi le kit est vendu au-dessus, le profil XMP/EXPO n'est probablement pas appliqué.",
                        "ThWarn");
            }

            return ("Fréquence lue",
                $"{moduleLine}\nFréquence Windows : {shown} MHz\nTweakly ne peut pas conclure proprement pour ce type mémoire.",
                "ThTextBody");
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

                string role; string status;
                if (g.curGen == g.maxGen && g.curWidth == g.maxWidth)
                {
                    role = "ThOk";
                    status = $"OPTIMAL — le GPU tourne à pleine vitesse ({pct:F0} %)";
                }
                else if (g.atRest)
                {
                    // Driver Nvidia downclock le lien PCIe quand le GPU est inactif (ASPM). Comportement
                    // NORMAL : la vraie vitesse revient en charge. On l'affiche en bleu informatif,
                    // PAS en jaune alerte — sinon on inquiète l'utilisateur pour rien.
                    role = "ThAccentIcon";
                    status = $"GPU AU REPOS — le lien descend à {GenName[g.curGen]} x{g.curWidth} pour économiser. " +
                             $"En charge, il revient à {GenName[g.maxGen]} x{g.maxWidth}.";
                    // On gonfle la barre au max pour refléter la capacité réelle, pas le repos
                    SetBarPct(CompatGpuBar, 100);
                }
                else if (g.curGen < g.maxGen)
                {
                    role = "ThWarn";
                    status = $"SLOT LIMITÉ — actuel {GenName[g.curGen]} x{g.curWidth}, le GPU supporte {GenName[g.maxGen]} x{g.maxWidth} ({pct:F0} %)";
                }
                else
                {
                    role = "ThWarn";
                    status = $"VOIES RÉDUITES — x{g.curWidth} actuel / max x{g.maxWidth} ({pct:F0} %)";
                }
                CompatGpuFill.SetResourceReference(Border.BackgroundProperty, role);
                TxtCompatGpuStatus.SetResourceReference(TextBlock.ForegroundProperty, role);
                TxtCompatGpuStatus.Text = status;
            }
            else
            {
                TxtCompatGpuGen.Text = "Données PCIe indisponibles (GPU NVIDIA + pilotes requis).";
                TxtCompatGpuBw.Text  = "";
                TxtCompatGpuStatus.Text = "";
                CompatGpuFill.SetResourceReference(Border.BackgroundProperty, "ThTextDim");
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
                    item.Role = spec.gen switch
                    {
                        5 => "ThWarn",
                        4 => "ThOk",
                        3 => "ThAccentIcon",
                        _ => "ThTextDim",
                    };
                }
                else
                {
                    item.Info  = "Modèle non répertorié — consulter les specs du fabricant";
                    item.Badge = "?";
                    item.Role = "ThTextDim";
                }
                list.Add(item);
            }
            return list;
        }
    }
}
