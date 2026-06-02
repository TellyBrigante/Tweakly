using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Optimisation_Tool.Helpers
{
    /// <summary>Un relevé réseau ponctuel (1 ping + débit instantané).</summary>
    public struct NetSample
    {
        public double PingMs;    // -1 = perte / timeout
        public double DownMbps;
        public double UpMbps;
    }

    /// <summary>Infos de connexion (rafraîchies moins souvent que le ping).</summary>
    public class NetInfo
    {
        public string Type       = "—";   // Ethernet / Wi-Fi
        public string Adapter    = "—";
        public string LinkSpeed  = "—";
        public string LocalIp    = "—";
        public string Gateway    = "—";
        public string Dns        = "—";
        public int    WifiSignal = -1;     // % ou -1 si non Wi-Fi
    }

    /// <summary>
    /// Mesure de qualité réseau (ping/jitter/perte via la série de pings, débit via
    /// les compteurs de l'interface). 100 % local — aucun appel à un service tiers.
    /// </summary>
    public sealed class NetworkMonitor
    {
        public static NetworkMonitor Instance { get; } = new NetworkMonitor();

        private long _prevRx, _prevTx;
        private DateTime _prevStamp;
        private bool _haveBase;

        private NetworkMonitor() { }

        public async Task<NetSample> CollectAsync(string target)
        {
            var s = new NetSample { PingMs = -1 };

            // ── Ping (une instance neuve par appel : pas de send concurrent) ──
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(target, 1000);
                s.PingMs = reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
            }
            catch { s.PingMs = -1; }

            // ── Débit (delta des compteurs de l'interface primaire) ──
            try
            {
                var ni = GetPrimary();
                if (ni != null)
                {
                    var st = ni.GetIPv4Statistics();
                    long rx = st.BytesReceived, tx = st.BytesSent;
                    var now = DateTime.UtcNow;
                    if (_haveBase)
                    {
                        double sec = (now - _prevStamp).TotalSeconds;
                        if (sec > 0)
                        {
                            s.DownMbps = Math.Max(0, (rx - _prevRx) * 8.0 / 1_000_000.0 / sec);
                            s.UpMbps   = Math.Max(0, (tx - _prevTx) * 8.0 / 1_000_000.0 / sec);
                        }
                    }
                    _prevRx = rx; _prevTx = tx; _prevStamp = now; _haveBase = true;
                }
            }
            catch { }

            return s;
        }

        public NetInfo GetInfo()
        {
            var info = new NetInfo();
            try
            {
                var ni = GetPrimary();
                if (ni == null) return info;

                info.Type    = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
                info.Adapter = ni.Name;

                if (ni.Speed > 0)
                    info.LinkSpeed = ni.Speed >= 1_000_000_000
                        ? $"{ni.Speed / 1_000_000_000.0:0.#} Gbps"
                        : $"{ni.Speed / 1_000_000.0:0} Mbps";

                var ip = ni.GetIPProperties();
                var v4 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 != null) info.LocalIp = v4.Address.ToString();
                var gw = ip.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                if (gw != null) info.Gateway = gw.Address.ToString();
                var dns = ip.DnsAddresses.FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork);
                if (dns != null) info.Dns = dns.ToString();

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    info.WifiSignal = ReadWifiSignal();
            }
            catch { }
            return info;
        }

        /// <summary>IP de la passerelle (routeur), pour la cible "Passerelle".</summary>
        public string GetGatewayIp()
        {
            try
            {
                var gw = GetPrimary()?.GetIPProperties().GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                return gw?.Address.ToString() ?? "";
            }
            catch { return ""; }
        }

        // Interface "primaire" = celle qui est Up, non-loopback, avec une passerelle IPv4 valide.
        private static NetworkInterface? GetPrimary()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                             && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .Where(n => n.GetIPProperties().GatewayAddresses
                                 .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                        && !g.Address.ToString().StartsWith("0.")))
                    .OrderByDescending(n => n.Speed)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static int ReadWifiSignal()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                });
                if (p == null) return -1;
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                // "Signal" est le libellé en EN comme en FR ; on prend le 1er pourcentage qui suit.
                var m = Regex.Match(outp, @"Signal[^\d]*(\d{1,3})\s*%");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            catch { }
            return -1;
        }
    }
}
