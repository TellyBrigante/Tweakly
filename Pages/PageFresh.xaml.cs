using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    // ── Convertisseur statut → couleur (conservé pour compatibilité interne) ─

    public sealed class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value?.ToString() ?? "") switch
            {
                var s when s.StartsWith("✓") => ThemeManager.Brush("ThOk"),
                var s when s.StartsWith("✗") => ThemeManager.Brush("ThCrit"),
                "En cours…"                  => ThemeManager.Brush("ThAccentIcon"),
                "En attente"                 => ThemeManager.Brush("ThTextDim"),
                _                            => ThemeManager.Brush("ThTextBody"),
            };
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // ── Modèle ligne ─────────────────────────────────────────────────────────

    public sealed class FreshAppItem : INotifyPropertyChanged
    {
        private bool   _sel    = false;
        private string _status = "—";

        public string Name     { get; set; } = "";
        public string WingetId { get; set; } = "";
        public string Version  { get; set; } = "";

        public bool IsSelected
        {
            get => _sel;
            set { _sel = value; OnPropChanged(); }
        }
        public string Status
        {
            get => _status;
            set { _status = value; OnPropChanged(); OnPropChanged(nameof(StatusRole)); }
        }

        public string StatusRole => Status switch
        {
            var s when s.StartsWith("✓") => "ThOk",
            var s when s.StartsWith("✗") => "ThCrit",
            "En cours…"                  => "ThAccentIcon",
            "En attente"                 => "ThTextDim",
            _                            => "ThTextBody",
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Modèles JSON manifest ─────────────────────────────────────────────────

    internal sealed class ManifestData
    {
        [JsonPropertyName("date")]    public string              Date    { get; set; } = "";
        [JsonPropertyName("machine")] public string              Machine { get; set; } = "";
        [JsonPropertyName("apps")]    public List<ManifestEntry> Apps    { get; set; } = new();
    }

    internal sealed class ManifestEntry
    {
        [JsonPropertyName("name")]     public string Name     { get; set; } = "";
        [JsonPropertyName("wingetId")] public string WingetId { get; set; } = "";
        [JsonPropertyName("version")]  public string Version  { get; set; } = "";
    }

    // ── Page ──────────────────────────────────────────────────────────────────

    public partial class PageFresh : UserControl
    {
        private enum PageMode { Save, Restore }

        private readonly MainWindow _main;
        private PageMode _mode  = PageMode.Save;
        private int      _step  = 1;  // 1-basé
        private string   _zipPath = "";
        private CancellationTokenSource? _cts;

        private readonly ObservableCollection<FreshAppItem> _saveItems    = new();
        private readonly ObservableCollection<FreshAppItem> _restoreItems = new();

        // Noms des hints par mode et étape
        private static readonly string[] SaveHints =
        {
            "Coche les applications à conserver. WMT-GUI va télécharger les installeurs via winget et les compresser dans un ZIP portable.",
            "Téléchargement en cours via winget — les installeurs sont récupérés un par un. Ne ferme pas WMT-GUI.",
            "ZIP créé avec succès — copie ce fichier sur une clé USB ou un stockage externe avant de formater.",
        };

        private static readonly string[] RestoreHints =
        {
            "Sélectionne le fichier ZIP généré par WMT-GUI (mode SAVE). Il contient les installeurs et le manifest de tes apps.",
            "Extraction du manifest depuis le ZIP en cours…",
            "Coche les applications à réinstaller, puis clique INSTALLER. L'installation utilise les fichiers locaux du ZIP.",
            "Installation en cours — chaque app est installée automatiquement. Ne ferme pas WMT-GUI.",
        };

        public PageFresh(MainWindow main)
        {
            _main = main;
            InitializeComponent();

            DgSaveApps.ItemsSource    = _saveItems;
            DgRestApps.ItemsSource    = _restoreItems;

            _saveItems.CollectionChanged    += (_, e) => { SubscribeItems(e); UpdateSaveButton(); };
            _restoreItems.CollectionChanged += (_, e) => { SubscribeItems(e); UpdateInstallButton(); };

            // Démarrage en mode Save, étape 1
            SetMode(PageMode.Save, 1, startScan: true);
        }

        private void SubscribeItems(NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems == null) return;
            foreach (FreshAppItem item in e.NewItems)
                item.PropertyChanged += (_, _) =>
                {
                    if (_mode == PageMode.Save)    UpdateSaveButton();
                    else                           UpdateInstallButton();
                };
        }

        // ═════════════════════════════════════════════════════════════════════
        // NAVIGATION MODE / ÉTAPE
        // ═════════════════════════════════════════════════════════════════════

        private void SetMode(PageMode mode, int step, bool startScan = false)
        {
            _mode = mode;
            _step = step;

            // Toggle buttons
            SetToggleActive(BtnModeSave,    mode == PageMode.Save);
            SetToggleActive(BtnModeRestore, mode == PageMode.Restore);

            // Steppers
            StepperSave.Visibility    = mode == PageMode.Save    ? Visibility.Visible : Visibility.Collapsed;
            StepperRestore.Visibility = mode == PageMode.Restore ? Visibility.Visible : Visibility.Collapsed;

            UpdateStepper();
            UpdateHint();
            ShowStep();

            if (startScan && mode == PageMode.Save && _saveItems.Count == 0)
                _ = StartScanAsync();
        }

        private void GoToStep(int step) => SetMode(_mode, step);

        private static void SetToggleActive(Button btn, bool active)
        {
            if (active)
            {
                btn.SetResourceReference(BackgroundProperty, "ThTabSel");
                btn.SetResourceReference(ForegroundProperty, "ThWhite");
                btn.SetResourceReference(BorderBrushProperty, "ThTabSel");
            }
            else
            {
                btn.SetResourceReference(BackgroundProperty, "ThSecBtn");
                btn.SetResourceReference(ForegroundProperty, "ThTextNav");
                btn.SetResourceReference(BorderBrushProperty, "ThBorder");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // STEPPER — mise à jour visuelle
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateStepper()
        {
            if (_mode == PageMode.Save)
            {
                UpdateStepDot(SS1Dot, SS1Num, SS1Lbl, 1);
                UpdateStepDot(SS2Dot, SS2Num, SS2Lbl, 2);
                UpdateStepDot(SS3Dot, SS3Num, SS3Lbl, 3);
                UpdateStepLine(SS1Line, 2);
                UpdateStepLine(SS2Line, 3);
            }
            else
            {
                UpdateStepDot(RS1Dot, RS1Num, RS1Lbl, 1);
                UpdateStepDot(RS2Dot, RS2Num, RS2Lbl, 2);
                UpdateStepDot(RS3Dot, RS3Num, RS3Lbl, 3);
                UpdateStepDot(RS4Dot, RS4Num, RS4Lbl, 4);
                UpdateStepLine(RS1Line, 2);
                UpdateStepLine(RS2Line, 3);
                UpdateStepLine(RS3Line, 4);
            }
        }

        private void UpdateStepDot(Border dot, TextBlock num, TextBlock lbl, int stepN)
        {
            if (_step > stepN)
            {
                // Terminé — vert
                dot.SetResourceReference(Border.BackgroundProperty, "ThOk");
                dot.SetResourceReference(Border.BorderBrushProperty, "ThOk");
                num.Text = "✓";
                num.SetResourceReference(TextBlock.ForegroundProperty, "ThWhite");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ThOk");
            }
            else if (_step == stepN)
            {
                // Actif — bleu
                dot.SetResourceReference(Border.BackgroundProperty, "ThTabSel");
                dot.SetResourceReference(Border.BorderBrushProperty, "ThAccentIcon");
                num.Text = stepN.ToString();
                num.SetResourceReference(TextBlock.ForegroundProperty, "ThWhite");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ThTextTitle");
            }
            else
            {
                // Futur — gris (thémé : neutre dans les deux modes)
                dot.SetResourceReference(Border.BackgroundProperty, "ThTrack");
                dot.SetResourceReference(Border.BorderBrushProperty, "ThBorder");
                num.Text = stepN.ToString();
                num.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            }
        }

        private void UpdateStepLine(Border line, int activatesAtStep)
        {
            line.SetResourceReference(Border.BackgroundProperty, _step >= activatesAtStep ? "ThOk" : "ThBorder");
        }

        // ═════════════════════════════════════════════════════════════════════
        // AFFICHAGE PANNEAUX + HINTS
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateHint()
        {
            var hints = _mode == PageMode.Save ? SaveHints : RestoreHints;
            TxtHint.Text = (_step >= 1 && _step <= hints.Length) ? hints[_step - 1] : "";
        }

        private void ShowStep()
        {
            // Cacher tout
            PanelS1.Visibility = PanelS2.Visibility = PanelS3.Visibility = Visibility.Collapsed;
            PanelR1.Visibility = PanelR2.Visibility = PanelR3.Visibility = PanelR4.Visibility = Visibility.Collapsed;

            if (_mode == PageMode.Save)
            {
                switch (_step)
                {
                    case 1: PanelS1.Visibility = Visibility.Visible; break;
                    case 2: PanelS2.Visibility = Visibility.Visible; break;
                    case 3: PanelS3.Visibility = Visibility.Visible; break;
                }
            }
            else
            {
                switch (_step)
                {
                    case 1: PanelR1.Visibility = Visibility.Visible; break;
                    case 2: PanelR2.Visibility = Visibility.Visible; break;
                    case 3: PanelR3.Visibility = Visibility.Visible; break;
                    case 4: PanelR4.Visibility = Visibility.Visible; break;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // TOGGLE MODE
        // ═════════════════════════════════════════════════════════════════════

        private void BtnModeSave_Click(object sender, RoutedEventArgs e)
        {
            if (_mode == PageMode.Save) return;
            SetMode(PageMode.Save, 1, startScan: _saveItems.Count == 0);
        }

        private void BtnModeRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_mode == PageMode.Restore) return;
            SetMode(PageMode.Restore, 1);
        }

        // ═════════════════════════════════════════════════════════════════════
        // SAVE — ÉTAPE 1 : SCAN WINGET
        // ═════════════════════════════════════════════════════════════════════

        private async Task StartScanAsync()
        {
            _saveItems.Clear();
            BtnSaveSelectAll.IsEnabled = BtnSaveDeselectAll.IsEnabled = false;
            BtnDownload.IsEnabled = false;

            // ── Phase 1 : Registre Windows (toujours dispo) ──────────────────
            TxtHint.Text = "Lecture du registre…";
            var regApps = await Task.Run(LoadFromRegistry);

            if (regApps.Count == 0)
            {
                TxtHint.Text = "Aucune application trouvée dans le registre.";
                return;
            }

            // ── Phase 2 : IDs Winget (optionnel) ─────────────────────────────
            TxtHint.Text = $"{regApps.Count} app(s) trouvée(s) — récupération des IDs Winget…";
            var (idMap, wingetAvailable) = await Task.Run(BuildWingetIdMap);

            int withId = 0;
            foreach (var reg in regApps)
            {
                var hasId = idMap.TryGetValue(reg.Name, out var entry);
                var item = new FreshAppItem
                {
                    Name       = reg.Name,
                    WingetId   = hasId ? entry.id  : "",
                    Version    = hasId && entry.ver.Length > 0 ? entry.ver : reg.Version,
                    IsSelected = false,
                    Status     = hasId ? "—" : "— Sans Winget ID",
                };
                _saveItems.Add(item);
                if (hasId) withId++;
            }

            var wingetNote = !wingetAvailable
                ? " (Winget indisponible — aucun ID assigné)"
                : withId == 0
                ? " — aucune app reconnue par le catalogue Winget"
                : $" dont {withId} téléchargeables via Winget";

            TxtHint.Text = $"{regApps.Count} application(s) chargée(s){wingetNote}. Coche celles à inclure dans le ZIP.";
            BtnSaveSelectAll.IsEnabled   = true;
            BtnSaveDeselectAll.IsEnabled = true;
            _main.Log($"Pack Réinstallation : {regApps.Count} app(s) registre, {withId} ID(s) Winget.");
        }

        // ── Registre : toutes les apps installées ────────────────────────────

        private static List<FreshAppItem> LoadFromRegistry()
        {
            var result = new List<FreshAppItem>();
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] paths =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            foreach (var path in paths)
            {
                try
                {
                    using var root = RegistryKey
                        .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(path);
                    if (root == null) continue;
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var k = root.OpenSubKey(sub);
                            if (k == null) continue;
                            var item = RegKeyToFreshItem(k);
                            if (item != null && seen.Add(item.Name))
                                result.Add(item);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root != null)
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var k = root.OpenSubKey(sub);
                            if (k == null) continue;
                            var item = RegKeyToFreshItem(k);
                            if (item != null && seen.Add(item.Name))
                                result.Add(item);
                        }
                        catch { }
                    }
            }
            catch { }

            return result.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static FreshAppItem? RegKeyToFreshItem(RegistryKey k)
        {
            var name = k.GetValue("DisplayName")?.ToString()?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            if (k.GetValue("SystemComponent") is int sc && sc == 1) return null;
            if (Regex.IsMatch(name, @"^KB\d{6,}", RegexOptions.IgnoreCase))        return null;
            if (name.StartsWith("Security Update", StringComparison.OrdinalIgnoreCase)) return null;
            if (name.StartsWith("Update for",      StringComparison.OrdinalIgnoreCase)) return null;
            if (name.StartsWith("Hotfix for",      StringComparison.OrdinalIgnoreCase)) return null;

            var ver = k.GetValue("DisplayVersion")?.ToString()?.Trim() ?? "";
            return new FreshAppItem { Name = name, Version = ver };
        }

        // ── Winget : dictionnaire Nom→(ID, Version) du catalogue ─────────────

        private static (Dictionary<string, (string id, string ver)> map, bool available) BuildWingetIdMap()
        {
            var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var pChk = Process.Start(new ProcessStartInfo("winget", "--version")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                });
                pChk?.WaitForExit(5_000);
                if (pChk == null || pChk.ExitCode != 0) return (map, false);

                // Essai 1 : --source winget (catalogue uniquement, pas les entrées ARP)
                // Essai 2 : sans --source (fallback pour winget < 1.6)
                foreach (var args in new[]
                {
                    "list --source winget --accept-source-agreements --disable-interactivity",
                    "list --accept-source-agreements --disable-interactivity",
                })
                {
                    var raw = RunWingetCommand(args);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        ParseWingetOutputToMap(raw, map);
                        if (map.Count > 0) break;   // on a des résultats → stop
                    }
                }
            }
            catch { return (map, false); }

            return (map, true);
        }

        private static string RunWingetCommand(string arguments)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("winget", arguments)
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    StandardOutputEncoding = Encoding.UTF8,
                });
                if (p == null) return "";
                var t = Task.Run(() => p.StandardOutput.ReadToEnd());
                Task.Run(() => p.StandardError.ReadToEnd());   // drain stderr
                p.WaitForExit(60_000);
                return t.GetAwaiter().GetResult();
            }
            catch { return ""; }
        }

        private static void ParseWingetOutputToMap(string output,
            Dictionary<string, (string id, string ver)> map)
        {
            // Supprimer les codes ANSI (\x1B[...m)
            output = Regex.Replace(output, @"\x1B\[[0-9;]*[a-zA-Z]", "");

            var lines = output.Split('\n');
            int nameCol = -1, idCol = -1, verCol = -1;
            bool past = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');

                // Détection de l'en-tête — insensible à la casse, supporte EN/FR
                if (nameCol < 0)
                {
                    // "Id" / "ID" (case-insensitive)
                    var d = line.IndexOf("Id", StringComparison.OrdinalIgnoreCase);
                    if (d < 4) continue;

                    // "Name" (EN) ou "Nom" (FR)
                    var n = line.IndexOf("Name", StringComparison.OrdinalIgnoreCase);
                    if (n < 0) n = line.IndexOf("Nom", StringComparison.OrdinalIgnoreCase);
                    if (n < 0 || d <= n + 2) continue;

                    // "Version"
                    var v = line.IndexOf("Version", StringComparison.OrdinalIgnoreCase);
                    nameCol = n; idCol = d; verCol = v;
                    continue;
                }

                // Séparateur : -, ─ (U+2500), ― (U+2015)
                if (!past)
                {
                    var t = line.TrimStart();
                    if (t.Length > 0 && (t[0] == '-' || t[0] == '─' || t[0] == '―'))
                        past = true;
                    continue;
                }

                if (line.Length <= idCol) continue;

                var name = line.Substring(nameCol,
                    Math.Min(idCol - nameCol, line.Length - nameCol)).Trim();
                var id = line.Substring(idCol)
                             .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                             .FirstOrDefault() ?? "";

                // Garder uniquement les vraies IDs catalogue winget (ex. "7zip.7zip")
                if (string.IsNullOrEmpty(name)) continue;
                if (!id.Contains('.'))          continue;   // pas une ID catalogue
                if (id.Contains('\\'))          continue;   // entrée ARP registre

                var ver = "";
                if (verCol > 0 && line.Length > verCol)
                {
                    ver = line.Substring(verCol)
                               .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                               .FirstOrDefault() ?? "";
                    if (ver == "N/A" || ver.StartsWith("<", StringComparison.Ordinal)) ver = "";
                }

                map.TryAdd(name, (id, ver));
            }
        }

        // ── Recherche d'un ID Winget par nom dans le catalogue ────────────────
        // Utilisé pendant le téléchargement pour les apps sans ID pré-assigné.

        private static string SearchWingetId(string appName)
        {
            try
            {
                // winget search retourne un tableau similaire à winget list
                var raw = RunWingetCommand(
                    $"search --query \"{appName}\" --count 5 --accept-source-agreements --disable-interactivity");

                if (string.IsNullOrWhiteSpace(raw)) return "";

                raw = Regex.Replace(raw, @"\x1B\[[0-9;]*[a-zA-Z]", "");

                var lines = raw.Split('\n');
                int nameCol = -1, idCol = -1;
                bool past = false;

                foreach (var rawLine in lines)
                {
                    var line = rawLine.TrimEnd('\r');

                    if (nameCol < 0)
                    {
                        var d = line.IndexOf("Id", StringComparison.OrdinalIgnoreCase);
                        if (d < 4) continue;
                        var n = line.IndexOf("Name", StringComparison.OrdinalIgnoreCase);
                        if (n < 0) n = line.IndexOf("Nom", StringComparison.OrdinalIgnoreCase);
                        if (n < 0 || d <= n + 2) continue;
                        nameCol = n; idCol = d;
                        continue;
                    }

                    if (!past)
                    {
                        var t = line.TrimStart();
                        if (t.Length > 0 && (t[0] == '-' || t[0] == '─' || t[0] == '―'))
                            past = true;
                        continue;
                    }

                    if (line.Length <= idCol) continue;

                    var name = line.Substring(nameCol,
                        Math.Min(idCol - nameCol, line.Length - nameCol)).Trim();
                    var id = line.Substring(idCol)
                                 .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                 .FirstOrDefault() ?? "";

                    if (!id.Contains('.') || id.Contains('\\')) continue;

                    // Match exact ou début de nom
                    if (name.Equals(appName, StringComparison.OrdinalIgnoreCase) ||
                        appName.StartsWith(name, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(appName, StringComparison.OrdinalIgnoreCase))
                    {
                        return id;
                    }
                }
            }
            catch { }
            return "";
        }

        // ═════════════════════════════════════════════════════════════════════
        // SAVE — ÉTAPE 2 : TÉLÉCHARGEMENT + ZIP
        // ═════════════════════════════════════════════════════════════════════

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            var selected = _saveItems.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0) return;

            GoToStep(2);

            var dlFolder = Path.Combine(Path.GetTempPath(), $"WMT-Fresh-{DateTime.Now:yyyyMMddHHmmss}");
            Directory.CreateDirectory(dlFolder);

            TxtDlTitle.Text  = "Téléchargement en cours…";
            TxtDlStatus.Text = $"0 / {selected.Count}";
            SetBar(BarDl, 0, selected.Count);

            _main.Log($"Pack Réinstallation : début téléchargement de {selected.Count} app(s).");

            int done = 0;
            foreach (var item in selected)
            {
                // ── Résolution de l'ID Winget ────────────────────────────────
                var wingetId = item.WingetId;

                if (string.IsNullOrEmpty(wingetId))
                {
                    TxtDlApp.Text = $"Recherche catalogue : {item.Name}…";
                    wingetId = await Task.Run(() => SearchWingetId(item.Name));

                    if (!string.IsNullOrEmpty(wingetId))
                    {
                        item.WingetId = wingetId;   // mémoriser pour le manifest
                        _main.Log($"Pack Réinstallation : ID trouvé → {item.Name} = {wingetId}");
                    }
                    else
                    {
                        _main.Log($"Pack Réinstallation : {item.Name} — introuvable dans le catalogue, ignoré.");
                        done++;
                        TxtDlStatus.Text = $"{done} / {selected.Count}";
                        SetBar(BarDl, done, selected.Count);
                        continue;
                    }
                }

                // ── Téléchargement ───────────────────────────────────────────
                TxtDlApp.Text = $"Téléchargement : {item.Name}";
                _main.Log($"Pack Réinstallation : winget download → {item.Name} ({wingetId})");

                await Task.Run(() =>
                {
                    using var p = Process.Start(new ProcessStartInfo(
                        "winget",
                        $"download --id \"{wingetId}\" -l \"{dlFolder}\" --accept-package-agreements --accept-source-agreements")
                    {
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    });
                    if (p == null) return;

                    // Drainer stdout ET stderr obligatoirement — sinon deadlock pipe buffer
                    var outTask = Task.Run(() => p.StandardOutput.ReadToEnd());
                    var errTask = Task.Run(() => p.StandardError.ReadToEnd());
                    p.WaitForExit(120_000);
                    outTask.GetAwaiter().GetResult();
                    errTask.GetAwaiter().GetResult();
                });

                done++;
                TxtDlStatus.Text = $"{done} / {selected.Count}";
                SetBar(BarDl, done, selected.Count);
            }

            // Créer le manifest.json dans le dossier temp
            TxtDlApp.Text = "Création du manifest.json…";
            var manifest = new ManifestData
            {
                Date    = DateTime.Now.ToString("o"),
                Machine = Environment.MachineName,
                Apps    = selected.Select(a => new ManifestEntry
                {
                    Name     = a.Name,
                    WingetId = a.WingetId,
                    Version  = a.Version,
                }).ToList(),
            };
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(dlFolder, "manifest.json"), manifestJson, Encoding.UTF8);

            // Créer le ZIP dans Téléchargements
            TxtDlApp.Text = "Compression du ZIP…";
            var downloads  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var zipName    = $"WMT-FreshInstall-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            _zipPath       = Path.Combine(downloads, "Downloads", zipName);

            bool zipOk = false;
            try
            {
                if (File.Exists(_zipPath)) File.Delete(_zipPath);
                ZipFile.CreateFromDirectory(dlFolder, _zipPath, CompressionLevel.Optimal, false);
                zipOk = true;
            }
            catch (Exception ex)
            {
                _main.Log($"Pack Réinstallation : erreur ZIP — {ex.Message}");
                _zipPath = dlFolder; // fallback : pointer sur le dossier temp
            }

            // Nettoyage dossier temp (si ZIP créé)
            if (zipOk)
            {
                try { Directory.Delete(dlFolder, recursive: true); } catch { }
            }

            // Étape 3 : afficher résultat
            if (zipOk)
            {
                TxtZipTitle.Text = "✓  ZIP prêt !";
                TxtZipTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThOk");
                TxtZipPath.Text        = _zipPath;
                _main.Log($"Pack Réinstallation : ZIP créé → {_zipPath}");
            }
            else
            {
                TxtZipTitle.Text = "✗  Erreur ZIP";
                TxtZipTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
                TxtZipPath.Text        = $"Fichiers dans : {_zipPath}";
            }
            _main.ClearTaskbarProgress();
            GoToStep(3);
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var target = _zipPath;
            if (File.Exists(target))
                { using var _ = Process.Start("explorer.exe", $"/select,\"{target}\""); }
            else if (Directory.Exists(target))
                { using var _ = Process.Start("explorer.exe", target); }
        }

        private void BtnSaveAgain_Click(object sender, RoutedEventArgs e)
        {
            _saveItems.Clear();
            SetMode(PageMode.Save, 1, startScan: true);
        }

        // ═════════════════════════════════════════════════════════════════════
        // RESTORE — ÉTAPE 1 : CHOISIR LE ZIP
        // ═════════════════════════════════════════════════════════════════════

        private void BtnPickZip_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Sélectionner le ZIP de sauvegarde",
                Filter = "ZIP|*.zip",
            };
            if (dlg.ShowDialog() != true) return;

            _zipPath = dlg.FileName;
            TxtPickedZip.Text = dlg.FileName;
            TxtPickedZip.SetResourceReference(TextBlock.ForegroundProperty, "ThOk");
            TxtPickedZip.FontStyle = FontStyles.Normal;
            BtnNextR1.IsEnabled = true;
        }

        private void BtnNextR1_Click(object sender, RoutedEventArgs e) => _ = ReadZipAsync();

        // ═════════════════════════════════════════════════════════════════════
        // RESTORE — ÉTAPE 2 : LECTURE DU MANIFEST
        // ═════════════════════════════════════════════════════════════════════

        private async Task ReadZipAsync()
        {
            GoToStep(2);
            _restoreItems.Clear();

            try
            {
                var manifest = await Task.Run(() =>
                {
                    using var zip = ZipFile.OpenRead(_zipPath);
                    var entry = zip.Entries.FirstOrDefault(e => e.Name == "manifest.json")
                        ?? throw new Exception("manifest.json introuvable dans le ZIP.");

                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var json = reader.ReadToEnd();
                    return JsonSerializer.Deserialize<ManifestData>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new Exception("manifest.json invalide.");
                });

                foreach (var a in manifest.Apps.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _restoreItems.Add(new FreshAppItem
                    {
                        Name       = a.Name,
                        WingetId   = a.WingetId,
                        Version    = a.Version,
                        IsSelected = false,
                        Status     = "—",
                    });
                }

                _main.Log($"Pack Réinstallation : {manifest.Apps.Count} app(s) lues depuis {Path.GetFileName(_zipPath)}.");
                BtnRestSelectAll.IsEnabled  = true;
                BtnRestDeselectAll.IsEnabled = true;
                GoToStep(3);
            }
            catch (Exception ex)
            {
                TxtHint.Text = $"Erreur : {ex.Message}";
                GoToStep(1); // retour si erreur
                _main.Log($"Pack Réinstallation : erreur lecture ZIP — {ex.Message}");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // RESTORE — ÉTAPE 4 : INSTALLATION
        // ═════════════════════════════════════════════════════════════════════

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            var toInstall = _restoreItems.Where(i => i.IsSelected).ToList();
            if (toInstall.Count == 0) return;

            _cts = new CancellationTokenSource();
            GoToStep(4);
            TxtInstTitle.Text = "Installation en cours…";
            TxtInstTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThTextTitle");
            TxtInstStatus.Text = $"0 / {toInstall.Count}";
            SetBar(BarInst, 0, toInstall.Count);
            BtnCancelInst.IsEnabled = true;

            // Extraire le ZIP dans un dossier temp
            var extractDir = Path.Combine(Path.GetTempPath(), $"WMT-Restore-{DateTime.Now:yyyyMMddHHmmss}");
            bool extracted = false;
            try
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(_zipPath, extractDir));
                extracted = true;
            }
            catch { }

            foreach (var item in toInstall)
            {
                item.Status = "En attente";
            }

            int done = 0;
            foreach (var item in toInstall)
            {
                if (_cts.Token.IsCancellationRequested) { item.Status = "— Annulé"; continue; }

                item.Status         = "En cours…";
                TxtInstApp.Text     = $"Installation : {item.Name}";
                SetBar(BarInst, done, toInstall.Count);

                var (result, debug) = await Task.Run(() =>
                    InstallApp(item, extracted ? extractDir : null, _cts.Token));

                item.Status = result switch
                {
                    InstallResult.Ok        => "✓ Installé",
                    InstallResult.Already   => "✓ Déjà présent",
                    InstallResult.Cancelled => "— Annulé",
                    _                       => "✗ Échec",
                };
                _main.Log($"Pack Réinstallation : [{item.Status}] {item.Name} — {debug}");
                done++;
                TxtInstStatus.Text = $"{done} / {toInstall.Count}";
            }

            SetBar(BarInst, done, toInstall.Count);

            int failed    = toInstall.Count(i => i.Status.StartsWith("✗"));
            int succeeded = toInstall.Count(i => i.Status.StartsWith("✓"));

            if (_cts.Token.IsCancellationRequested)
            {
                TxtInstTitle.Text       = $"Annulé — {done} / {toInstall.Count} traitée(s).";
                TxtInstTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
            }
            else if (failed == 0)
            {
                TxtInstTitle.Text       = "✓  Tout est installé !";
                TxtInstTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThOk");
            }
            else if (succeeded == 0)
            {
                TxtInstTitle.Text       = $"✗  Échec — {failed} application(s) non installée(s).";
                TxtInstTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
            }
            else
            {
                TxtInstTitle.Text       = $"⚠  Partiel — {succeeded} OK, {failed} échec(s).";
                TxtInstTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThWarn");
            }

            TxtInstApp.Text         = "";
            BtnCancelInst.IsEnabled = false;
            _main.ClearTaskbarProgress();

            // Nettoyage dossier temp
            if (extracted) try { Directory.Delete(extractDir, recursive: true); } catch { }

            _main.Log($"Pack Réinstallation : terminé — {succeeded} OK, {failed} échec(s), {done} traitée(s).");
        }

        private void BtnCancelInst_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            BtnCancelInst.IsEnabled = false;
            TxtInstApp.Text = "Annulation en cours…";
        }

        // ═════════════════════════════════════════════════════════════════════
        // INSTALLATION D'UNE APP (depuis fichier local ou winget)
        // ═════════════════════════════════════════════════════════════════════

        private enum InstallResult { Ok, Already, Failed, Cancelled }

        private static (InstallResult result, string debug) InstallApp(
            FreshAppItem item, string? extractDir, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return (InstallResult.Cancelled, "annulé");
            var log = new StringBuilder();

            // codes winget « déjà installé / rien à faire »
            static bool IsAlready(int ec) => ec == -1978335109 || ec == -1978335146 || ec == -1978335189;

            // ── 1. winget install DÉ-ÉLEVÉ (via cmd) — gère le user-scope ─────
            //    winget connaît les bons switches silencieux par paquet et installe
            //    Discord/Spotify dans le bon profil utilisateur (pas en admin).
            if (!string.IsNullOrEmpty(item.WingetId))
            {
                var args = $"/c winget install --id \"{item.WingetId}\" --silent " +
                           "--accept-package-agreements --accept-source-agreements --disable-interactivity";
                try
                {
                    int ec = DeElevatedLauncher.StartAndWait(
                        Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                        args, 300_000, null);
                    log.Append($"winget-userctx ec={ec}; ");
                    if (ec == 0)            return (InstallResult.Ok,      log.ToString());
                    if (IsAlready(ec))      return (InstallResult.Already, log.ToString());
                }
                catch (Exception ex) { log.Append($"winget-userctx err={ex.Message}; "); }
            }

            if (ct.IsCancellationRequested) return (InstallResult.Cancelled, log + "annulé");

            // ── 2. winget install ÉLEVÉ (apps machine-scope qui exigent admin) ─
            if (!string.IsNullOrEmpty(item.WingetId))
            {
                foreach (var scope in new[] { "", "--scope machine" })
                {
                    if (ct.IsCancellationRequested) return (InstallResult.Cancelled, log + "annulé");
                    try
                    {
                        var scopeArg = scope.Length > 0 ? $" {scope}" : "";
                        using var p = Process.Start(new ProcessStartInfo(
                            "winget",
                            $"install --id \"{item.WingetId}\" --silent{scopeArg} " +
                            "--accept-package-agreements --accept-source-agreements --disable-interactivity")
                        {
                            UseShellExecute        = false,
                            CreateNoWindow         = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError  = true,
                            StandardOutputEncoding = Encoding.UTF8,
                        });
                        if (p == null) continue;

                        var outTask = Task.Run(() => p.StandardOutput.ReadToEnd());
                        Task.Run(() => p.StandardError.ReadToEnd());

                        while (!p.WaitForExit(500))
                        {
                            if (!ct.IsCancellationRequested) continue;
                            try { p.Kill(entireProcessTree: true); } catch { }
                            return (InstallResult.Cancelled, log + "annulé");
                        }

                        var stdout = outTask.GetAwaiter().GetResult();
                        var ec     = p.ExitCode;
                        log.Append($"winget-admin{(scope.Length > 0 ? "(machine)" : "")} ec={ec}; ");

                        if (ec == 0)       return (InstallResult.Ok,      log.ToString());
                        if (IsAlready(ec)) return (InstallResult.Already, log.ToString());
                        if (stdout.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
                            stdout.Contains("déjà install",       StringComparison.OrdinalIgnoreCase))
                            return (InstallResult.Already, log.ToString());
                    }
                    catch (Exception ex) { log.Append($"winget-admin err={ex.Message}; "); }
                }
            }

            // ── 3. Installeur local du ZIP (dé-élevé), dernier recours ────────
            if (extractDir != null && Directory.Exists(extractDir))
            {
                var idPrefix = item.WingetId.Contains('.') ? item.WingetId.Split('.')[0] : item.WingetId;
                var subDir = Directory.GetDirectories(extractDir, "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(d =>
                    {
                        var fn = Path.GetFileName(d);
                        return fn.StartsWith(idPrefix,      StringComparison.OrdinalIgnoreCase)
                            || fn.StartsWith(item.WingetId, StringComparison.OrdinalIgnoreCase);
                    });
                var searchRoot = subDir ?? extractDir;

                foreach (var pat in new[] { "*.exe", "*.msi", "*.msix", "*.msixbundle" })
                {
                    var files = Directory.GetFiles(searchRoot, pat, SearchOption.AllDirectories);
                    if (files.Length == 0) continue;

                    var (res, dbg) = RunLocalInstaller(files[0], ct);
                    log.Append($"local[{dbg}]; ");
                    if (res == InstallResult.Ok || res == InstallResult.Cancelled)
                        return (res, log.ToString());
                    break;
                }
            }
            else log.Append("pas d'installeur local; ");

            return (InstallResult.Failed, log.ToString());
        }

        /// <summary>
        /// Lance un installeur local avec les bons flags silencieux selon son type.
        /// Les .exe sont lancés DÉ-ÉLEVÉS (contexte utilisateur) pour gérer les apps
        /// user-scope comme Discord/Spotify qui refusent l'exécution administrateur.
        /// </summary>
        private static (InstallResult result, string debug) RunLocalInstaller(string file, CancellationToken ct)
        {
            var ext  = Path.GetExtension(file).ToLowerInvariant();
            var name = Path.GetFileName(file);
            var dir  = Path.GetDirectoryName(file);

            try
            {
                // ── MSI : per-machine → msiexec élevé, silencieux ────────────
                if (ext == ".msi")
                {
                    using var p = Process.Start(new ProcessStartInfo(
                        "msiexec", $"/i \"{file}\" /qn /norestart")
                    { UseShellExecute = true });
                    p?.WaitForExit(180_000);
                    if (ct.IsCancellationRequested) return (InstallResult.Cancelled, "annulé");
                    return (p?.ExitCode == 0 ? InstallResult.Ok : InstallResult.Failed,
                            $"msiexec {name} ec={p?.ExitCode}");
                }

                // ── MSIX / APPX : Add-AppxPackage ────────────────────────────
                if (ext == ".msix" || ext == ".msixbundle")
                {
                    using var p = Process.Start(new ProcessStartInfo(
                        "powershell",
                        $"-NoProfile -NonInteractive -Command \"Add-AppxPackage -Path '{file}'\"")
                    { UseShellExecute = false, CreateNoWindow = true });
                    p?.WaitForExit(180_000);
                    if (ct.IsCancellationRequested) return (InstallResult.Cancelled, "annulé");
                    return (p?.ExitCode == 0 ? InstallResult.Ok : InstallResult.Failed,
                            $"Add-AppxPackage {name} ec={p?.ExitCode}");
                }

                // ── EXE : essais silencieux DÉ-ÉLEVÉS ────────────────────────
                // --silent : Squirrel (Discord), Inno setup
                // /S       : NSIS
                // /silent  : Inno
                // /verysilent : Inno
                foreach (var flag in new[] { "--silent", "/S", "/silent", "/verysilent" })
                {
                    if (ct.IsCancellationRequested) return (InstallResult.Cancelled, "annulé");
                    try
                    {
                        int ec = DeElevatedLauncher.StartAndWait(file, flag, 120_000, dir);
                        if (ec == 0)
                            return (InstallResult.Ok, $"local dé-élevé {name} {flag} ec=0");
                        // ec == -1 (timeout, GUI restée ouverte) ou ec != 0 → flag suivant
                    }
                    catch
                    {
                        // Le token trick a échoué → tenter en élevé silencieux
                        try
                        {
                            using var p = Process.Start(new ProcessStartInfo(file, flag)
                            { UseShellExecute = true });
                            p?.WaitForExit(120_000);
                            if (p != null && p.HasExited && p.ExitCode == 0)
                                return (InstallResult.Ok, $"local élevé {name} {flag} ec=0");
                        }
                        catch { }
                    }
                }

                return (InstallResult.Failed, $"local {name} : aucun flag silencieux n'a réussi");
            }
            catch (Exception ex) { return (InstallResult.Failed, $"local {name} erreur: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // SÉLECTION
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cliquer N'IMPORTE OÙ sur une ligne coche/décoche sa case (demande utilisateur :
        /// forcer le clic dans la petite case n'était pas naturel). Garde anti-double-toggle :
        /// si le clic vient de la CheckBox elle-même, elle gère déjà son état → on ne fait rien.
        /// Branché en PreviewMouseLeftButtonUp sur DgSaveApps ET DgRestApps.
        /// </summary>
        private void Dg_RowClickToggle(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            // Un clic sur du texte peut remonter un ContentElement (Run) : non-visuel →
            // VisualTreeHelper lèverait. On rejoint d'abord l'arbre visuel par le parent logique.
            while (src is System.Windows.ContentElement ce)
                src = System.Windows.LogicalTreeHelper.GetParent(ce);
            for (var d = src; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            {
                if (d is CheckBox) return;                                        // la case gère déjà son clic
                if (d is System.Windows.Controls.Primitives.DataGridColumnHeader) return; // tri en-tête : ne pas cocher
                if (d is DataGridRow row && row.Item is FreshAppItem item)
                {
                    item.IsSelected = !item.IsSelected;
                    return;
                }
            }
        }

        private void BtnSaveSelectAll_Click(object s, RoutedEventArgs e)
        { foreach (var i in _saveItems)    i.IsSelected = true; }

        private void BtnSaveDeselectAll_Click(object s, RoutedEventArgs e)
        { foreach (var i in _saveItems)    i.IsSelected = false; }

        private void BtnRestSelectAll_Click(object s, RoutedEventArgs e)
        { foreach (var i in _restoreItems) i.IsSelected = true; }

        private void BtnRestDeselectAll_Click(object s, RoutedEventArgs e)
        { foreach (var i in _restoreItems) i.IsSelected = false; }

        // ═════════════════════════════════════════════════════════════════════
        // HELPERS
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateSaveButton()
        {
            var n = _saveItems.Count(i => i.IsSelected);
            BtnDownload.IsEnabled = n > 0;
            BtnDownload.Content   = $"TÉLÉCHARGER ET ZIPPER ({n})";
        }

        private void UpdateInstallButton()
        {
            var n = _restoreItems.Count(i => i.IsSelected);
            BtnInstall.IsEnabled = n > 0;
            BtnInstall.Content   = $"INSTALLER ({n})";
        }

        private void SetBar(Grid bar, int done, int total)
        {
            var pct = total > 0 ? Math.Min(100.0, done * 100.0 / total) : 0;
            bar.ColumnDefinitions[0].Width = new GridLength(pct,       GridUnitType.Star);
            bar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
            // Sync avec la barre des tâches Windows
            if (done > 0) _main.SetTaskbarProgress(pct);
        }
    }
}
