using Microsoft.Win32;
using System;
using System.Collections.Generic;

namespace Optimisation_Tool.Helpers
{
    public static class GpuMsiMode
    {
        private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        private const string EnumPciPath = @"SYSTEM\CurrentControlSet\Enum\PCI";
        private const string MsiSuffix =
            @"Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

        public static bool TryRead(out bool enabled, out string error)
        {
            enabled = false;
            error = "";

            Candidate? candidate = FindBestCandidate();
            if (candidate == null)
            {
                error = "Aucun GPU PCI compatible avec le mode MSI n'a été trouvé.";
                return false;
            }

            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(candidate.MsiPath);
                if (key == null)
                {
                    error = $"Le pilote de {candidate.Name} n'expose pas le réglage MSI.";
                    return false;
                }

                object? value = key.GetValue("MSISupported");
                enabled = value != null && Convert.ToInt32(value) == 1;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Lecture MSI impossible pour {candidate.Name} : {ex.Message}";
                return false;
            }
        }

        public static bool TrySet(bool enabled, out string message)
        {
            Candidate? candidate = FindBestCandidate();
            if (candidate == null)
            {
                message = "Mode MSI GPU : aucun GPU PCI compatible n'a été trouvé.";
                return false;
            }

            object? previousValue = null;
            RegistryValueKind previousKind = RegistryValueKind.DWord;
            bool previousValueExisted = false;
            bool writeAttempted = false;
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(candidate.MsiPath, writable: true))
                {
                    if (key == null)
                    {
                        message = $"Mode MSI GPU : le pilote de {candidate.Name} n'expose pas un réglage modifiable.";
                        return false;
                    }

                    previousValue = key.GetValue("MSISupported", null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    previousValueExisted = previousValue != null;
                    if (previousValueExisted) previousKind = key.GetValueKind("MSISupported");
                    writeAttempted = true;
                    key.SetValue("MSISupported", enabled ? 1 : 0, RegistryValueKind.DWord);
                    key.Flush();
                }

                if (!TryReadCandidate(candidate, out bool actual, out string readError))
                {
                    throw new InvalidOperationException("écriture impossible à vérifier — " + readError);
                }
                if (actual != enabled)
                {
                    throw new InvalidOperationException(
                        $"Windows a conservé l'état {(actual ? "activé" : "désactivé")}");
                }

                message = $"Mode MSI GPU : {(enabled ? "ACTIVÉ" : "DÉSACTIVÉ")} sur {candidate.Name} — redémarrage requis.";
                return true;
            }
            catch (Exception ex)
            {
                if (writeAttempted && !TryRestore(
                        candidate, previousValueExisted, previousValue, previousKind, out string rollbackError))
                {
                    message = $"Mode MSI GPU : échec pour {candidate.Name} — {ex.Message}. " +
                              $"Restauration incomplète — {rollbackError}.";
                    return false;
                }

                message = writeAttempted
                    ? $"Mode MSI GPU : modification annulée pour {candidate.Name} — {ex.Message}. L'état précédent a été restauré."
                    : $"Mode MSI GPU : modification refusée pour {candidate.Name} — {ex.Message}";
                return false;
            }
        }

        private static bool TryReadCandidate(Candidate candidate, out bool enabled, out string error)
        {
            enabled = false;
            error = "";
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(candidate.MsiPath);
                if (key == null)
                {
                    error = "clé MSI inaccessible après écriture";
                    return false;
                }

                object? value = key.GetValue("MSISupported", null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null)
                {
                    error = "valeur MSISupported absente après écriture";
                    return false;
                }
                enabled = Convert.ToInt32(value) == 1;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryRestore(
            Candidate candidate,
            bool valueExisted,
            object? value,
            RegistryValueKind valueKind,
            out string error)
        {
            error = "";
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(candidate.MsiPath, writable: true))
                {
                    if (key == null) throw new InvalidOperationException("clé MSI inaccessible");
                    if (valueExisted)
                        key.SetValue("MSISupported", value!, valueKind);
                    else
                        key.DeleteValue("MSISupported", throwOnMissingValue: false);
                    key.Flush();
                }

                using RegistryKey? verify = Registry.LocalMachine.OpenSubKey(candidate.MsiPath);
                object? actual = verify?.GetValue("MSISupported", null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                bool restored = valueExisted ? Equals(actual, value) : actual == null;
                if (!restored) throw new InvalidOperationException("l'état précédent n'a pas été conservé");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppLog.Error("Mode MSI GPU : restauration de l'état précédent", ex);
                return false;
            }
        }

        public static bool TryGetRegistryPath(out string path)
        {
            Candidate? candidate = FindBestCandidate();
            path = candidate?.MsiPath ?? "";
            return candidate != null;
        }

        private static Candidate? FindBestCandidate()
        {
            var candidates = new List<Candidate>();
            try
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(EnumPciPath);
                if (root == null) return null;

                foreach (string deviceName in root.GetSubKeyNames())
                {
                    using RegistryKey? device = root.OpenSubKey(deviceName);
                    if (device == null) continue;

                    foreach (string instanceName in device.GetSubKeyNames())
                    {
                        try
                        {
                            using RegistryKey? instance = device.OpenSubKey(instanceName);
                            if (instance == null || !IsDisplayDevice(instance)) continue;

                            string msiPath = $@"{EnumPciPath}\{deviceName}\{instanceName}\{MsiSuffix}";
                            using RegistryKey? msiKey = Registry.LocalMachine.OpenSubKey(msiPath);
                            if (msiKey == null) continue;

                            string service = Convert.ToString(instance.GetValue("Service")) ?? "";
                            string description = Convert.ToString(instance.GetValue("DeviceDesc")) ?? "";
                            string name = FriendlyName(description, deviceName);
                            int score = CandidateScore(deviceName, service, instance.GetValue("ConfigFlags"));
                            candidates.Add(new Candidate(msiPath, name, score));
                        }
                        catch (Exception ex)
                        {
                            // Une ancienne instance inaccessible ne doit pas masquer le GPU actif suivant.
                            AppLog.ErrorOnce("gpu-msi-instance", "Mode MSI GPU : instance PCI inaccessible", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("gpu-msi-discovery", "Mode MSI GPU : inventaire PCI impossible", ex);
                return null;
            }

            Candidate? best = null;
            foreach (Candidate candidate in candidates)
            {
                if (best == null || candidate.Score > best.Score)
                    best = candidate;
            }
            return best;
        }

        private static bool IsDisplayDevice(RegistryKey instance)
        {
            string className = Convert.ToString(instance.GetValue("Class")) ?? "";
            string classGuid = Convert.ToString(instance.GetValue("ClassGUID")) ?? "";
            return className.Equals("Display", StringComparison.OrdinalIgnoreCase) ||
                   classGuid.Equals(DisplayClassGuid, StringComparison.OrdinalIgnoreCase);
        }

        private static int CandidateScore(string deviceName, string service, object? configFlags)
        {
            int score = 0;
            if (deviceName.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) score += 300;
            else if (deviceName.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)) score += 250;
            else if (deviceName.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) score += 100;

            if (service.Equals("nvlddmkm", StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (service.Contains("amdk", StringComparison.OrdinalIgnoreCase)) score += 90;

            try
            {
                if (configFlags == null || Convert.ToInt32(configFlags) == 0) score += 25;
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("gpu-msi-config-flags", "Mode MSI GPU : état d'une instance PCI illisible", ex);
            }
            return score;
        }

        private static string FriendlyName(string description, string deviceName)
        {
            int separator = description.LastIndexOf(';');
            if (separator >= 0 && separator + 1 < description.Length)
                description = description[(separator + 1)..];
            return string.IsNullOrWhiteSpace(description) ? deviceName : description.Trim();
        }

        private sealed record Candidate(string MsiPath, string Name, int Score);
    }
}
