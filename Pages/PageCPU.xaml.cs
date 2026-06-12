using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
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
                 bool TimerResolution, bool DisableHVCI) _state;

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

            var state = await Task.Run(ReadState);

            ChkUltimatePower.IsChecked     = state.UltimatePower;
            ChkDisableThrottling.IsChecked = state.DisableThrottling;
            ChkSysResponsiveness.IsChecked = state.SysResponsiveness;
            ChkTimerResolution.IsChecked   = state.TimerResolution;
            ChkHVCI.IsChecked              = state.DisableHVCI;

            _state = state;
            BtnAppliquer.IsEnabled = true;
            _main.Log("CPU : état chargé.");
        }

        private static (bool UltimatePower, bool DisableThrottling,
                        bool SysResponsiveness, bool TimerResolution,
                        bool DisableHVCI) ReadState()
        {
            bool ultimatePower = false;
            try
            {
                using var p = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true
                });
                if (p != null)
                {
                    var output = p.StandardOutput.ReadToEnd().ToLowerInvariant();
                    p.WaitForExit(5_000);
                    ultimatePower = output.Contains("ultimate") ||
                                    output.Contains("ultim")    ||
                                    output.Contains("haute performance") ||
                                    output.Contains("hautes performances");
                }
            }
            catch { }

            bool disableThrottling = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                    "PowerThrottlingOff", null);
                disableThrottling = v != null && Convert.ToInt32(v) == 1;
            }
            catch { }

            bool sysResponsiveness = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                    "SystemResponsiveness", null);
                sysResponsiveness = v != null && Convert.ToInt32(v) == 0;
            }
            catch { }

            bool timerResolution = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel",
                    "GlobalTimerResolutionRequests", null);
                timerResolution = v != null && Convert.ToInt32(v) == 1;
            }
            catch { }

            // HVCI / Memory Integrity désactivé (Enabled=0 ou absent)
            bool disableHVCI = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                    "Enabled", null);
                disableHVCI = v == null || Convert.ToInt32(v) == 0;
            }
            catch { disableHVCI = true; }

            return (ultimatePower, disableThrottling, sysResponsiveness, timerResolution, disableHVCI);
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
            bool? chUlt   = Helpers.TweakFeedback.Changed(ChkUltimatePower,     _state.UltimatePower);
            bool? chThr   = Helpers.TweakFeedback.Changed(ChkDisableThrottling, _state.DisableThrottling);
            bool? chSys   = Helpers.TweakFeedback.Changed(ChkSysResponsiveness, _state.SysResponsiveness);
            bool? chTimer = Helpers.TweakFeedback.Changed(ChkTimerResolution,   _state.TimerResolution);
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

            if (!(chUlt.HasValue || chThr.HasValue || chSys.HasValue || chTimer.HasValue || chHVCI.HasValue))
            {
                Helpers.TweakFeedback.ShowInfo(StatusBanner, StatusDot, StatusText, "Aucune modification à appliquer.");
                BtnAppliquer.IsEnabled = true;
                return;
            }

            _main.Log("CPU : application des tweaks…");
            var msgs = new System.Collections.Generic.List<string>();
            await Task.Run(() =>
                ApplyChanges(chUlt, chThr, chSys, chTimer, chHVCI,
                             msg => { _main.Log(msg); msgs.Add(msg); }));

            _state = (ChkUltimatePower.IsChecked == true, ChkDisableThrottling.IsChecked == true,
                      ChkSysResponsiveness.IsChecked == true, ChkTimerResolution.IsChecked == true,
                      ChkHVCI.IsChecked == true);
            _main.Log("CPU : tweaks appliqués.");
            Helpers.TweakFeedback.Show(StatusBanner, StatusDot, StatusText, msgs, "Tweaks CPU appliqués");
            BtnAppliquer.IsEnabled = true;
        }

        private static void ApplyChanges(
            bool? doUltimate, bool? doThrottling, bool? doSysResp, bool? doTimerRes,
            bool? doHVCI, Action<string> log)
        {
            const string UltimateGUID = "e9a42b02-d5df-448d-aa00-03f14749eb61";

            // Power plan
            if (doUltimate.HasValue)
            try
            {
                if (doUltimate.Value)
                {
                    RunCmd("powercfg", $"/setactive {UltimateGUID}");
                    // Si le plan n'existe pas encore, le dupliquer
                    using var pCheck = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                    var chkOut = pCheck?.StandardOutput.ReadToEnd().ToLowerInvariant() ?? "";
                    pCheck?.WaitForExit(5_000);
                    if (!chkOut.Contains("ultim") && !chkOut.Contains("haute performance"))
                    {
                        using var pDup = Process.Start(new ProcessStartInfo(
                            "powercfg", $"/duplicatescheme {UltimateGUID}")
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                        var dupOut = pDup?.StandardOutput.ReadToEnd() ?? "";
                        pDup?.WaitForExit(10_000);
                        var m = Regex.Match(dupOut,
                            @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
                            RegexOptions.IgnoreCase);
                        if (m.Success) RunCmd("powercfg", $"/setactive {m.Value}");
                    }
                    log("Power Plan : PERFORMANCES ULTIMES activé.");
                }
                else
                {
                    RunCmd("powercfg", "/setactive SCHEME_BALANCED");
                    log("Power Plan : ÉQUILIBRÉ (par défaut).");
                }
            }
            catch (Exception ex) { log($"Power Plan : erreur — {ex.Message}"); }

            // Power Throttling
            if (doThrottling.HasValue)
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                    "PowerThrottlingOff", doThrottling.Value ? 1 : 0, RegistryValueKind.DWord);
                log($"Power Throttling : {(doThrottling.Value ? "DÉSACTIVÉ" : "ACTIVÉ (par défaut)")}.");
            }
            catch (Exception ex) { log($"Power Throttling : erreur — {ex.Message}"); }

            // SystemResponsiveness
            if (doSysResp.HasValue)
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                    "SystemResponsiveness", doSysResp.Value ? 0 : 20, RegistryValueKind.DWord);
                log($"SystemResponsiveness : {(doSysResp.Value ? "0 (jeux)" : "20 (par défaut)")}.");
            }
            catch (Exception ex) { log($"SystemResponsiveness : erreur — {ex.Message}"); }

            // GlobalTimerResolution
            if (doTimerRes.HasValue)
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel",
                    "GlobalTimerResolutionRequests", doTimerRes.Value ? 1 : 0, RegistryValueKind.DWord);
                log($"GlobalTimerResolution : {(doTimerRes.Value ? "ACTIVÉ" : "DÉSACTIVÉ")}.");
            }
            catch (Exception ex) { log($"GlobalTimerResolution : erreur — {ex.Message}"); }

            // HVCI / Memory Integrity
            if (doHVCI.HasValue)
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                    "Enabled", doHVCI.Value ? 0 : 1, RegistryValueKind.DWord);
                log($"Memory Integrity (HVCI) : {(doHVCI.Value ? "DÉSACTIVÉ" : "ACTIVÉ")} — redémarrage requis.");
            }
            catch (Exception ex) { log($"HVCI : erreur — {ex.Message}"); }
        }

        private static void RunCmd(string exe, string args)
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(15_000);
        }
    }
}
