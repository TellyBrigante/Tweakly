using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace Optimisation_Tool.Helpers
{
    public static class NetworkAdapterPower
    {
        private const int DisableMask = 0x18;
        private const string NetworkClassPath =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

        public static bool TryRead(out bool disabled, out string error)
        {
            disabled = false;
            error = "";
            try
            {
                List<AdapterEntry> adapters = FindActiveAdapters();
                if (adapters.Count == 0)
                {
                    error = "Aucun adaptateur réseau actif modifiable n'a été trouvé.";
                    return false;
                }

                disabled = adapters.All(item => (item.CurrentValue & DisableMask) == DisableMask);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Lecture de la gestion d'alimentation réseau impossible : {ex.Message}";
                return false;
            }
        }

        public static bool TrySet(bool disabled, out string message)
        {
            List<AdapterEntry> adapters;
            try
            {
                adapters = FindActiveAdapters();
            }
            catch (Exception ex)
            {
                message = $"Adaptateurs réseau : détection impossible — {ex.Message}";
                return false;
            }

            if (adapters.Count == 0)
            {
                message = "Adaptateurs réseau : aucun adaptateur actif modifiable n'a été trouvé.";
                return false;
            }

            var changed = new List<AdapterEntry>();
            try
            {
                foreach (AdapterEntry adapter in adapters)
                {
                    int updated = RegistryValueLogic.SetMaskedBits(adapter.CurrentValue, DisableMask, disabled);
                    if (updated == adapter.CurrentValue) continue;

                    using RegistryKey key = Registry.LocalMachine.OpenSubKey(adapter.RegistryPath, writable: true)
                        ?? throw new InvalidOperationException($"clé inaccessible pour {adapter.Name}");
                    changed.Add(adapter);
                    key.SetValue("PnPCapabilities", updated, RegistryValueKind.DWord);
                    key.Flush();
                }

                if (!TryRead(out bool actual, out string readError))
                    throw new InvalidOperationException(readError);
                if (actual != disabled)
                    throw new InvalidOperationException("Windows n'a pas conservé l'état demandé sur tous les adaptateurs actifs");

                message = $"Adaptateurs réseau : mise en veille {(disabled ? "DÉSACTIVÉE" : "RESTAURÉE")} sur {adapters.Count} adaptateur(s).";
                return true;
            }
            catch (Exception ex)
            {
                bool rolledBack = RollBack(changed, out string rollbackError);
                message = rolledBack
                    ? $"Adaptateurs réseau : modification annulée — {ex.Message}. Les valeurs précédentes ont été restaurées."
                    : $"Adaptateurs réseau : échec — {ex.Message}. Restauration incomplète — {rollbackError}.";
                return false;
            }
        }

        private static List<AdapterEntry> FindActiveAdapters()
        {
            var activeIds = NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                               item.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                               item.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(item => NormalizeId(item.Id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = new List<AdapterEntry>();
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(NetworkClassPath);
            if (root == null) return result;

            foreach (string subKeyName in root.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? key = root.OpenSubKey(subKeyName);
                    if (key == null) continue;

                    string instanceId = Convert.ToString(key.GetValue("NetCfgInstanceId")) ?? "";
                    if (!activeIds.Contains(NormalizeId(instanceId))) continue;

                    string name = Convert.ToString(key.GetValue("DriverDesc")) ?? instanceId;
                    object? raw = key.GetValue("PnPCapabilities");
                    int current = raw == null ? 0 : Convert.ToInt32(raw);
                    result.Add(new AdapterEntry($@"{NetworkClassPath}\{subKeyName}", name, current, raw != null));
                }
                catch (Exception ex)
                {
                    // Une ancienne entrée protégée ne doit pas bloquer l'adaptateur actif suivant.
                    AppLog.ErrorOnce("network-adapter-power-entry",
                        "Gestion d'alimentation réseau : entrée pilote inaccessible", ex);
                }
            }
            return result;
        }

        private static string NormalizeId(string value)
            => value.Trim().Trim('{', '}');

        private static bool RollBack(IEnumerable<AdapterEntry> adapters, out string error)
        {
            var failures = new List<string>();
            foreach (AdapterEntry adapter in adapters)
            {
                try
                {
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(adapter.RegistryPath, writable: true);
                    if (key == null) continue;
                    if (adapter.ValueExisted)
                        key.SetValue("PnPCapabilities", adapter.CurrentValue, RegistryValueKind.DWord);
                    else
                        key.DeleteValue("PnPCapabilities", throwOnMissingValue: false);
                    key.Flush();

                    using RegistryKey? verify = Registry.LocalMachine.OpenSubKey(adapter.RegistryPath);
                    object? actual = verify?.GetValue("PnPCapabilities", null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    bool restored = adapter.ValueExisted
                        ? actual != null && Convert.ToInt32(actual) == adapter.CurrentValue
                        : actual == null;
                    if (!restored) failures.Add(adapter.Name + " non restauré");
                }
                catch (Exception ex)
                {
                    failures.Add(adapter.Name + " : " + ex.Message);
                }
            }
            error = string.Join(" ; ", failures.Distinct(StringComparer.OrdinalIgnoreCase));
            return failures.Count == 0;
        }

        private sealed record AdapterEntry(string RegistryPath, string Name, int CurrentValue, bool ValueExisted);
    }
}
