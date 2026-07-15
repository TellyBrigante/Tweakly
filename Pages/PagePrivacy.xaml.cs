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

        private sealed record StateRead(
            Helpers.ProbeResult<bool> Telemetrie,
            Helpers.ProbeResult<bool> AdID,
            Helpers.ProbeResult<bool> ActivityHistory,
            Helpers.ProbeResult<bool> BingSearch,
            Helpers.ProbeResult<bool> InputPersonal,
            Helpers.ProbeResult<bool> Location,
            Helpers.ProbeResult<bool> WER,
            Helpers.ProbeResult<bool> TailoredExp,
            Helpers.ProbeResult<bool> CompatTel)
        {
            public (bool Telemetrie, bool AdID, bool ActivityHistory, bool BingSearch,
                    bool InputPersonal, bool Location, bool WER, bool TailoredExp, bool CompatTel) Values
                => (Telemetrie.Value, AdID.Value, ActivityHistory.Value, BingSearch.Value,
                    InputPersonal.Value, Location.Value, WER.Value, TailoredExp.Value, CompatTel.Value);
        }

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

            StateRead read = await Task.Run(ReadStateDetailed);

            Helpers.TweakFeedback.ApplyDetectedState(ChkTelemetrie, read.Telemetrie, _main.Log, "Télémétrie");
            Helpers.TweakFeedback.ApplyDetectedState(ChkAdID, read.AdID, _main.Log, "Identifiant publicitaire");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkActivityHistory, read.ActivityHistory, _main.Log, "Historique d'activité");
            Helpers.TweakFeedback.ApplyDetectedState(ChkBingSearch, read.BingSearch, _main.Log, "Recherche Bing");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkInputPersonal, read.InputPersonal, _main.Log, "Personnalisation de la saisie");
            Helpers.TweakFeedback.ApplyDetectedState(ChkLocation, read.Location, _main.Log, "Localisation");
            Helpers.TweakFeedback.ApplyDetectedState(ChkWER, read.WER, _main.Log, "Rapports d'erreurs Windows");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkTailoredExp, read.TailoredExp, _main.Log, "Expériences personnalisées");
            Helpers.TweakFeedback.ApplyDetectedState(ChkCompatTel, read.CompatTel, _main.Log, "CompatTel");

            _state = read.Values;
            BtnAppliquer.IsEnabled = true;
            _main.Log("Confidentialité : état chargé.");
        }

        private static (bool Telemetrie, bool AdID, bool ActivityHistory, bool BingSearch,
                        bool InputPersonal, bool Location, bool WER,
                        bool TailoredExp, bool CompatTel) ReadState()
            => ReadStateDetailed().Values;

        private static StateRead ReadStateDetailed()
        {
            // Télémétrie (AllowTelemetry = 0 ET service DiagTrack désactivé)
            var telemetrie = Helpers.ProbeResult<bool>.Capture(
                "Confidentialité : lecture de la télémétrie",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                        "AllowTelemetry", null);
                    bool policySet = v != null && Convert.ToInt32(v) == 0;
                    bool servicesDisabled = IsSvcDisabledOrMissing("DiagTrack") &&
                                            IsSvcDisabledOrMissing("dmwappushservice");
                    return policySet && servicesDisabled;
                },
                fallback: false);

            // Identifiant publicitaire
            var adId = Helpers.ProbeResult<bool>.Capture(
                "Confidentialité : lecture de l'identifiant publicitaire",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                        "Enabled", null);
                    return v != null && Convert.ToInt32(v) == 0;
                },
                fallback: false);

            // Historique d'activité
            var activityHistory = Helpers.ProbeResult<bool>.Capture(
                "Confidentialité : lecture de l'historique d'activité",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                        "EnableActivityFeed", null);
                    return v != null && Convert.ToInt32(v) == 0;
                },
                fallback: false);

            // Bing Search désactivé
            var bingSearch = Helpers.ProbeResult<bool>.Capture(
                "Confidentialité : lecture de la recherche Bing",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
                        "BingSearchEnabled", null);
                    return v != null && Convert.ToInt32(v) == 0;
                },
                fallback: false);

            // Input Personalization
            var inputPersonal = Helpers.ProbeResult<bool>.Capture(
                "Confidentialité : lecture de la personnalisation de saisie",
                () =>
                {
                    var ink = Registry.GetValue(
                        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\InputPersonalization",
                        "RestrictImplicitInkCollection", null);
                    var text = Registry.GetValue(
                        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\InputPersonalization",
                        "RestrictImplicitTextCollection", null);
                    return ink != null && Convert.ToInt32(ink) == 1 &&
                           text != null && Convert.ToInt32(text) == 1;
                },
                fallback: false);

            // Localisation
            var location = Helpers.ProbeResult<bool>.Capture(
                "Confidentialité : lecture de la localisation",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                        "DisableLocation", null);
                    return v != null && Convert.ToInt32(v) == 1;
                },
                fallback: false);

            // WER
            var wer = Helpers.ProbeResult<bool>.Capture(
                "Confidentialité : lecture des rapports d'erreurs Windows",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
                        "Disabled", null);
                    bool policySet = v != null && Convert.ToInt32(v) == 1;
                    return policySet && IsSvcDisabled("WerSvc");
                },
                fallback: false);

            // Tailored Experiences
            var tailoredExp = Helpers.ProbeResult<bool>.Capture(
                "Confidentialité : lecture des expériences personnalisées",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy",
                        "TailoredExperiencesWithDiagnosticDataEnabled", null);
                    return v != null && Convert.ToInt32(v) == 0;
                },
                fallback: false);

            // CompatTel / AppCompat
            var compatTel = Helpers.ProbeResult<bool>.Capture(
                "Confidentialité : lecture de CompatTel",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppCompat",
                        "DisableInventory", null);
                    return v != null && Convert.ToInt32(v) == 1;
                },
                fallback: false);

            return new StateRead(
                telemetrie, adId, activityHistory, bingSearch,
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
            if (sender is System.Windows.Controls.Border row && row.Tag is CheckBox chk && chk.IsEnabled)
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

            StateRead actual = await Task.Run(ReadStateDetailed);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Telemetrie Windows", doTel, actual.Telemetrie);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Identifiant publicitaire", doAdID, actual.AdID);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Historique d'activite", doActivity, actual.ActivityHistory);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Recherche Bing", doBing, actual.BingSearch);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Personnalisation de saisie", doInk, actual.InputPersonal);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Localisation", doLoc, actual.Location);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Rapports d'erreurs Windows", doWER, actual.WER);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Experiences personnalisees", doTailored, actual.TailoredExp);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Inventaire applications", doCompat, actual.CompatTel);

            Helpers.TweakFeedback.ApplyDetectedState(ChkTelemetrie, actual.Telemetrie, _main.Log, "Télémétrie");
            Helpers.TweakFeedback.ApplyDetectedState(ChkAdID, actual.AdID, _main.Log, "Identifiant publicitaire");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkActivityHistory, actual.ActivityHistory, _main.Log, "Historique d'activité");
            Helpers.TweakFeedback.ApplyDetectedState(ChkBingSearch, actual.BingSearch, _main.Log, "Recherche Bing");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkInputPersonal, actual.InputPersonal, _main.Log, "Personnalisation de la saisie");
            Helpers.TweakFeedback.ApplyDetectedState(ChkLocation, actual.Location, _main.Log, "Localisation");
            Helpers.TweakFeedback.ApplyDetectedState(ChkWER, actual.WER, _main.Log, "Rapports d'erreurs Windows");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkTailoredExp, actual.TailoredExp, _main.Log, "Expériences personnalisées");
            Helpers.TweakFeedback.ApplyDetectedState(ChkCompatTel, actual.CompatTel, _main.Log, "CompatTel");
            _state = actual.Values;
            _main.Log("Confidentialité : application terminée.");
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
                if (doTel.Value)
                {
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                        "AllowTelemetry", 0);
                    SetSvc("DiagTrack",          disabled: true);
                    SetSvc("dmwappushservice",   disabled: true);
                    log("Télémétrie Windows : DÉSACTIVÉE.");
                }
                else
                {
                    Helpers.VerifiedRegistry.DeleteValue(Registry.LocalMachine,
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
                Helpers.VerifiedRegistry.SetDword(
                    Registry.CurrentUser,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                    "Enabled", doAdID.Value ? 0 : 1);
                log($"Identifiant publicitaire : {(doAdID.Value ? "DÉSACTIVÉ" : "ACTIVÉ")}.");
            }
            catch (Exception ex) { log($"AdvertisingInfo : erreur — {ex.Message}"); }

            // Historique d'activité
            if (doActivity.HasValue)
            try
            {
                if (doActivity.Value)
                {
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\System",
                        "EnableActivityFeed", 0);
                }
                else
                {
                    Helpers.VerifiedRegistry.DeleteValue(Registry.LocalMachine,
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
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.CurrentUser,
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
                        "BingSearchEnabled", 0);
                }
                else
                {
                    Helpers.VerifiedRegistry.DeleteValue(Registry.CurrentUser,
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
                if (doInk.Value)
                {
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.CurrentUser,
                        @"SOFTWARE\Microsoft\InputPersonalization",
                        "RestrictImplicitInkCollection", 1);
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.CurrentUser,
                        @"SOFTWARE\Microsoft\InputPersonalization",
                        "RestrictImplicitTextCollection", 1);
                }
                else
                {
                    Helpers.VerifiedRegistry.DeleteValue(Registry.CurrentUser,
                        @"SOFTWARE\Microsoft\InputPersonalization",
                        "RestrictImplicitInkCollection");
                    Helpers.VerifiedRegistry.DeleteValue(Registry.CurrentUser,
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
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                        "DisableLocation", 1);
                }
                else
                {
                    Helpers.VerifiedRegistry.DeleteValue(Registry.LocalMachine,
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
                if (doWER.Value)
                {
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
                        "Disabled", 1);
                    SetSvc("WerSvc", disabled: true);
                    log("Rapport d'erreurs Windows : DÉSACTIVÉ.");
                }
                else
                {
                    Helpers.VerifiedRegistry.DeleteValue(Registry.LocalMachine,
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
                Helpers.VerifiedRegistry.SetDword(
                    Registry.CurrentUser,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy",
                    "TailoredExperiencesWithDiagnosticDataEnabled",
                    doTailored.Value ? 0 : 1);
                log($"Tailored Experiences : {(doTailored.Value ? "DÉSACTIVÉES" : "ACTIVÉES")}.");
            }
            catch (Exception ex) { log($"TailoredExperiences : erreur — {ex.Message}"); }

            // CompatTel
            if (doCompat.HasValue)
            try
            {
                if (doCompat.Value)
                {
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.LocalMachine,
                        @"SOFTWARE\Policies\Microsoft\Windows\AppCompat",
                        "DisableInventory", 1);
                }
                else
                {
                    Helpers.VerifiedRegistry.DeleteValue(Registry.LocalMachine,
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
            object? value = Registry.GetValue(
                $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{name}",
                "Start", null);
            return value != null && Convert.ToInt32(value) == 4;
        }

        private static bool IsSvcDisabledOrMissing(string name)
            => !ServiceExists(name) || IsSvcDisabled(name);

        private static void SetSvc(string name, bool disabled)
        {
            if (!ServiceExists(name)) return;
            var startType = disabled ? "disabled" : "auto";
            RunCmd("sc", $"config \"{name}\" start= {startType}");
            VerifyServiceStartType(name, disabled ? 4 : 2);
            if (disabled)
                RunCmd("sc", $"stop \"{name}\"", 1062);
            else
                RunCmd("sc", $"start \"{name}\"");
        }

        private static void SetSvcStart(string name, string startType, bool start)
        {
            if (!ServiceExists(name)) return;
            RunCmd("sc", $"config \"{name}\" start= {startType}");
            VerifyServiceStartType(name, startType.Equals("auto", StringComparison.OrdinalIgnoreCase) ? 2 : 3);
            if (start) RunCmd("sc", $"start \"{name}\"", 1056);
        }

        private static bool ServiceExists(string name)
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return key != null;
        }

        private static void VerifyServiceStartType(string name, int expected)
        {
            object? value = Registry.GetValue(
                $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{name}",
                "Start", null);
            if (value == null || Convert.ToInt32(value) != expected)
                throw new InvalidOperationException($"Windows n'a pas conservé le type de démarrage du service {name}");
        }

        private static void RunCmd(string exe, string args, params int[] acceptedExitCodes)
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) throw new InvalidOperationException($"{exe} n'a pas démarré");

            string output = p.StandardOutput.ReadToEnd();
            string error = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"{exe} n'a pas répondu sous 10 s");
            }
            if (p.ExitCode == 0 || Array.IndexOf(acceptedExitCodes, p.ExitCode) >= 0) return;

            string detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"{exe} a retourné le code {p.ExitCode}"
                : detail);
        }
    }
}
