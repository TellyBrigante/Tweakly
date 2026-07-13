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

        // État lu au chargement → référence pour n'appliquer que ce qui change
        private (bool Telemetrie, bool AdID, bool ActivityHistory, bool BingSearch,
                 bool InputPersonal, bool Location, bool WER, bool TailoredExp, bool CompatTel) _state;

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

            _state = s;
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

        /// <summary>
        /// DA optim v1.3.5 : cliquer n'importe ou sur une rangee actionne son switch
        /// (Tag de la rangee = sa CheckBox via x:Reference). Garde anti-double-toggle.
        /// </summary>
        private void Row_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            for (var d = e.OriginalSource as DependencyObject; d != null;
                 d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                if (d is CheckBox) return;
            if (sender is System.Windows.Controls.Border row && row.Tag is CheckBox chk)
                chk.IsChecked = chk.IsChecked != true;
        }
        private async void BtnAppliquer_Click(object sender, RoutedEventArgs e)
        {
            BtnAppliquer.IsEnabled = false;

            // N'appliquer QUE ce qui a changé (null = inchangé → ignoré)
            bool? doTel      = Helpers.TweakFeedback.Changed(ChkTelemetrie,      _state.Telemetrie);
            bool? doAdID     = Helpers.TweakFeedback.Changed(ChkAdID,            _state.AdID);
            bool? doActivity = Helpers.TweakFeedback.Changed(ChkActivityHistory, _state.ActivityHistory);
            bool? doBing     = Helpers.TweakFeedback.Changed(ChkBingSearch,      _state.BingSearch);
            bool? doInk      = Helpers.TweakFeedback.Changed(ChkInputPersonal,   _state.InputPersonal);
            bool? doLoc      = Helpers.TweakFeedback.Changed(ChkLocation,        _state.Location);
            bool? doWER      = Helpers.TweakFeedback.Changed(ChkWER,             _state.WER);
            bool? doTailored = Helpers.TweakFeedback.Changed(ChkTailoredExp,     _state.TailoredExp);
            bool? doCompat   = Helpers.TweakFeedback.Changed(ChkCompatTel,       _state.CompatTel);

            if (!(doTel.HasValue || doAdID.HasValue || doActivity.HasValue || doBing.HasValue
                  || doInk.HasValue || doLoc.HasValue || doWER.HasValue || doTailored.HasValue || doCompat.HasValue))
            {
                Helpers.TweakFeedback.ShowInfo(StatusBanner, StatusDot, StatusText, "Aucune modification à appliquer.");
                BtnAppliquer.IsEnabled = true;
                return;
            }

            _main.Log("Confidentialité : application des paramètres…");
            var msgs = new System.Collections.Generic.List<string>();
            await Task.Run(() =>
                ApplyChanges(doTel, doAdID, doActivity, doBing, doInk,
                             doLoc, doWER, doTailored, doCompat,
                             msg => { _main.Log(msg); msgs.Add(msg); }));

            var actual = await Task.Run(ReadState);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Telemetrie Windows", doTel, actual.Telemetrie);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Identifiant publicitaire", doAdID, actual.AdID);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Historique d'activite", doActivity, actual.ActivityHistory);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Recherche Bing", doBing, actual.BingSearch);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Personnalisation de saisie", doInk, actual.InputPersonal);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Localisation", doLoc, actual.Location);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Rapports d'erreurs Windows", doWER, actual.WER);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Experiences personnalisees", doTailored, actual.TailoredExp);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Inventaire applications", doCompat, actual.CompatTel);

            ChkTelemetrie.IsChecked = actual.Telemetrie;
            ChkAdID.IsChecked = actual.AdID;
            ChkActivityHistory.IsChecked = actual.ActivityHistory;
            ChkBingSearch.IsChecked = actual.BingSearch;
            ChkInputPersonal.IsChecked = actual.InputPersonal;
            ChkLocation.IsChecked = actual.Location;
            ChkWER.IsChecked = actual.WER;
            ChkTailoredExp.IsChecked = actual.TailoredExp;
            ChkCompatTel.IsChecked = actual.CompatTel;
            _state = actual;
            _main.Log("Confidentialité : paramètres appliqués.");
            Helpers.TweakFeedback.Show(StatusBanner, StatusDot, StatusText, msgs, "Paramètres de confidentialité appliqués");
            BtnAppliquer.IsEnabled = true;
        }

        private static void ApplyChanges(
            bool? doTel, bool? doAdID, bool? doActivity, bool? doBing, bool? doInk,
            bool? doLoc, bool? doWER, bool? doTailored, bool? doCompat,
            Action<string> log)
        {
            // Télémétrie
            if (doTel.HasValue)
            try
            {
                const string pathDC =
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection";
                if (doTel.Value)
                {
                    Registry.SetValue(pathDC, "AllowTelemetry", 0, RegistryValueKind.DWord);
                    SetSvc("DiagTrack",          disabled: true);
                    SetSvc("dmwappushservice",   disabled: true);
                    log("Télémétrie Windows : DÉSACTIVÉE.");
                }
                else
                {
                    DeleteRegistryValue(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                        "AllowTelemetry");
                    SetSvcStart("DiagTrack", "auto", start: true);
                    SetSvcStart("dmwappushservice", "demand", start: false);
                    log("Télémétrie Windows : RESTAURÉE (par défaut).");
                }
            }
            catch (Exception ex) { log($"Télémétrie : erreur — {ex.Message}"); }

            // Identifiant publicitaire
            if (doAdID.HasValue)
            try
            {
                Registry.SetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                    "Enabled", doAdID.Value ? 0 : 1, RegistryValueKind.DWord);
                log($"Identifiant publicitaire : {(doAdID.Value ? "DÉSACTIVÉ" : "ACTIVÉ")}.");
            }
            catch (Exception ex) { log($"AdvertisingInfo : erreur — {ex.Message}"); }

            // Historique d'activité
            if (doActivity.HasValue)
            try
            {
                if (doActivity.Value)
                {
                    Registry.SetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                        "EnableActivityFeed", 0, RegistryValueKind.DWord);
                }
                else
                {
                    DeleteRegistryValue(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\System",
                        "EnableActivityFeed");
                }
                log($"Historique d'activité : {(doActivity.Value ? "DÉSACTIVÉ" : "ACTIVÉ")}.");
            }
            catch (Exception ex) { log($"ActivityFeed : erreur — {ex.Message}"); }

            // Bing Search
            if (doBing.HasValue)
            try
            {
                if (doBing.Value)
                {
                    Registry.SetValue(
                        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
                        "BingSearchEnabled", 0, RegistryValueKind.DWord);
                }
                else
                {
                    DeleteRegistryValue(Registry.CurrentUser,
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
                        "BingSearchEnabled");
                }
                log($"Recherche Bing : {(doBing.Value ? "DÉSACTIVÉE" : "ACTIVÉE")}.");
            }
            catch (Exception ex) { log($"BingSearch : erreur — {ex.Message}"); }

            // Input Personalization
            if (doInk.HasValue)
            try
            {
                const string pathInk =
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\InputPersonalization";
                if (doInk.Value)
                {
                    Registry.SetValue(pathInk, "RestrictImplicitInkCollection",
                        1, RegistryValueKind.DWord);
                    Registry.SetValue(pathInk, "RestrictImplicitTextCollection",
                        1, RegistryValueKind.DWord);
                }
                else
                {
                    DeleteRegistryValue(Registry.CurrentUser,
                        @"SOFTWARE\Microsoft\InputPersonalization",
                        "RestrictImplicitInkCollection");
                    DeleteRegistryValue(Registry.CurrentUser,
                        @"SOFTWARE\Microsoft\InputPersonalization",
                        "RestrictImplicitTextCollection");
                }
                log($"Personnalisation saisie : {(doInk.Value ? "DÉSACTIVÉE" : "ACTIVÉE")}.");
            }
            catch (Exception ex) { log($"InputPersonalization : erreur — {ex.Message}"); }

            // Localisation
            if (doLoc.HasValue)
            try
            {
                if (doLoc.Value)
                {
                    Registry.SetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                        "DisableLocation", 1, RegistryValueKind.DWord);
                }
                else
                {
                    DeleteRegistryValue(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                        "DisableLocation");
                }
                log($"Localisation : {(doLoc.Value ? "DÉSACTIVÉE" : "ACTIVÉE")}.");
            }
            catch (Exception ex) { log($"Localisation : erreur — {ex.Message}"); }

            // WER
            if (doWER.HasValue)
            try
            {
                const string pathWER =
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting";
                if (doWER.Value)
                {
                    Registry.SetValue(pathWER, "Disabled", 1, RegistryValueKind.DWord);
                    SetSvc("WerSvc", disabled: true);
                    log("Rapport d'erreurs Windows : DÉSACTIVÉ.");
                }
                else
                {
                    DeleteRegistryValue(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
                        "Disabled");
                    SetSvcStart("WerSvc", "demand", start: false);
                    log("Rapport d'erreurs Windows : ACTIVÉ (par défaut).");
                }
            }
            catch (Exception ex) { log($"WER : erreur — {ex.Message}"); }

            // Tailored Experiences
            if (doTailored.HasValue)
            try
            {
                Registry.SetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy",
                    "TailoredExperiencesWithDiagnosticDataEnabled",
                    doTailored.Value ? 0 : 1, RegistryValueKind.DWord);
                log($"Tailored Experiences : {(doTailored.Value ? "DÉSACTIVÉES" : "ACTIVÉES")}.");
            }
            catch (Exception ex) { log($"TailoredExperiences : erreur — {ex.Message}"); }

            // CompatTel
            if (doCompat.HasValue)
            try
            {
                if (doCompat.Value)
                {
                    Registry.SetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppCompat",
                        "DisableInventory", 1, RegistryValueKind.DWord);
                }
                else
                {
                    DeleteRegistryValue(Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\AppCompat",
                        "DisableInventory");
                }
                log($"Télémétrie applications (CompatTelRunner) : {(doCompat.Value ? "DÉSACTIVÉE" : "ACTIVÉE")}.");
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

        private static void SetSvcStart(string name, string startType, bool start)
        {
            RunCmd("sc", $"config \"{name}\" start= {startType}");
            if (start) RunCmd("sc", $"start \"{name}\"");
        }

        private static void DeleteRegistryValue(RegistryKey root, string subKey, string name)
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
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
