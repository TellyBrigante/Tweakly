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

        private async void BtnAppliquer_Click(object sender, RoutedEventArgs e)
        {
            BtnAppliquer.IsEnabled = false;

            bool doNagle   = ChkNagle.IsChecked        == true;
            bool doDNS     = ChkNetDNS.IsChecked       == true;
            bool doPower   = ChkAdapterPower.IsChecked == true;
            bool doWPAD    = ChkWPAD.IsChecked         == true;

            _main.Log("Réseau : application des tweaks…");

            await Task.Run(() =>
                ApplyChanges(doNagle, doDNS, doPower, doWPAD,
                             msg => _main.Log(msg)));

            _main.Log("Réseau : tweaks appliqués.");
            BtnAppliquer.IsEnabled = true;
        }

        private static void ApplyChanges(
            bool doNagle, bool doDNS, bool doPower, bool doWPAD,
            Action<string> log)
        {
            // Nagle
            if (doNagle)
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
            else
            {
                try
                {
                    const string tcpPath =
                        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
                    Registry.SetValue(tcpPath, "TcpAckFrequency", 2, RegistryValueKind.DWord);
                    Registry.SetValue(tcpPath, "TcpNoDelay",      0, RegistryValueKind.DWord);
                    log("Nagle : ACTIVÉ (par défaut).");
                }
                catch (Exception ex) { log($"Nagle : erreur — {ex.Message}"); }
            }

            // DNS
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (doDNS)
                        obj.InvokeMethod("SetDNSServerSearchOrder",
                            new object[] { new string[] { "1.1.1.1", "8.8.8.8" } });
                    else
                        obj.InvokeMethod("SetDNSServerSearchOrder",
                            new object?[] { null });
                    obj.Dispose();
                }
                log($"DNS : {(doDNS ? "1.1.1.1 / 8.8.8.8 (Cloudflare)" : "automatique (DHCP)")}.");
            }
            catch (Exception ex) { log($"DNS : erreur — {ex.Message}"); }

            // Adapter power management
            try
            {
                const string netClass =
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
                using var root = Registry.LocalMachine.OpenSubKey(netClass, writable: true);
                if (root != null)
                {
                    int pnpVal = doPower ? 24 : 0;
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
                log($"Adaptateur réseau : mise en veille {(doPower ? "DÉSACTIVÉE" : "ACTIVÉE (par défaut)")}.");
            }
            catch (Exception ex) { log($"Adaptateur réseau : erreur — {ex.Message}"); }

            // WPAD
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp",
                    "DisableWpad", doWPAD ? 1 : 0, RegistryValueKind.DWord);
                log($"WPAD : {(doWPAD ? "DÉSACTIVÉ" : "ACTIVÉ (par défaut)")}.");
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
            BtnFlushDNS.IsEnabled = true;
        }
    }
}
