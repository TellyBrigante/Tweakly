using System;
using Microsoft.Win32;

namespace Optimisation_Tool.Helpers
{
    internal static class CpuStabilityDefaults
    {
        public const int CurrentRevision = 1;

        public static void ApplyIfNeeded(AppSettings settings, Action<string>? log = null)
        {
            if (settings.CpuStabilityDefaultsRevision >= CurrentRevision) return;

            try
            {
                ApplyConservativeDefaults(log);
                settings.CpuStabilityDefaultsRevision = CurrentRevision;
                settings.Save();
            }
            catch (Exception ex)
            {
                AppLog.Error("CpuStabilityDefaults.ApplyIfNeeded", ex);
                log?.Invoke("CPU : impossible de remettre les tweaks stabilité par défaut.");
            }
        }

        private static void ApplyConservativeDefaults(Action<string>? log)
        {
            Registry.SetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                "PowerThrottlingOff", 0, RegistryValueKind.DWord);

            Registry.SetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "SystemResponsiveness", 20, RegistryValueKind.DWord);

            Registry.SetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                "Enabled", 1, RegistryValueKind.DWord);

            log?.Invoke("CPU : Power Throttling, SystemResponsiveness et HVCI remis sur les valeurs Windows par défaut.");
        }
    }
}
