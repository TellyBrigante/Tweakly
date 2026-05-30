using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;

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

        public PageNvidia(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshProfiles();
        }

        // ── Profils NIP ───────────────────────────────────────────────────────

        private void RefreshProfiles()
        {
            ComboProfils.Items.Clear();
            TxtNvStatus.Text = "";

            var dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
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
                TxtNvStatus.Text = "Aucun profil .nip trouvé dans le dossier data/.";
                BtnAppliquer.IsEnabled = false;
            }
        }

        // ── Récap du profil sélectionné ────────────────────────────────────────

        private void ComboProfils_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = ComboProfils.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selected)) { RecapCard.Visibility = Visibility.Collapsed; return; }

            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", selected);
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

            var exeDir      = AppDomain.CurrentDomain.BaseDirectory;
            var inspectorPath = Path.Combine(exeDir, "data", "nvidiaProfileInspector.exe");
            var profilePath   = Path.Combine(exeDir, "data", selected);

            if (!File.Exists(inspectorPath))
            {
                _main.Log("Nvidia : nvidiaProfileInspector.exe introuvable dans data/.");
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
