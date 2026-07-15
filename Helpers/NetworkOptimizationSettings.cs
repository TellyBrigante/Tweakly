using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;

namespace Optimisation_Tool.Helpers
{
    public static class NetworkOptimizationSettings
    {
        private const string TcpParameters = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
        private const string TcpInterfaces = TcpParameters + @"\Interfaces";
        private static readonly string[] OptimizedDns = { "1.1.1.1", "8.8.8.8" };

        public static bool TryReadNagle(out bool disabled, out string error)
        {
            disabled = false;
            error = "";
            try
            {
                bool global = VerifiedRegistry.IsDword(
                    Registry.LocalMachine, TcpParameters, "TcpAckFrequency", 1) &&
                    VerifiedRegistry.IsDword(
                    Registry.LocalMachine, TcpParameters, "TcpNoDelay", 1);
                if (!global) return true;

                List<string> interfaces = FindActiveInterfaceKeys();
                if (interfaces.Count == 0)
                {
                    error = "Aucune interface TCP active n'a été trouvée dans le registre.";
                    return false;
                }
                disabled = interfaces.All(path =>
                    VerifiedRegistry.IsDword(Registry.LocalMachine, path, "TcpAckFrequency", 1) &&
                    VerifiedRegistry.IsDword(Registry.LocalMachine, path, "TcpNoDelay", 1));
                return true;
            }
            catch (Exception ex)
            {
                error = $"Lecture de Nagle impossible : {ex.Message}";
                return false;
            }
        }

        public static bool TrySetNagle(bool disabled, out string message)
        {
            var snapshots = new List<RegistryValueSnapshot>();
            try
            {
                List<string> interfaces = FindActiveInterfaceKeys();
                if (interfaces.Count == 0)
                    throw new InvalidOperationException("aucune interface TCP active dans le registre");

                var targets = new List<string> { TcpParameters };
                targets.AddRange(interfaces);
                foreach (string path in targets)
                {
                    snapshots.Add(CaptureRegistryValue(path, "TcpAckFrequency"));
                    snapshots.Add(CaptureRegistryValue(path, "TcpNoDelay"));
                }

                foreach (string path in targets)
                {
                    if (disabled)
                    {
                        VerifiedRegistry.SetDword(Registry.LocalMachine, path, "TcpAckFrequency", 1);
                        VerifiedRegistry.SetDword(Registry.LocalMachine, path, "TcpNoDelay", 1);
                    }
                    else
                    {
                        VerifiedRegistry.DeleteValue(Registry.LocalMachine, path, "TcpAckFrequency");
                        VerifiedRegistry.DeleteValue(Registry.LocalMachine, path, "TcpNoDelay");
                    }
                }

                if (!TryReadNagle(out bool actual, out string readError))
                    throw new InvalidOperationException(readError);
                if (actual != disabled)
                    throw new InvalidOperationException("Windows n'a pas conserve l'etat TCP demande");

                message = disabled
                    ? $"Nagle : DÉSACTIVÉ sur {Math.Max(0, targets.Count - 1)} interface(s) active(s)."
                    : "Nagle : ACTIVÉ (valeurs Windows par défaut restaurées).";
                return true;
            }
            catch (Exception ex)
            {
                string rollback = RestoreRegistryValues(snapshots, out string rollbackError)
                    ? " Les valeurs précédentes ont été restaurées."
                    : $" Restauration incomplète — {rollbackError}";
                message = $"Nagle : erreur — {ex.Message}.{rollback}";
                return false;
            }
        }

        public static bool TryReadDns(out bool optimized, out string error)
        {
            optimized = false;
            error = "";
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
                var states = new List<string[]>();
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                        states.Add((obj["DNSServerSearchOrder"] as string[]) ?? Array.Empty<string>());
                }

                if (states.Count == 0)
                {
                    error = "Aucun adaptateur réseau IP actif n'a été trouvé.";
                    return false;
                }

                optimized = states.All(IsOptimizedDns);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Lecture DNS impossible : {ex.Message}";
                return false;
            }
        }

        public static bool TrySetDns(bool optimized, out string message)
        {
            var snapshots = new List<DnsAdapterSnapshot>();
            try
            {
                bool restartRequired = false;
                int changed = 0;
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        snapshots.Add(CaptureDnsAdapter(obj));
                        object? result = optimized
                            ? obj.InvokeMethod("SetDNSServerSearchOrder", new object[] { OptimizedDns })
                            : obj.InvokeMethod("SetDNSServerSearchOrder", new object?[] { null });
                        uint code = result == null ? 0 : Convert.ToUInt32(result);
                        if (code == 1) restartRequired = true;
                        else if (code != 0)
                            throw new InvalidOperationException($"Windows a refusé le changement DNS (code WMI {code})");
                        changed++;
                    }
                }

                if (changed == 0)
                    throw new InvalidOperationException("aucun adaptateur réseau IP actif");

                if (optimized)
                {
                    if (!TryReadDns(out bool actual, out string readError))
                        throw new InvalidOperationException(readError);
                    if (!actual)
                        throw new InvalidOperationException("les serveurs DNS demandés ne sont pas actifs sur tous les adaptateurs");
                }
                else if (!AllAdaptersUseAutomaticDns())
                {
                    throw new InvalidOperationException("Windows n'a pas restauré le DNS automatique sur tous les adaptateurs");
                }

                string restart = restartRequired ? " — redémarrage requis" : "";
                message = $"DNS : {(optimized ? "1.1.1.1 / 8.8.8.8" : "automatique (DHCP)")} sur {changed} adaptateur(s){restart}.";
                return true;
            }
            catch (Exception ex)
            {
                string rollback = RestoreDnsAdapters(snapshots, out string rollbackError)
                    ? " Les DNS précédents ont été restaurés."
                    : $" Restauration DNS incomplète — {rollbackError}";
                message = $"DNS : erreur — {ex.Message}.{rollback}";
                return false;
            }
        }

        private static bool IsOptimizedDns(string[] dns)
            => dns.Length >= OptimizedDns.Length &&
               dns[0].Equals(OptimizedDns[0], StringComparison.OrdinalIgnoreCase) &&
               dns[1].Equals(OptimizedDns[1], StringComparison.OrdinalIgnoreCase);

        private static bool AllAdaptersUseAutomaticDns()
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SettingID FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
            int count = 0;
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    string id = Convert.ToString(obj["SettingID"]) ?? "";
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    count++;
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{TcpInterfaces}\{{{id.Trim().Trim('{', '}')}}}");
                    string nameServer = Convert.ToString(key?.GetValue("NameServer")) ?? "";
                    if (!string.IsNullOrWhiteSpace(nameServer)) return false;
                }
            }
            return count > 0;
        }

        private static List<string> FindActiveInterfaceKeys()
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                               item.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                               item.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(item => item.Id.Trim().Trim('{', '}'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = new List<string>();
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(TcpInterfaces);
            if (root == null) return result;
            foreach (string name in root.GetSubKeyNames())
            {
                if (active.Contains(name.Trim().Trim('{', '}')))
                    result.Add($@"{TcpInterfaces}\{name}");
            }
            return result;
        }

        private static RegistryValueSnapshot CaptureRegistryValue(string path, string name)
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            if (key == null)
                throw new InvalidOperationException($"clé réseau inaccessible : {path}");

            object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null)
                return new RegistryValueSnapshot(path, name, false, null, RegistryValueKind.None);
            return new RegistryValueSnapshot(path, name, true, value, key.GetValueKind(name));
        }

        private static bool RestoreRegistryValues(
            IEnumerable<RegistryValueSnapshot> snapshots,
            out string error)
        {
            var failures = new List<string>();
            foreach (RegistryValueSnapshot snapshot in snapshots.Reverse())
            {
                try
                {
                    if (!snapshot.Existed)
                    {
                        VerifiedRegistry.DeleteValue(
                            Registry.LocalMachine, snapshot.Path, snapshot.Name);
                        continue;
                    }

                    using (RegistryKey key = Registry.LocalMachine.CreateSubKey(
                        snapshot.Path, writable: true)
                        ?? throw new InvalidOperationException($"clé inaccessible : {snapshot.Path}"))
                    {
                        key.SetValue(snapshot.Name, snapshot.Value!, snapshot.Kind);
                        key.Flush();
                    }

                    using RegistryKey? verify = Registry.LocalMachine.OpenSubKey(snapshot.Path);
                    object? actual = verify?.GetValue(
                        snapshot.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (!Equals(actual, snapshot.Value))
                        throw new InvalidOperationException($"{snapshot.Name} non restauré");
                }
                catch (Exception ex)
                {
                    failures.Add(ex.Message);
                }
            }

            error = string.Join(" ; ", failures.Distinct(StringComparer.OrdinalIgnoreCase));
            return failures.Count == 0;
        }

        private static DnsAdapterSnapshot CaptureDnsAdapter(ManagementObject adapter)
        {
            string settingId = NormalizeId(Convert.ToString(adapter["SettingID"]) ?? "");
            if (string.IsNullOrWhiteSpace(settingId))
                throw new InvalidOperationException("adaptateur DNS sans identifiant");

            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"{TcpInterfaces}\{{{settingId}}}");
            string configured = Convert.ToString(key?.GetValue("NameServer")) ?? "";
            bool automatic = string.IsNullOrWhiteSpace(configured);
            string[] servers = (adapter["DNSServerSearchOrder"] as string[]) ?? Array.Empty<string>();
            return new DnsAdapterSnapshot(settingId, automatic, servers);
        }

        private static bool RestoreDnsAdapters(
            IReadOnlyCollection<DnsAdapterSnapshot> snapshots,
            out string error)
        {
            if (snapshots.Count == 0)
            {
                error = "";
                return true;
            }

            var failures = new List<string>();
            try
            {
                var byId = snapshots.ToDictionary(
                    item => item.SettingId,
                    StringComparer.OrdinalIgnoreCase);
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        string id = NormalizeId(Convert.ToString(obj["SettingID"]) ?? "");
                        if (!byId.TryGetValue(id, out DnsAdapterSnapshot? snapshot)) continue;
                        object? result = snapshot.Automatic
                            ? obj.InvokeMethod("SetDNSServerSearchOrder", new object?[] { null })
                            : obj.InvokeMethod("SetDNSServerSearchOrder", new object[] { snapshot.Servers });
                        uint code = result == null ? 0 : Convert.ToUInt32(result);
                        if (code is not 0 and not 1)
                            failures.Add($"adaptateur {id}, code WMI {code}");
                    }
                }

                foreach (DnsAdapterSnapshot snapshot in snapshots)
                {
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                        $@"{TcpInterfaces}\{{{snapshot.SettingId}}}");
                    string configured = Convert.ToString(key?.GetValue("NameServer")) ?? "";
                    if (snapshot.Automatic && !string.IsNullOrWhiteSpace(configured))
                        failures.Add($"adaptateur {snapshot.SettingId} resté en DNS manuel");
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
            }

            error = string.Join(" ; ", failures.Distinct(StringComparer.OrdinalIgnoreCase));
            return failures.Count == 0;
        }

        private static string NormalizeId(string value)
            => value.Trim().Trim('{', '}');

        private sealed record RegistryValueSnapshot(
            string Path,
            string Name,
            bool Existed,
            object? Value,
            RegistryValueKind Kind);

        private sealed record DnsAdapterSnapshot(
            string SettingId,
            bool Automatic,
            string[] Servers);
    }
}
