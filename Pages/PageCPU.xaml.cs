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

        private async void BtnAppliquer_Click(object sender, RoutedEventArgs e)
        {
            BtnAppliquer.IsEnabled = false;

            bool doUltimate   = ChkUltimatePower.IsChecked     == true;
            bool doThrottling = ChkDisableThrottling.IsChecked  == true;
            bool doSysResp    = ChkSysResponsiveness.IsChecked  == true;
            bool doTimerRes   = ChkTimerResolution.IsChecked    == true;
            bool doHVCI       = ChkHVCI.IsChecked               == true;

            // Avertissement sécurité avant de désactiver Memory Integrity
            if (doHVCI && !ReadState().DisableHVCI)
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
                    doHVCI = false;
                }
            }

            _main.Log("CPU : application des tweaks…");

            await Task.Run(() =>
                ApplyChanges(doUltimate, doThrottling, doSysResp, doTimerRes, doHVCI,
                             msg => _main.Log(msg)));

            _main.Log("CPU : tweaks appliqués.");
            BtnAppliquer.IsEnabled = true;
        }

        private static void ApplyChanges(
            bool doUltimate, bool doThrottling, bool doSysResp, bool doTimerRes,
            bool doHVCI, Action<string> log)
        {
            const string UltimateGUID = "e9a42b02-d5df-448d-aa00-03f14749eb61";

            // Power plan
            try
            {
                if (doUltimate)
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
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                    "PowerThrottlingOff", doThrottling ? 1 : 0, RegistryValueKind.DWord);
                log($"Power Throttling : {(doThrottling ? "DÉSACTIVÉ" : "ACTIVÉ (par défaut)")}.");
            }
            catch (Exception ex) { log($"Power Throttling : erreur — {ex.Message}"); }

            // SystemResponsiveness
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                    "SystemResponsiveness", doSysResp ? 0 : 20, RegistryValueKind.DWord);
                log($"SystemResponsiveness : {(doSysResp ? "0 (jeux)" : "20 (par défaut)")}.");
            }
            catch (Exception ex) { log($"SystemResponsiveness : erreur — {ex.Message}"); }

            // GlobalTimerResolution
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel",
                    "GlobalTimerResolutionRequests", doTimerRes ? 1 : 0, RegistryValueKind.DWord);
                log($"GlobalTimerResolution : {(doTimerRes ? "ACTIVÉ" : "DÉSACTIVÉ")}.");
            }
            catch (Exception ex) { log($"GlobalTimerResolution : erreur — {ex.Message}"); }

            // HVCI / Memory Integrity
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                    "Enabled", doHVCI ? 0 : 1, RegistryValueKind.DWord);
                log($"Memory Integrity (HVCI) : {(doHVCI ? "DÉSACTIVÉ" : "ACTIVÉ")} — redémarrage requis.");
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
