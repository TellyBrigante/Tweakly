using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Optimisation_Tool.Pages
{
    public partial class PageCPU : UserControl
    {
        private readonly MainWindow _main;
        private bool _loaded = false;

        // État lu au chargement → référence pour n'appliquer que ce qui change
        private (bool UltimatePower, bool DisableThrottling, bool SysResponsiveness,
                 bool DisableHVCI) _state;

        private sealed record StateRead(
            Helpers.ProbeResult<bool> UltimatePower,
            Helpers.ProbeResult<bool> DisableThrottling,
            Helpers.ProbeResult<bool> SysResponsiveness,
            Helpers.ProbeResult<bool> DisableHVCI)
        {
            public (bool UltimatePower, bool DisableThrottling, bool SysResponsiveness,
                    bool DisableHVCI) Values
                => (UltimatePower.Value, DisableThrottling.Value,
                    SysResponsiveness.Value, DisableHVCI.Value);
        }

        public PageCPU(MainWindow main)
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

            Helpers.TweakFeedback.ApplyDetectedState(
                ChkUltimatePower, read.UltimatePower, _main.Log, "Plan Performances ultimes");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkDisableThrottling, read.DisableThrottling, _main.Log, "Power Throttling");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkSysResponsiveness, read.SysResponsiveness, _main.Log, "SystemResponsiveness");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkHVCI, read.DisableHVCI, _main.Log, "Memory Integrity (HVCI)");

            _state = read.Values;
            BtnAppliquer.IsEnabled = true;
            _main.Log("CPU : état chargé.");
        }

        private static (bool UltimatePower, bool DisableThrottling,
                        bool SysResponsiveness,
                        bool DisableHVCI) ReadState()
            => ReadStateDetailed().Values;

        private static StateRead ReadStateDetailed()
        {
            bool powerAvailable = Helpers.PowerPlanManager.TryReadUltimateState(
                out bool ultimatePower,
                out string powerError);
            var power = Helpers.ProbeResult<bool>.FromTry(
                "CPU : lecture du plan d'alimentation",
                powerAvailable,
                ultimatePower,
                powerError,
                fallback: false);

            var throttling = Helpers.ProbeResult<bool>.Capture(
                "CPU : lecture de Power Throttling",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                        "PowerThrottlingOff", null);
                    return v != null && Convert.ToInt32(v) == 1;
                },
                fallback: false);

            var responsiveness = Helpers.ProbeResult<bool>.Capture(
                "CPU : lecture de SystemResponsiveness",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                        "SystemResponsiveness", null);
                    return v != null && Convert.ToInt32(v) == 0;
                },
                fallback: false);

            // (GlobalTimerResolution RETIRÉ le 2026-06-12 — effet catastrophique mesuré au
            // bench sur build 26200, dépendant du build Windows. Voir commentaire XAML.)

            // HVCI / Memory Integrity désactivé uniquement si Windows expose Enabled=0.
            // Absent = pas d'override Tweakly visible, donc case décochée comme défaut.
            var hvci = Helpers.ProbeResult<bool>.Capture(
                "CPU : lecture de Memory Integrity (HVCI)",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                        "Enabled", null);
                    return v != null && Convert.ToInt32(v) == 0;
                },
                fallback: false);

            return new StateRead(power, throttling, responsiveness, hvci);
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
            bool? chUlt   = Helpers.TweakFeedback.Changed(ChkUltimatePower,     _state.UltimatePower);
            bool? chThr   = Helpers.TweakFeedback.Changed(ChkDisableThrottling, _state.DisableThrottling);
            bool? chSys   = Helpers.TweakFeedback.Changed(ChkSysResponsiveness, _state.SysResponsiveness);
            bool? chHVCI  = Helpers.TweakFeedback.Changed(ChkHVCI,              _state.DisableHVCI);

            // Avertissement sécurité avant de désactiver Memory Integrity (uniquement si on vient de cocher)
            if (chHVCI == true)
            {
                var r = MessageBox.Show(
                    "Désactiver Memory Integrity (HVCI) améliore les performances (5-10% dans certains jeux) " +
                    "mais réduit la protection contre les pilotes malveillants.\n\n" +
                    "Un redémarrage sera nécessaire. Continuer ?",
                    "Memory Integrity / HVCI",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes)
                {
                    ChkHVCI.IsChecked = false;
                    chHVCI = null;
                }
            }

            if (!(chUlt.HasValue || chThr.HasValue || chSys.HasValue || chHVCI.HasValue))
            {
                Helpers.TweakFeedback.ShowInfo(StatusBanner, StatusDot, StatusText, "Aucune modification à appliquer.");
                BtnAppliquer.IsEnabled = true;
                return;
            }

            _main.Log("CPU : application des tweaks…");
            var msgs = new System.Collections.Generic.List<string>();
            await Task.Run(() =>
                ApplyChanges(chUlt, chThr, chSys, chHVCI,
                             msg => { _main.Log(msg); msgs.Add(msg); }));

            StateRead actual = await Task.Run(ReadStateDetailed);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Power Plan", chUlt, actual.UltimatePower);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Power Throttling", chThr, actual.DisableThrottling);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "SystemResponsiveness", chSys, actual.SysResponsiveness);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Memory Integrity (HVCI)", chHVCI, actual.DisableHVCI);

            Helpers.TweakFeedback.ApplyDetectedState(
                ChkUltimatePower, actual.UltimatePower, _main.Log, "Plan Performances ultimes");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkDisableThrottling, actual.DisableThrottling, _main.Log, "Power Throttling");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkSysResponsiveness, actual.SysResponsiveness, _main.Log, "SystemResponsiveness");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkHVCI, actual.DisableHVCI, _main.Log, "Memory Integrity (HVCI)");
            _state = actual.Values;
            _main.Log("CPU : application terminée.");
            Helpers.TweakFeedback.Show(StatusBanner, StatusDot, StatusText, msgs, "Tweaks CPU appliqués");
            BtnAppliquer.IsEnabled = true;
        }

        private static void ApplyChanges(
            bool? doUltimate, bool? doThrottling, bool? doSysResp,
            bool? doHVCI, Action<string> log)
        {
            // Power plan
            if (doUltimate.HasValue)
            {
                bool success = Helpers.PowerPlanManager.TrySetUltimate(doUltimate.Value, out string result);
                log(success ? result : $"Power Plan : erreur — {result}");
            }

            // Power Throttling
            if (doThrottling.HasValue)
            try
            {
                if (doThrottling.Value)
                {
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.LocalMachine,
                        @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                        "PowerThrottlingOff", 1);
                }
                else
                {
                    Helpers.VerifiedRegistry.DeleteValue(Registry.LocalMachine,
                        @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                        "PowerThrottlingOff");
                }
                log($"Power Throttling : {(doThrottling.Value ? "DÉSACTIVÉ" : "ACTIVÉ (par défaut)")}.");
            }
            catch (Exception ex) { log($"Power Throttling : erreur — {ex.Message}"); }

            // SystemResponsiveness
            if (doSysResp.HasValue)
            try
            {
                Helpers.VerifiedRegistry.SetDword(
                    Registry.LocalMachine,
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                    "SystemResponsiveness",
                    doSysResp.Value ? 0 : Helpers.RegistryValueLogic.SystemResponsivenessDefault);
                log($"SystemResponsiveness : {(doSysResp.Value ? "0 (jeux)" : $"{Helpers.RegistryValueLogic.SystemResponsivenessDefault} (par défaut)")}.");
            }
            catch (Exception ex) { log($"SystemResponsiveness : erreur — {ex.Message}"); }

            // (GlobalTimerResolution : tweak RETIRÉ le 2026-06-12 — effet catastrophique
            // mesuré au bench sur build 26200. Les users qui l'avaient activé gardent leur
            // valeur registre ; remise à zéro manuelle : Session Manager\kernel
            // \GlobalTimerResolutionRequests = 0 + reboot.)

            // HVCI / Memory Integrity
            if (doHVCI.HasValue)
            try
            {
                const string hvciPath =
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
                if (doHVCI.Value)
                    Helpers.VerifiedRegistry.SetDword(Registry.LocalMachine, hvciPath, "Enabled", 0);
                else
                    Helpers.VerifiedRegistry.DeleteValue(Registry.LocalMachine, hvciPath, "Enabled");
                log(doHVCI.Value
                    ? "Memory Integrity (HVCI) : DÉSACTIVÉ — redémarrage requis."
                    : "Memory Integrity (HVCI) : réglage Windows restauré — redémarrage requis.");
            }
            catch (Exception ex) { log($"HVCI : erreur — {ex.Message}"); }
        }

    }
}
