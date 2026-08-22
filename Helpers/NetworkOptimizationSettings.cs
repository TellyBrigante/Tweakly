using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;

namespace Optimisation_Tool.Helpers
{
    public static class NetworkOptimizationSettings
    {
        private const string TcpParameters = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
        private const string TcpInterfaces = TcpParameters + @"\Interfaces";
        private static readonly string[] OptimizedDns = { "1.1.1.1", "8.8.8.8" };
        private static readonly object UndoSync = new();
        private static readonly JsonSerializerOptions UndoJsonOptions = new() { WriteIndented = true };

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
            bool createdUndo = false;
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

                NetworkUndoState undo = LoadUndoState();
                if (disabled)
                {
                    if (undo.Nagle is not { Count: > 0 })
                    {
                        undo.Nagle = snapshots.Select(ToPersistentNagleSnapshot).ToList();
                        SaveUndoState(undo);
                        createdUndo = true;
                    }
                }
                else
                {
                    if (undo.Nagle is not { Count: > 0 })
                        throw new InvalidOperationException(
                            "aucun état précédent sauvegardé par Tweakly ; les valeurs existantes sont laissées intactes");

                    if (!RestorePersistentNagle(undo.Nagle, out string restoreError))
                        throw new InvalidOperationException(restoreError);
                    if (!PersistentNagleMatches(undo.Nagle, out string verifyError))
                        throw new InvalidOperationException(verifyError);

                    undo.Nagle = null;
                    SaveUndoState(undo);
                    message = "Nagle : état précédent restauré et vérifié exactement.";
                    return true;
                }

                foreach (string path in targets)
                {
                    VerifiedRegistry.SetDword(Registry.LocalMachine, path, "TcpAckFrequency", 1);
                    VerifiedRegistry.SetDword(Registry.LocalMachine, path, "TcpNoDelay", 1);
                }

                if (!TryReadNagle(out bool actual, out string readError))
                    throw new InvalidOperationException(readError);
                if (actual != disabled)
                    throw new InvalidOperationException("Windows n'a pas conserve l'etat TCP demande");

                message = $"Nagle : DÉSACTIVÉ sur {Math.Max(0, targets.Count - 1)} interface(s) active(s), état précédent sauvegardé.";
                return true;
            }
            catch (Exception ex)
            {
                string rollback = RestoreRegistryValues(snapshots, out string rollbackError)
                    ? " Les valeurs précédentes ont été restaurées."
                    : $" Restauration incomplète — {rollbackError}";
                if (createdUndo && rollbackError.Length == 0)
                {
                    try
                    {
                        NetworkUndoState undo = LoadUndoState();
                        undo.Nagle = null;
                        SaveUndoState(undo);
                    }
                    catch (Exception cleanupError)
                    {
                        rollback += " Journal d'annulation conservé : " + cleanupError.Message;
                    }
                }
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
            bool createdUndo = false;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                        snapshots.Add(CaptureDnsAdapter(obj));
                }

                if (snapshots.Count == 0)
                    throw new InvalidOperationException("aucun adaptateur réseau IP actif");

                NetworkUndoState undo = LoadUndoState();
                if (!optimized)
                {
                    if (undo.Dns is not { Count: > 0 })
                        throw new InvalidOperationException(
                            "aucun état DNS précédent sauvegardé par Tweakly ; la configuration existante est laissée intacte");
                    if (!RestoreDnsAdapters(undo.Dns, out string restoreError))
                        throw new InvalidOperationException(restoreError);
                    if (!DnsSnapshotsMatch(undo.Dns, out string verifyError))
                        throw new InvalidOperationException(verifyError);

                    undo.Dns = null;
                    SaveUndoState(undo);
                    message = "DNS : état précédent restauré et vérifié exactement.";
                    return true;
                }

                if (undo.Dns is not { Count: > 0 })
                {
                    undo.Dns = snapshots;
                    SaveUndoState(undo);
                    createdUndo = true;
                }

                bool restartRequired = false;
                int changed = 0;
                using var applySearcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
                foreach (ManagementObject obj in applySearcher.Get())
                {
                    using (obj)
                    {
                        object? result = obj.InvokeMethod(
                            "SetDNSServerSearchOrder", new object[] { OptimizedDns });
                        uint code = result == null ? 0 : Convert.ToUInt32(result);
                        if (code == 1) restartRequired = true;
                        else if (code != 0)
                            throw new InvalidOperationException($"Windows a refusé le changement DNS (code WMI {code})");
                        changed++;
                    }
                }

                if (!TryReadDns(out bool actual, out string readError))
                    throw new InvalidOperationException(readError);
                if (!actual)
                    throw new InvalidOperationException("les serveurs DNS demandés ne sont pas actifs sur tous les adaptateurs");

                string restart = restartRequired ? " — redémarrage requis" : "";
                message = $"DNS : 1.1.1.1 / 8.8.8.8 sur {changed} adaptateur(s), état précédent sauvegardé{restart}.";
                return true;
            }
            catch (Exception ex)
            {
                string rollback = RestoreDnsAdapters(snapshots, out string rollbackError)
                    ? " Les DNS précédents ont été restaurés."
                    : $" Restauration DNS incomplète — {rollbackError}";
                if (createdUndo && rollbackError.Length == 0)
                {
                    try
                    {
                        NetworkUndoState undo = LoadUndoState();
                        undo.Dns = null;
                        SaveUndoState(undo);
                    }
                    catch (Exception cleanupError)
                    {
                        rollback += " Journal d'annulation conservé : " + cleanupError.Message;
                    }
                }
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
                ValidateDnsSnapshots(snapshots);
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

        private static PersistentNagleSnapshot ToPersistentNagleSnapshot(RegistryValueSnapshot snapshot)
        {
            ValidateNagleAddress(snapshot.Path, snapshot.Name);
            if (!snapshot.Existed)
                return new(snapshot.Path, snapshot.Name, false, null);
            if (snapshot.Kind != RegistryValueKind.DWord || snapshot.Value is not int value)
                throw new InvalidOperationException(
                    $"{snapshot.Path}\\{snapshot.Name} n'est pas un DWORD ; optimisation refusée pour préserver sa valeur exacte");
            return new(snapshot.Path, snapshot.Name, true, value);
        }

        private static bool RestorePersistentNagle(
            IReadOnlyCollection<PersistentNagleSnapshot> snapshots,
            out string error)
        {
            var failures = new List<string>();
            foreach (PersistentNagleSnapshot snapshot in snapshots.Reverse())
            {
                try
                {
                    ValidateNagleSnapshot(snapshot);
                    if (snapshot.Existed)
                        VerifiedRegistry.SetDword(
                            Registry.LocalMachine, snapshot.Path, snapshot.Name, snapshot.Value!.Value);
                    else
                        VerifiedRegistry.DeleteValue(
                            Registry.LocalMachine, snapshot.Path, snapshot.Name);
                }
                catch (Exception ex)
                {
                    failures.Add(ex.Message);
                }
            }
            error = string.Join(" ; ", failures.Distinct(StringComparer.OrdinalIgnoreCase));
            return failures.Count == 0;
        }

        private static bool PersistentNagleMatches(
            IReadOnlyCollection<PersistentNagleSnapshot> snapshots,
            out string error)
        {
            var failures = new List<string>();
            foreach (PersistentNagleSnapshot expected in snapshots)
            {
                try
                {
                    ValidateNagleSnapshot(expected);
                    RegistryValueSnapshot actual = CaptureRegistryValue(expected.Path, expected.Name);
                    bool equal = expected.Existed == actual.Existed &&
                                 (!expected.Existed ||
                                  (actual.Kind == RegistryValueKind.DWord &&
                                   actual.Value is int value && value == expected.Value));
                    if (!equal)
                        failures.Add($"{expected.Path}\\{expected.Name} ne correspond pas à la sauvegarde");
                }
                catch (Exception ex)
                {
                    failures.Add(ex.Message);
                }
            }
            error = string.Join(" ; ", failures.Distinct(StringComparer.OrdinalIgnoreCase));
            return failures.Count == 0;
        }

        private static bool DnsSnapshotsMatch(
            IReadOnlyCollection<DnsAdapterSnapshot> snapshots,
            out string error)
        {
            var failures = new List<string>();
            try
            {
                ValidateDnsSnapshots(snapshots);
                var expected = snapshots.ToDictionary(item => item.SettingId, StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        string id = NormalizeId(Convert.ToString(obj["SettingID"]) ?? "");
                        if (!expected.TryGetValue(id, out DnsAdapterSnapshot? saved)) continue;
                        seen.Add(id);
                        DnsAdapterSnapshot actual = CaptureDnsAdapter(obj);
                        bool equal = saved.Automatic == actual.Automatic &&
                                     (saved.Automatic || saved.Servers.SequenceEqual(
                                         actual.Servers, StringComparer.OrdinalIgnoreCase));
                        if (!equal)
                            failures.Add($"adaptateur {id}, DNS restauré différent de la sauvegarde");
                    }
                }
                foreach (string missing in expected.Keys.Except(seen, StringComparer.OrdinalIgnoreCase))
                    failures.Add($"adaptateur {missing} absent pendant la vérification");
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
            }
            error = string.Join(" ; ", failures.Distinct(StringComparer.OrdinalIgnoreCase));
            return failures.Count == 0;
        }

        private static NetworkUndoState LoadUndoState()
        {
            lock (UndoSync)
            {
                if (!File.Exists(PathLayout.NetworkUndoFile))
                    return new NetworkUndoState();

                try
                {
                    byte[] json = File.ReadAllBytes(PathLayout.NetworkUndoFile);
                    NetworkUndoEnvelope envelope = JsonSerializer.Deserialize<NetworkUndoEnvelope>(json, UndoJsonOptions)
                        ?? throw new InvalidOperationException("journal vide");
                    if (envelope.SchemaVersion != 1 || envelope.State is null || string.IsNullOrWhiteSpace(envelope.Digest))
                        throw new InvalidOperationException("format de journal invalide");

                    byte[] stateJson = JsonSerializer.SerializeToUtf8Bytes(envelope.State, UndoJsonOptions);
                    byte[] expected = SHA256.HashData(stateJson);
                    byte[] actual = Convert.FromHexString(envelope.Digest);
                    if (actual.Length != expected.Length ||
                        !CryptographicOperations.FixedTimeEquals(actual, expected))
                        throw new InvalidOperationException("empreinte du journal invalide");

                    if (envelope.State.Nagle is { Count: > 0 })
                        foreach (PersistentNagleSnapshot snapshot in envelope.State.Nagle)
                            ValidateNagleSnapshot(snapshot);
                    if (envelope.State.Dns is { Count: > 0 })
                        ValidateDnsSnapshots(envelope.State.Dns);
                    return envelope.State;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
                {
                    throw new InvalidOperationException(
                        "journal d'annulation réseau illisible ou non fiable ; aucune modification effectuée", ex);
                }
            }
        }

        private static void SaveUndoState(NetworkUndoState state)
        {
            lock (UndoSync)
            {
                Directory.CreateDirectory(PathLayout.Config);
                byte[] stateJson = JsonSerializer.SerializeToUtf8Bytes(state, UndoJsonOptions);
                var envelope = new NetworkUndoEnvelope(
                    1, state, Convert.ToHexString(SHA256.HashData(stateJson)));
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, UndoJsonOptions);
                string temporary = PathLayout.NetworkUndoFile + ".tmp";
                using (var stream = new FileStream(
                    temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(json);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, PathLayout.NetworkUndoFile, overwrite: true);
            }
        }

        private static void ValidateNagleSnapshot(PersistentNagleSnapshot snapshot)
        {
            ValidateNagleAddress(snapshot.Path, snapshot.Name);
            if (snapshot.Existed != snapshot.Value.HasValue)
                throw new InvalidOperationException("instantané Nagle incohérent");
        }

        private static void ValidateNagleAddress(string path, string name)
        {
            if (name is not ("TcpAckFrequency" or "TcpNoDelay"))
                throw new InvalidOperationException("valeur Nagle non autorisée");
            if (string.Equals(path, TcpParameters, StringComparison.OrdinalIgnoreCase))
                return;
            string prefix = TcpInterfaces + "\\";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParse(path[prefix.Length..].Trim('{', '}'), out _))
                throw new InvalidOperationException("chemin Nagle non autorisé");
        }

        private static void ValidateDnsSnapshots(IReadOnlyCollection<DnsAdapterSnapshot> snapshots)
        {
            if (snapshots.Count is < 1 or > 128)
                throw new InvalidOperationException("nombre d'adaptateurs DNS incohérent");
            foreach (DnsAdapterSnapshot snapshot in snapshots)
            {
                if (!Guid.TryParse(snapshot.SettingId, out _))
                    throw new InvalidOperationException("identifiant d'adaptateur DNS invalide");
                if (snapshot.Servers.Length > 16 ||
                    snapshot.Servers.Any(server => !IPAddress.TryParse(server, out _)))
                    throw new InvalidOperationException("adresse DNS sauvegardée invalide");
                if (!snapshot.Automatic && snapshot.Servers.Length == 0)
                    throw new InvalidOperationException("instantané DNS manuel incomplet");
            }
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

        private sealed record PersistentNagleSnapshot(
            string Path,
            string Name,
            bool Existed,
            int? Value);

        private sealed class NetworkUndoState
        {
            public List<PersistentNagleSnapshot>? Nagle { get; set; }
            public List<DnsAdapterSnapshot>? Dns { get; set; }
        }

        private sealed record NetworkUndoEnvelope(
            int SchemaVersion,
            NetworkUndoState State,
            string Digest);
    }
}
