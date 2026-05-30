using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Optimisation_Tool.Pages
{
    public partial class PagePrivacy : UserControl
    {
        private readonly MainWindow _main;
        private bool _loaded = false;

        public PagePrivacy(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded) return;
            _loaded = true;
            await LoadStateAsync();
        }

        // ── Lecture état ──────────────────────────────────────────────────────

        private async Task LoadStateAsync()
        {
            BtnAppliquer.IsEnabled = false;

            var s = await Task.Run(ReadState);

            ChkTelemetrie.IsChecked      = s.Telemetrie;
            ChkAdID.IsChecked            = s.AdID;
            ChkActivityHistory.IsChecked = s.ActivityHistory;
            ChkBingSearch.IsChecked      = s.BingSearch;
            ChkInputPersonal.IsChecked   = s.InputPersonal;
            ChkLocation.IsChecked        = s.Location;
            ChkWER.IsChecked             = s.WER;
            ChkTailoredExp.IsChecked     = s.TailoredExp;
            ChkCompatTel.IsChecked       = s.CompatTel;

            BtnAppliquer.IsEnabled = true;
            _main.Log("Confidentialité : état chargé.");
        }

        private static (bool Telemetrie, bool AdID, bool ActivityHistory, bool BingSearch,
                        bool InputPersonal, bool Location, bool WER,
                        bool TailoredExp, bool CompatTel) ReadState()
        {
            // Télémétrie (AllowTelemetry = 0 ET service DiagTrack désactivé)
            bool telemetrie = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                    "AllowTelemetry", null);
                bool policySet = v != null && Convert.ToInt32(v) == 0;
                bool svcDisabled = IsSvcDisabled("DiagTrack");
                telemetrie = policySet && svcDisabled;
            }
            catch { }

            // Identifiant publicitaire
            bool adID = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                    "Enabled", null);
                adID = v != null && Convert.ToInt32(v) == 0;
            }
            catch { }

            // Historique d'activité
            bool activityHistory = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                    "EnableActivityFeed", null);
                activityHistory = v != null && Convert.ToInt32(v) == 0;
            }
            catch { }

            // Bing Search désactivé
            bool bingSearch = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
                    "BingSearchEnabled", null);
                bingSearch = v != null && Convert.ToInt32(v) == 0;
            }
            catch { }

            // Input Personalization
            bool inputPersonal = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\InputPersonalization",
                    "RestrictImplicitInkCollection", null);
                inputPersonal = v != null && Convert.ToInt32(v) == 1;
            }
            catch { }

            // Localisation
            bool location = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                    "DisableLocation", null);
                location = v != null && Convert.ToInt32(v) == 1;
            }
            catch { }

            // WER
            bool wer = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
                    "Disabled", null);
                bool policySet = v != null && Convert.ToInt32(v) == 1;
                bool svcDisabled = IsSvcDisabled("WerSvc");
                wer = policySet && svcDisabled;
            }
            catch { }

            // Tailored Experiences
            bool tailoredExp = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy",
                    "TailoredExperiencesWithDiagnosticDataEnabled", null);
                tailoredExp = v != null && Convert.ToInt32(v) == 0;
            }
            catch { }

            // CompatTel / AppCompat
            bool compatTel = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppCompat",
                    "DisableInventory", null);
                compatTel = v != null && Convert.ToInt32(v) == 1;
            }
            catch { }

            return (telemetrie, adID, activityHistory, bingSearch,
                    inputPersonal, location, wer, tailoredExp, compatTel);
        }

        // ── Appliquer ─────────────────────────────────────────────────────────

        private async void BtnAppliquer_Click(object sender, RoutedEventArgs e)
        {
            BtnAppliquer.IsEnabled = false;

            bool doTel      = ChkTelemetrie.IsChecked      == true;
            bool doAdID     = ChkAdID.IsChecked            == true;
            bool doActivity = ChkActivityHistory.IsChecked == true;
            bool doBing     = ChkBingSearch.IsChecked      == true;
            bool doInk      = ChkInputPersonal.IsChecked   == true;
            bool doLoc      = ChkLocation.IsChecked        == true;
            bool doWER      = ChkWER.IsChecked             == true;
            bool doTailored = ChkTailoredExp.IsChecked     == true;
            bool doCompat   = ChkCompatTel.IsChecked       == true;

            _main.Log("Confidentialité : application des paramètres…");

            await Task.Run(() =>
                ApplyChanges(doTel, doAdID, doActivity, doBing, doInk,
                             doLoc, doWER, doTailored, doCompat,
                             msg => _main.Log(msg)));

            _main.Log("Confidentialité : paramètres appliqués.");
            BtnAppliquer.IsEnabled = true;
        }

        private static void ApplyChanges(
            bool doTel, bool doAdID, bool doActivity, bool doBing, bool doInk,
            bool doLoc, bool doWER, bool doTailored, bool doCompat,
            Action<string> log)
        {
            // Télémétrie
            try
            {
                const string pathDC =
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection";
                if (doTel)
                {
                    Registry.SetValue(pathDC, "AllowTelemetry", 0, RegistryValueKind.DWord);
                    SetSvc("DiagTrack",          disabled: true);
                    SetSvc("dmwappushservice",   disabled: true);
                    log("Télémétrie Windows : DÉSACTIVÉE.");
                }
                else
                {
                    Registry.SetValue(pathDC, "AllowTelemetry", 3, RegistryValueKind.DWord);
                    SetSvc("DiagTrack",          disabled: false);
                    SetSvc("dmwappushservice",   disabled: false);
                    log("Télémétrie Windows : RESTAURÉE (par défaut).");
                }
            }
            catch (Exception ex) { log($"Télémétrie : erreur — {ex.Message}"); }

            // Identifiant publicitaire
            try
            {
                Registry.SetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                    "Enabled", doAdID ? 0 : 1, RegistryValueKind.DWord);
                log($"Identifiant publicitaire : {(doAdID ? "DÉSACTIVÉ" : "ACTIVÉ")}.");
            }
            catch (Exception ex) { log($"AdvertisingInfo : erreur — {ex.Message}"); }

            // Historique d'activité
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                    "EnableActivityFeed", doActivity ? 0 : 1, RegistryValueKind.DWord);
                log($"Historique d'activité : {(doActivity ? "DÉSACTIVÉ" : "ACTIVÉ")}.");
            }
            catch (Exception ex) { log($"ActivityFeed : erreur — {ex.Message}"); }

            // Bing Search
            try
            {
                Registry.SetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
                    "BingSearchEnabled", doBing ? 0 : 1, RegistryValueKind.DWord);
                log($"Recherche Bing : {(doBing ? "DÉSACTIVÉE" : "ACTIVÉE")}.");
            }
            catch (Exception ex) { log($"BingSearch : erreur — {ex.Message}"); }

            // Input Personalization
            try
            {
                const string pathInk =
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\InputPersonalization";
                Registry.SetValue(pathInk, "RestrictImplicitInkCollection",
                    doInk ? 1 : 0, RegistryValueKind.DWord);
                Registry.SetValue(pathInk, "RestrictImplicitTextCollection",
                    doInk ? 1 : 0, RegistryValueKind.DWord);
                log($"Personnalisation saisie : {(doInk ? "DÉSACTIVÉE" : "ACTIVÉE")}.");
            }
            catch (Exception ex) { log($"InputPersonalization : erreur — {ex.Message}"); }

            // Localisation
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                    "DisableLocation", doLoc ? 1 : 0, RegistryValueKind.DWord);
                log($"Localisation : {(doLoc ? "DÉSACTIVÉE" : "ACTIVÉE")}.");
            }
            catch (Exception ex) { log($"Localisation : erreur — {ex.Message}"); }

            // WER
            try
            {
                const string pathWER =
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting";
                if (doWER)
                {
                    Registry.SetValue(pathWER, "Disabled", 1, RegistryValueKind.DWord);
                    SetSvc("WerSvc", disabled: true);
                    log("Rapport d'erreurs Windows : DÉSACTIVÉ.");
                }
                else
                {
                    Registry.SetValue(pathWER, "Disabled", 0, RegistryValueKind.DWord);
                    SetSvc("WerSvc", disabled: false);
                    log("Rapport d'erreurs Windows : ACTIVÉ (par défaut).");
                }
            }
            catch (Exception ex) { log($"WER : erreur — {ex.Message}"); }

            // Tailored Experiences
            try
            {
                Registry.SetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy",
                    "TailoredExperiencesWithDiagnosticDataEnabled",
                    doTailored ? 0 : 1, RegistryValueKind.DWord);
                log($"Tailored Experiences : {(doTailored ? "DÉSACTIVÉES" : "ACTIVÉES")}.");
            }
            catch (Exception ex) { log($"TailoredExperiences : erreur — {ex.Message}"); }

            // CompatTel
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppCompat",
                    "DisableInventory", doCompat ? 1 : 0, RegistryValueKind.DWord);
                log($"Télémétrie applications (CompatTelRunner) : {(doCompat ? "DÉSACTIVÉE" : "ACTIVÉE")}.");
            }
            catch (Exception ex) { log($"CompatTel : erreur — {ex.Message}"); }
        }

        // ── Helpers service ───────────────────────────────────────────────────

        private static bool IsSvcDisabled(string name)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("sc", $"qc \"{name}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(5_000);
                return output.Contains("DISABLED", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void SetSvc(string name, bool disabled)
        {
            var startType = disabled ? "disabled" : "auto";
            RunCmd("sc", $"config \"{name}\" start= {startType}");
            if (disabled)
                RunCmd("sc", $"stop \"{name}\"");
            else
                RunCmd("sc", $"start \"{name}\"");
        }

        private static void RunCmd(string exe, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, args)
                { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(10_000);
            }
            catch { }
        }
    }
}
