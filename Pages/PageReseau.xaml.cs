using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Optimisation_Tool.Pages
{
    public partial class PageReseau : UserControl
    {
        private readonly MainWindow _main;
        private bool _loaded = false;

        // État lu au chargement → référence pour n'appliquer que ce qui change
        private (bool Nagle, bool NetDNS, bool AdapterPower, bool WPAD) _state;

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

            var s = await Task.Run(ReadState);

            ChkNagle.IsChecked        = s.Nagle;
            ChkNetDNS.IsChecked       = s.NetDNS;
            ChkAdapterPower.IsChecked = s.AdapterPower;
            ChkWPAD.IsChecked         = s.WPAD;

            _state = s;
            BtnAppliquer.IsEnabled = true;
            _main.Log("Réseau : état chargé.");
        }

        private static (bool Nagle, bool NetDNS, bool AdapterPower, bool WPAD) ReadState()
        {
            // Nagle désactivé (TcpAckFrequency = 1 ET TcpNoDelay = 1)
            bool nagle = false;
            try
            {
                const string tcpPath =
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
                var v1 = Registry.GetValue(tcpPath, "TcpAckFrequency", null);
                var v2 = Registry.GetValue(tcpPath, "TcpNoDelay",      null);
                nagle = v1 != null && Convert.ToInt32(v1) == 1 &&
                        v2 != null && Convert.ToInt32(v2) == 1;
            }
            catch { }

            // DNS Cloudflare
            bool netDNS = false;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    var dns = ni.GetIPProperties().DnsAddresses;
                    if (dns.Any(a => a.ToString() == "1.1.1.1"))
                    { netDNS = true; break; }
                }
            }
            catch { }

            // Adapter power management
            bool adapterPower = false;
            try
            {
                const string netClass =
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
                using var root = Registry.LocalMachine.OpenSubKey(netClass);
                if (root != null)
                {
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        using var k = root.OpenSubKey(sub);
                        var v = k?.GetValue("PnPCapabilities");
                        if (v != null && Convert.ToInt32(v) == 24)
                        { adapterPower = true; break; }
                    }
                }
            }
            catch { }

            // WPAD désactivé
            bool wpad = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp",
                    "DisableWpad", null);
                wpad = v != null && Convert.ToInt32(v) == 1;
            }
            catch { }

            return (nagle, netDNS, adapterPower, wpad);
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
            bool? chNagle = Helpers.TweakFeedback.Changed(ChkNagle,        _state.Nagle);
            bool? chDNS   = Helpers.TweakFeedback.Changed(ChkNetDNS,       _state.NetDNS);
            bool? chPower = Helpers.TweakFeedback.Changed(ChkAdapterPower, _state.AdapterPower);
            bool? chWPAD  = Helpers.TweakFeedback.Changed(ChkWPAD,         _state.WPAD);

            if (!(chNagle.HasValue || chDNS.HasValue || chPower.HasValue || chWPAD.HasValue))
            {
                Helpers.TweakFeedback.ShowInfo(StatusBanner, StatusDot, StatusText, "Aucune modification à appliquer.");
                BtnAppliquer.IsEnabled = true;
                return;
            }

            _main.Log("Réseau : application des tweaks…");
            var msgs = new System.Collections.Generic.List<string>();
            await Task.Run(() =>
                ApplyChanges(chNagle, chDNS, chPower, chWPAD,
                             msg => { _main.Log(msg); msgs.Add(msg); }));

            _state = (ChkNagle.IsChecked == true, ChkNetDNS.IsChecked == true,
                      ChkAdapterPower.IsChecked == true, ChkWPAD.IsChecked == true);
            _main.Log("Réseau : tweaks appliqués.");
            Helpers.TweakFeedback.Show(StatusBanner, StatusDot, StatusText, msgs, "Tweaks réseau appliqués");
            BtnAppliquer.IsEnabled = true;
        }

        private static void ApplyChanges(
            bool? doNagle, bool? doDNS, bool? doPower, bool? doWPAD,
            Action<string> log)
        {
            // Nagle
            if (doNagle == true)
            {
                try
                {
                    const string tcpPath =
                        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
                    Registry.SetValue(tcpPath, "TcpAckFrequency", 1, RegistryValueKind.DWord);
                    Registry.SetValue(tcpPath, "TcpNoDelay",      1, RegistryValueKind.DWord);

                    // Appliquer aussi sur chaque interface
                    using var ifRoot = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", writable: true);
                    if (ifRoot != null)
                    {
                        foreach (var ifName in ifRoot.GetSubKeyNames())
                        {
                            using var ifKey = ifRoot.OpenSubKey(ifName, writable: true);
                            ifKey?.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                            ifKey?.SetValue("TcpNoDelay",      1, RegistryValueKind.DWord);
                        }
                    }
                    log("Nagle : DÉSACTIVÉ.");
                }
                catch (Exception ex) { log($"Nagle : erreur — {ex.Message}"); }
            }
            else if (doNagle == false)
            {
                try
                {
                    // RESTAURATION : on RETIRE les overrides (global ET par-interface) pour que Windows
                    // reprenne son comportement par défaut. (Avant : seul le global était remis → les
                    // valeurs par-interface posées à la désactivation restaient = Nagle restait désactivé.)
                    using (var gp = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", writable: true))
                    {
                        gp?.DeleteValue("TcpAckFrequency", false);
                        gp?.DeleteValue("TcpNoDelay",      false);
                    }
                    using var ifRoot = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", writable: true);
                    if (ifRoot != null)
                    {
                        foreach (var ifName in ifRoot.GetSubKeyNames())
                        {
                            using var ifKey = ifRoot.OpenSubKey(ifName, writable: true);
                            ifKey?.DeleteValue("TcpAckFrequency", false);
                            ifKey?.DeleteValue("TcpNoDelay",      false);
                        }
                    }
                    log("Nagle : ACTIVÉ (valeurs par défaut restaurées).");
                }
                catch (Exception ex) { log($"Nagle : erreur — {ex.Message}"); }
            }

            // DNS
            if (doDNS.HasValue)
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (doDNS.Value)
                        obj.InvokeMethod("SetDNSServerSearchOrder",
                            new object[] { new string[] { "1.1.1.1", "8.8.8.8" } });
                    else
                        obj.InvokeMethod("SetDNSServerSearchOrder",
                            new object?[] { null });
                    obj.Dispose();
                }
                log($"DNS : {(doDNS.Value ? "1.1.1.1 / 8.8.8.8 (Cloudflare)" : "automatique (DHCP)")}.");
            }
            catch (Exception ex) { log($"DNS : erreur — {ex.Message}"); }

            // Adapter power management
            if (doPower.HasValue)
            try
            {
                const string netClass =
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
                using var root = Registry.LocalMachine.OpenSubKey(netClass, writable: true);
                if (root != null)
                {
                    int pnpVal = doPower.Value ? 24 : 0;
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var k = root.OpenSubKey(sub, writable: true);
                            if (k?.GetValue("DriverDesc") == null) continue;
                            k.SetValue("PnPCapabilities", pnpVal, RegistryValueKind.DWord);
                        }
                        catch { }
                    }
                }
                log($"Adaptateur réseau : mise en veille {(doPower.Value ? "DÉSACTIVÉE" : "ACTIVÉE (par défaut)")}.");
            }
            catch (Exception ex) { log($"Adaptateur réseau : erreur — {ex.Message}"); }

            // WPAD
            if (doWPAD.HasValue)
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp",
                    "DisableWpad", doWPAD.Value ? 1 : 0, RegistryValueKind.DWord);
                log($"WPAD : {(doWPAD.Value ? "DÉSACTIVÉ" : "ACTIVÉ (par défaut)")}.");
            }
            catch (Exception ex) { log($"WPAD : erreur — {ex.Message}"); }
        }

        // ── Vider le cache DNS ────────────────────────────────────────────────

        private async void BtnFlushDNS_Click(object sender, RoutedEventArgs e)
        {
            BtnFlushDNS.IsEnabled = false;
            _main.Log("DNS : vidage du cache…");

            var ok = await Task.Run(() =>
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
                    { UseShellExecute = false, CreateNoWindow = true });
                    p?.WaitForExit(10_000);
                    return true;
                }
                catch { return false; }
            });

            _main.Log(ok ? "Cache DNS vidé avec succès." : "Cache DNS : erreur lors du vidage.");
            Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, ok,
                "Cache DNS vidé", "Erreur lors du vidage du cache DNS — voir le journal.");
            BtnFlushDNS.IsEnabled = true;
        }
    }
}
