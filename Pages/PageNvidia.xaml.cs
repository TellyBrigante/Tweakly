using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public sealed class NipSetting
    {
        public string Name  { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public partial class PageNvidia : UserControl
    {
        private readonly MainWindow _main;
        private bool _appTabLoaded;   // l'onglet « par application » ne se peuple qu'au 1er clic (zéro activité au chargement → pas de churn/flash)

        public PageNvidia(MainWindow main)
        {
            _main = main;
            InitializeComponent();
            // État d'onglet figé AVANT le 1er rendu (sinon flash « par application »
            // le temps que UserControl_Loaded s'exécute). ApplyTemplate pour que
            // StyleTab trouve les éléments du template dès le constructeur.
            try { BtnTabGlobal.ApplyTemplate(); BtnTabApp.ApplyTemplate(); SelectTab(true); } catch { }
        }

        // La page est mise en CACHE (instance Lazy unique). En quittant la page, on remet
        // l'onglet sur « global » → au retour, la page est DÉJÀ sur global avant tout rendu,
        // donc le panneau « par application » ne peut pas flasher (sinon l'état mis en cache
        // s'affichait jusqu'à ce que Loaded — POST-rendu — le corrige).
        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            try { SelectTab(true); } catch { }   // au retour, la page est déjà sur l'onglet global
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            SelectTab(true);
            // RefreshProfiles() retiré d'ici : l'onglet « par application » est paresseux
            // (peuplé au 1er clic). Au chargement, AUCUNE activité ne touche AppPanel.
            await LoadGlobalEditorAsync();
        }

        // ── Onglets Global / Par application ───────────────────────────────────
        private void BtnTabGlobal_Click(object sender, RoutedEventArgs e) => SelectTab(true);
        private void BtnTabApp_Click(object sender, RoutedEventArgs e)
        {
            if (!_appTabLoaded) { _appTabLoaded = true; RefreshProfiles(); }
            SelectTab(false);
        }

        private void SelectTab(bool global)
        {
            GlobalPanel.Visibility = global ? Visibility.Visible : Visibility.Collapsed;
            AppPanel.Visibility    = global ? Visibility.Collapsed : Visibility.Visible;
            StyleTab(BtnTabGlobal, global);
            StyleTab(BtnTabApp, !global);
        }

        private static void StyleTab(Button btn, bool active)
        {
            btn.ApplyTemplate();
            if (btn.Template.FindName("Bg", btn) is not Border bg) return;
            if (btn.Template.FindName("Lbl", btn) is not TextBlock lbl) return;
            if (active)
            {
                bg.SetResourceReference(Border.BackgroundProperty, "ThTabSel");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ThWhite");
            }
            else
            {
                bg.Background = Brushes.Transparent;
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            }
        }

        // ── Éditeur des paramètres GLOBAUX (NVAPI, lecture + écriture) ─────────
        private readonly List<(NvCatalogEntry entry, ComboBox combo)> _globalRows = new();

        private async Task LoadGlobalEditorAsync()
        {
            // STRUCTURE d'abord, SYNCHRONE (depuis le Catalog STATIQUE) → la page n'est JAMAIS
            // vide : dès la 1re frame on voit les catégories + noms de réglages, donc plus aucun
            // état « Lecture en cours… » épars qui flashe avant les réglages (cause du bug vécu :
            // la lecture NvAPI à froid prend une fraction de seconde). Les VALEURS sont remplies
            // juste après, en async.
            BuildGlobalSkeleton();
            try
            {
                var cur = await Task.Run(() => NvidiaDriverSettings.ReadCurrentValues());
                if (cur.Count == 0)
                {
                    TxtCurrentStatus.Text = "GPU NVIDIA non détecté (ou pilote incompatible).";
                    BtnApplyGlobal.IsEnabled = false;
                    return;
                }
                TxtCurrentStatus.Text = "Modifie un réglage puis clique « Appliquer ». Écrit directement dans le pilote.";
                BtnApplyGlobal.IsEnabled = true;
                FillGlobalValues(cur);
            }
            catch (Exception ex)
            {
                TxtCurrentStatus.Text = "Impossible de lire les réglages du pilote.";
                _main.Log($"Nvidia : erreur lecture pilote — {ex.Message}");
            }
        }

        // Construit toutes les rangées (en-têtes + noms + listes) depuis le Catalog statique,
        // SANS toucher au pilote → instantané, page jamais vide. Les listes restent désactivées
        // tant que les valeurs réelles ne sont pas lues (on ne modifie pas un état périmé).
        private void BuildGlobalSkeleton()
        {
            CurrentSettingsPanel.Children.Clear();
            _globalRows.Clear();
            string? lastCat = null;
            foreach (var e in NvidiaDriverSettings.Catalog)
            {
                if (e.Category != lastCat)
                {
                    lastCat = e.Category;
                    var catHdr = new TextBlock
                    {
                        Text       = e.Category.ToUpperInvariant(),
                        FontFamily = (FontFamily)FindResource("AppFont"),
                        FontSize   = 10.5, FontWeight = FontWeights.SemiBold,
                        Margin     = new Thickness(2, CurrentSettingsPanel.Children.Count == 0 ? 0 : 14, 0, 6),
                    };
                    catHdr.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
                    CurrentSettingsPanel.Children.Add(catHdr);
                }

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });

                var nameTb = new TextBlock
                {
                    Text = e.Name, FontFamily = (FontFamily)FindResource("AppFont"), FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0),
                };
                nameTb.SetResourceReference(TextBlock.ForegroundProperty, "ThTextBody");
                grid.Children.Add(nameTb);

                var combo = new ComboBox
                {
                    Style = (Style)FindResource("DarkComboStyle"),
                    ItemsSource = new List<NvOption>(e.Options),
                    VerticalAlignment = VerticalAlignment.Center, MaxDropDownHeight = 240,
                    Tag = (uint)0,        // valeur courante du pilote, remplie après lecture
                    IsEnabled = false,    // réactivé une fois la valeur réelle connue
                };
                Grid.SetColumn(combo, 1);
                grid.Children.Add(combo);
                _globalRows.Add((e, combo));

                var rowB = new Border
                {
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding         = new Thickness(2, 8, 2, 8),
                    Child           = grid,
                };
                rowB.SetResourceReference(Border.BorderBrushProperty, "ThBorder");
                CurrentSettingsPanel.Children.Add(rowB);
            }
        }

        // Remplit la sélection de chaque liste avec la valeur réellement lue dans le pilote.
        private void FillGlobalValues(Dictionary<uint, uint> cur)
        {
            foreach (var (entry, combo) in _globalRows)
            {
                cur.TryGetValue(entry.Id, out var curVal);
                var opts  = new List<NvOption>(entry.Options);
                var match = opts.Find(o => o.Value == curVal);
                if (match == null)
                {
                    match = new NvOption(curVal, $"(actuel : {curVal})");
                    opts.Insert(0, match);
                }
                combo.ItemsSource  = opts;
                combo.SelectedItem = match;
                combo.Tag          = curVal;
                combo.IsEnabled    = true;
            }
        }

        private async void BtnApplyGlobal_Click(object sender, RoutedEventArgs e)
        {
            var changes = new Dictionary<uint, uint>();
            foreach (var (entry, combo) in _globalRows)
            {
                uint cur = combo.Tag is uint t ? t : 0u;
                if (combo.SelectedItem is NvOption opt && opt.Value != cur)
                    changes[entry.Id] = opt.Value;
            }

            if (changes.Count == 0) { _main.Log("Nvidia : aucun changement à appliquer."); return; }

            BtnApplyGlobal.IsEnabled = false;
            _main.Log($"Nvidia : application de {changes.Count} réglage(s) au pilote…");
            var ok = await Task.Run(() => NvidiaDriverSettings.WriteGlobal(changes));
            _main.Log(ok ? "Nvidia : réglages appliqués au pilote." : "Nvidia : échec de l'écriture.");
            if (ok) await LoadGlobalEditorAsync();   // recharge l'état réel du pilote
            BtnApplyGlobal.IsEnabled = true;
        }

        // ── Exporter le profil global en .nip (création de profil perso) ───────
        private void BtnExportGlobal_Click(object sender, RoutedEventArgs e)
        {
            var values = new Dictionary<uint, uint>();
            foreach (var (entry, combo) in _globalRows)
                if (combo.SelectedItem is NvOption opt) values[entry.Id] = opt.Value;

            if (values.Count == 0) { _main.Log("Nvidia : rien à exporter (pilote indisponible)."); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter           = "Profil NVIDIA Inspector (*.nip)|*.nip",
                FileName         = "MonProfil.nip",
                InitialDirectory = Helpers.PathLayout.NipFolder,
                Title            = "Exporter le profil global en .nip",
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var xml = NvidiaDriverSettings.BuildGlobalNip(values);
                File.WriteAllText(dlg.FileName, xml, System.Text.Encoding.Unicode);
                _main.Log($"Nvidia : profil exporté → {dlg.FileName}");
                RefreshProfiles();   // réapparaît dans la liste s'il est sauvé dans data/
            }
            catch (Exception ex) { _main.Log($"Nvidia : erreur d'export — {ex.Message}"); }
        }

        // ── Profils NIP ───────────────────────────────────────────────────────

        private void RefreshProfiles()
        {
            ComboProfils.Items.Clear();
            TxtNvStatus.Text = "";

            var dataFolder = Helpers.PathLayout.NipFolder;
            if (!Directory.Exists(dataFolder))
            {
                TxtNvStatus.Text = "Dossier 'data' introuvable à côté de l'exécutable.";
                BtnAppliquer.IsEnabled = false;
                return;
            }

            foreach (var f in Directory.EnumerateFiles(dataFolder, "*.nip"))
                ComboProfils.Items.Add(Path.GetFileName(f));

            if (ComboProfils.Items.Count > 0)
            {
                ComboProfils.SelectedIndex = 0;
                BtnAppliquer.IsEnabled = true;
            }
            else
            {
                TxtNvStatus.Text = "Aucun profil .nip trouvé dans data\\nip\\.";
                BtnAppliquer.IsEnabled = false;
            }
        }

        // ── Récap du profil sélectionné ────────────────────────────────────────

        private void ComboProfils_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = ComboProfils.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selected)) { RecapCard.Visibility = Visibility.Collapsed; return; }

            var path = Path.Combine(Helpers.PathLayout.NipFolder, selected);
            if (!File.Exists(path)) { RecapCard.Visibility = Visibility.Collapsed; return; }

            try
            {
                var doc = XDocument.Load(path);

                // Premier <Profile> (insensible au namespace via LocalName)
                var profile = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Profile");
                if (profile == null) { RecapCard.Visibility = Visibility.Collapsed; return; }

                string Local(XElement p, string n) =>
                    p.Elements().FirstOrDefault(x => x.Name.LocalName == n)?.Value?.Trim() ?? "";

                var profName = Local(profile, "ProfileName");
                if (string.IsNullOrEmpty(profName)) profName = Path.GetFileNameWithoutExtension(selected);

                // Exécutables associés
                var exes = profile.Descendants()
                    .Where(x => x.Name.LocalName == "string" || x.Name.LocalName == "Executeable")
                    .Select(x => x.Value.Trim())
                    .Where(v => v.Length > 0)
                    .Distinct()
                    .ToList();

                // Réglages
                var settings = new List<NipSetting>();
                foreach (var ps in profile.Descendants().Where(x => x.Name.LocalName == "ProfileSetting"))
                {
                    var name = Local(ps, "SettingNameInfo");
                    var id   = Local(ps, "SettingID");
                    var val  = Local(ps, "SettingValue");

                    if (string.IsNullOrEmpty(name))
                        name = id.Length > 0 ? $"Réglage #{id}" : "Réglage inconnu";

                    // Valeur : afficher en hex compact si numérique long
                    var shown = val;
                    if (ulong.TryParse(val, out var num))
                        shown = "0x" + num.ToString("X");

                    settings.Add(new NipSetting { Name = name, Value = shown });
                }

                settings = settings.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

                TxtRecapTitle.Text = profName;
                var exeInfo = exes.Count > 0 ? $"  ·  {exes.Count} exécutable(s)" : "";
                TxtRecapSub.Text = $"{settings.Count} réglage(s){exeInfo}";
                RecapList.ItemsSource = settings;
                RecapCard.Visibility = Visibility.Visible;
            }
            catch
            {
                RecapCard.Visibility = Visibility.Collapsed;
            }
        }

        // ── Appliquer ─────────────────────────────────────────────────────────

        private async void BtnAppliquer_Click(object sender, RoutedEventArgs e)
        {
            var selected = ComboProfils.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selected))
            {
                _main.Log("Nvidia : aucun profil sélectionné.");
                return;
            }

            var inspectorPath = Helpers.PathLayout.NvidiaInspector;
            var profilePath   = Path.Combine(Helpers.PathLayout.NipFolder, selected);

            if (!File.Exists(inspectorPath))
            {
                _main.Log("Nvidia : nvidiaProfileInspector.exe introuvable dans data\\tools\\.");
                return;
            }
            if (!File.Exists(profilePath))
            {
                _main.Log($"Nvidia : profil '{selected}' introuvable.");
                return;
            }

            BtnAppliquer.IsEnabled = false;
            _main.Log($"Nvidia : application du profil '{selected}'…");

            var ok = await Task.Run(() =>
            {
                try
                {
                    using IDisposable inspectorLease = Helpers.BundledFileTrust.OpenVerifiedLease(inspectorPath);
                    using var p = Process.Start(new ProcessStartInfo(
                        inspectorPath, $"-importProfile \"{profilePath}\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    });
                    p?.WaitForExit(30_000);
                    return true;
                }
                catch { return false; }
            });

            _main.Log(ok
                ? $"Nvidia : profil '{selected}' appliqué avec succès."
                : "Nvidia : erreur lors de l'application du profil.");

            BtnAppliquer.IsEnabled = true;
        }
    }
}
