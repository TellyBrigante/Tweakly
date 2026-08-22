using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class PageReseau : UserControl
    {
        private readonly MainWindow _main;
        private bool _loaded = false;

        // État lu au chargement → référence pour n'appliquer que ce qui change
        private (bool Nagle, bool NetDNS, bool AdapterPower, bool WPAD, bool NetThrottle) _state;

        private sealed record StateRead(
            Helpers.ProbeResult<bool> Nagle,
            Helpers.ProbeResult<bool> NetDNS,
            Helpers.ProbeResult<bool> AdapterPower,
            Helpers.ProbeResult<bool> WPAD,
            Helpers.ProbeResult<bool> NetThrottle)
        {
            public (bool Nagle, bool NetDNS, bool AdapterPower, bool WPAD, bool NetThrottle) Values
                => (Nagle.Value, NetDNS.Value, AdapterPower.Value, WPAD.Value, NetThrottle.Value);
        }

        public PageReseau(MainWindow main)
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

            Helpers.TweakFeedback.ApplyDetectedState(ChkNagle, read.Nagle, _main.Log, "Nagle");
            Helpers.TweakFeedback.ApplyDetectedState(ChkNetDNS, read.NetDNS, _main.Log, "DNS");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkAdapterPower, read.AdapterPower, _main.Log, "Gestion d'alimentation réseau");
            Helpers.TweakFeedback.ApplyDetectedState(ChkWPAD, read.WPAD, _main.Log, "WPAD");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkNetThrottle, read.NetThrottle, _main.Log, "Bridage réseau multimédia");

            _state = read.Values;
            BtnAppliquer.IsEnabled = true;
            _main.Log("Réseau : état chargé.");
        }

        private static (bool Nagle, bool NetDNS, bool AdapterPower, bool WPAD, bool NetThrottle) ReadState()
            => ReadStateDetailed().Values;

        private static StateRead ReadStateDetailed()
        {
            bool nagleAvailable = Helpers.NetworkOptimizationSettings.TryReadNagle(
                out bool nagleDisabled,
                out string nagleError);
            var nagle = Helpers.ProbeResult<bool>.FromTry(
                "Réseau : lecture de Nagle",
                nagleAvailable,
                nagleDisabled,
                nagleError,
                fallback: false);

            // DNS Cloudflare
            bool dnsAvailable = Helpers.NetworkOptimizationSettings.TryReadDns(
                out bool dnsOptimized,
                out string dnsError);
            var netDns = Helpers.ProbeResult<bool>.FromTry(
                "Réseau : lecture du DNS",
                dnsAvailable,
                dnsOptimized,
                dnsError,
                fallback: false);

            // Adapter power management
            bool powerAvailable = Helpers.NetworkAdapterPower.TryRead(
                out bool powerDisabled,
                out string powerError);
            var adapterPower = Helpers.ProbeResult<bool>.FromTry(
                "Réseau : lecture de la gestion d'alimentation",
                powerAvailable,
                powerDisabled,
                powerError,
                fallback: false);

            // WPAD désactivé
            var wpad = Helpers.ProbeResult<bool>.Capture(
                "Réseau : lecture de WPAD",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp",
                        "DisableWpad", null);
                    return v != null && Convert.ToInt32(v) == 1;
                },
                fallback: false);

            // Bridage réseau multimédia désactivé (NetworkThrottlingIndex = 0xFFFFFFFF ;
            // défaut Windows = 10 — vérifié en réel le 2026-06-12)
            var netThrottle = Helpers.ProbeResult<bool>.Capture(
                "Réseau : lecture de NetworkThrottlingIndex",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                        "NetworkThrottlingIndex", null);
                    return v != null && Convert.ToInt64(v) is 0xFFFFFFFF or -1;
                },
                fallback: false);

            return new StateRead(nagle, netDns, adapterPower, wpad, netThrottle);
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
            bool? chNagle = Helpers.TweakFeedback.Changed(ChkNagle,        _state.Nagle);
            bool? chDNS   = Helpers.TweakFeedback.Changed(ChkNetDNS,       _state.NetDNS);
            bool? chPower = Helpers.TweakFeedback.Changed(ChkAdapterPower, _state.AdapterPower);
            bool? chWPAD  = Helpers.TweakFeedback.Changed(ChkWPAD,         _state.WPAD);
            bool? chThr   = Helpers.TweakFeedback.Changed(ChkNetThrottle,  _state.NetThrottle);

            if (!(chNagle.HasValue || chDNS.HasValue || chPower.HasValue || chWPAD.HasValue || chThr.HasValue))
            {
                Helpers.TweakFeedback.ShowInfo(StatusBanner, StatusDot, StatusText, "Aucune modification à appliquer.");
                BtnAppliquer.IsEnabled = true;
                return;
            }

            _main.Log("Réseau : application des tweaks…");
            var msgs = new System.Collections.Generic.List<string>();
            await Task.Run(() =>
                ApplyChanges(chNagle, chDNS, chPower, chWPAD, chThr,
                             msg => { _main.Log(msg); msgs.Add(msg); }));

            StateRead actual = await Task.Run(ReadStateDetailed);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Nagle", chNagle, actual.Nagle);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "DNS", chDNS, actual.NetDNS);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Mise en veille adaptateur", chPower, actual.AdapterPower);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "WPAD", chWPAD, actual.WPAD);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Bridage reseau", chThr, actual.NetThrottle);

            Helpers.TweakFeedback.ApplyDetectedState(ChkNagle, actual.Nagle, _main.Log, "Nagle");
            Helpers.TweakFeedback.ApplyDetectedState(ChkNetDNS, actual.NetDNS, _main.Log, "DNS");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkAdapterPower, actual.AdapterPower, _main.Log, "Gestion d'alimentation réseau");
            Helpers.TweakFeedback.ApplyDetectedState(ChkWPAD, actual.WPAD, _main.Log, "WPAD");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkNetThrottle, actual.NetThrottle, _main.Log, "Bridage réseau multimédia");
            _state = actual.Values;
            _main.Log("Réseau : application terminée.");
            Helpers.TweakFeedback.Show(StatusBanner, StatusDot, StatusText, msgs, "Tweaks réseau appliqués");
            BtnAppliquer.IsEnabled = true;
        }

        private static void ApplyChanges(
            bool? doNagle, bool? doDNS, bool? doPower, bool? doWPAD, bool? doThrottle,
            Action<string> log)
        {
            // Bridage réseau multimédia (NetworkThrottlingIndex — même clé SystemProfile
            // que SystemResponsiveness du tab CPU ; off = 0xFFFFFFFF, défaut Windows = 10)
            if (doThrottle.HasValue)
            {
                try
                {
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.LocalMachine,
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                        "NetworkThrottlingIndex",
                        doThrottle == true
                            ? unchecked((int)0xFFFFFFFF)
                            : Helpers.RegistryValueLogic.NetworkThrottlingDefault);
                    log($"Bridage réseau multimédia : {(doThrottle == true ? "DÉSACTIVÉ" : "restauré (défaut Windows)")} — redémarrage conseillé.");
                }
                catch (Exception ex) { log($"Bridage réseau : erreur — {ex.Message}"); }
            }

            // Nagle
            if (doNagle.HasValue)
            {
                Helpers.NetworkOptimizationSettings.TrySetNagle(doNagle.Value, out string result);
                log(result);
            }

            // DNS
            if (doDNS.HasValue)
            {
                Helpers.NetworkOptimizationSettings.TrySetDns(doDNS.Value, out string result);
                log(result);
            }

            // Adapter power management
            if (doPower.HasValue)
            {
                bool success = Helpers.NetworkAdapterPower.TrySet(doPower.Value, out string result);
                log(success ? result : $"Adaptateur réseau : erreur — {result}");
            }

            // WPAD
            if (doWPAD.HasValue)
            try
            {
                if (doWPAD.Value)
                {
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.LocalMachine,
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp",
                        "DisableWpad", 1);
                }
                else
                {
                    Helpers.VerifiedRegistry.DeleteValue(Registry.LocalMachine,
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp",
                        "DisableWpad");
                }
                log($"WPAD : {(doWPAD.Value ? "DÉSACTIVÉ" : "ACTIVÉ (par défaut)")}.");
            }
            catch (Exception ex) { log($"WPAD : erreur — {ex.Message}"); }
        }

        // ── Vider le cache DNS ────────────────────────────────────────────────

        private async void BtnFlushDNS_Click(object sender, RoutedEventArgs e)
        {
            BtnFlushDNS.IsEnabled = false;
            _main.Log("DNS : vidage du cache…");

            var result = await Task.Run<(bool Ok, string Error)>(() =>
            {
                try
                {
                    ProcessCommandResult command = ProcessCommand.Run(WindowsSystemTools.PathFor("ipconfig.exe"), "/flushdns", 10_000);
                    if (!command.Success)
                        return (false, command.FailureDescription);
                    return (true, "");
                }
                catch (Exception ex) { return (false, ex.Message); }
            });

            _main.Log(result.Ok ? "Cache DNS vidé avec succès." : $"Cache DNS : erreur — {result.Error}");
            Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, result.Ok,
                "Cache DNS vidé", $"Cache DNS non vidé : {result.Error}");
            BtnFlushDNS.IsEnabled = true;
        }
    }
}
