using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Optimisation_Tool.Helpers;

public enum RestoreStepState
{
    Ok,
    Skipped,
    Error
}

public sealed record RestoreStepResult(
    string Group,
    string Name,
    RestoreStepState State,
    string Detail);

public sealed class RestoreDefaultsReport
{
    public List<RestoreStepResult> Steps { get; } = new();
    public bool NeedsRestart { get; set; }

    public int OkCount => Steps.Count(s => s.State == RestoreStepState.Ok);
    public int SkippedCount => Steps.Count(s => s.State == RestoreStepState.Skipped);
    public int ErrorCount => Steps.Count(s => s.State == RestoreStepState.Error);
}

public static class WindowsDefaultsRestorer
{
    public static RestoreDefaultsReport RestoreAll(Action<string>? log = null)
    {
        var report = new RestoreDefaultsReport();

        RestoreCpu(report);
        RestoreWindows(report);
        RestoreNetwork(report);
        RestorePrivacy(report);
        RestoreAppsTouchedByTweakly(report);

        foreach (var step in report.Steps)
        {
            var prefix = step.State switch
            {
                RestoreStepState.Ok => "OK",
                RestoreStepState.Skipped => "ignore",
                _ => "erreur"
            };
            log?.Invoke($"Defauts Windows : {prefix} - {step.Group} / {step.Name} - {step.Detail}");
        }

        return report;
    }

    private static void RestoreCpu(RestoreDefaultsReport report)
    {
        Step(report, "CPU", "Plan d'alimentation", () =>
        {
            if (!PowerPlanManager.TrySetUltimate(false, out string result))
                throw new InvalidOperationException(result);
            return result;
        });

        Step(report, "CPU", "Power Throttling", () =>
        {
            VerifiedRegistry.DeleteValue(Registry.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                "PowerThrottlingOff");
            return "Override supprime, Windows gere le bridage normalement.";
        });

        Step(report, "CPU", "SystemResponsiveness", () =>
        {
            VerifiedRegistry.SetDword(
                Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "SystemResponsiveness",
                RegistryValueLogic.SystemResponsivenessDefault);
            return $"{RegistryValueLogic.SystemResponsivenessDefault}.";
        });

        Step(report, "CPU", "Memory Integrity (HVCI)", () =>
        {
            VerifiedRegistry.DeleteValue(
                Registry.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                "Enabled");
            report.NeedsRestart = true;
            return "Override supprime, Windows reprend son comportement par defaut.";
        });
    }

    private static void RestoreWindows(RestoreDefaultsReport report)
    {
        Step(report, "Windows", "HAGS", () =>
        {
            VerifiedRegistry.DeleteValue(Registry.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                "HwSchMode");
            report.NeedsRestart = true;
            return "Override supprime, Windows reprend son comportement par defaut.";
        });

        Step(report, "Windows", "Barre de jeu Xbox", () =>
        {
            VerifiedRegistry.SetDword(
                Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
                "AppCaptureEnabled", 1);
            VerifiedRegistry.SetDword(
                Registry.CurrentUser,
                @"System\GameConfigStore",
                "GameDVR_Enabled", 1);
            return "Activee.";
        });

        Step(report, "Windows", "Enregistrement DVR", () =>
        {
            VerifiedRegistry.SetDword(
                Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
                "HistoricalCaptureEnabled", 1);
            return "Active.";
        });

        Step(report, "Windows", "Priorite GPU jeux", () =>
        {
            const string path =
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
            VerifiedRegistry.SetDword(Registry.LocalMachine, path, "GPU Priority", 8);
            VerifiedRegistry.SetDword(Registry.LocalMachine, path, "Priority", 2);
            VerifiedRegistry.SetString(Registry.LocalMachine, path, "Scheduling Category", "Medium");
            VerifiedRegistry.SetString(Registry.LocalMachine, path, "SFIO Priority", "Normal");
            return "8 / 2 / Medium / Normal.";
        });

        Step(report, "Windows", "Mode MSI GPU", () =>
        {
            if (!GpuMsiMode.TryGetRegistryPath(out _))
                return Skip("Aucun GPU PCI compatible avec le mode MSI n'a ete trouve.");

            return Skip("Valeur definie par le pilote GPU; reglage laisse intact.");
        });

        Step(report, "Windows", "Mode Jeu", () =>
        {
            VerifiedRegistry.DeleteValue(Registry.CurrentUser,
                @"Software\Microsoft\GameBar",
                "AutoGameModeEnabled");
            return "Override supprime, comportement Windows par defaut.";
        });

        Step(report, "Windows", "Jeux fenetres", () =>
        {
            const string subKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
            using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
            if (key == null) return "Aucun override DirectX present.";

            var raw = key.GetValue("DirectXUserGlobalSettings") as string;
            if (string.IsNullOrWhiteSpace(raw)) return "Aucun override DirectX present.";

            var updated = RegistryValueLogic.SetSemicolonValue(
                raw, "SwapEffectUpgradeEnable", null);
            if (updated == null)
                VerifiedRegistry.DeleteValue(
                    Registry.CurrentUser, subKey, "DirectXUserGlobalSettings");
            else
                VerifiedRegistry.SetString(
                    Registry.CurrentUser, subKey, "DirectXUserGlobalSettings", updated);

            return "Override SwapEffectUpgradeEnable retire.";
        });

        Step(report, "Windows", "Popups accessibilite", () =>
        {
            RestoreAccessibilityHotkey("StickyKeys", 510);
            RestoreAccessibilityHotkey("ToggleKeys", 62);
            RestoreAccessibilityHotkey("Keyboard Response", 126);
            return "Raccourcis clavier reactives.";
        });
    }

    private static void RestoreNetwork(RestoreDefaultsReport report)
    {
        Step(report, "Reseau", "Nagle", () =>
        {
            if (!NetworkOptimizationSettings.TrySetNagle(false, out string result))
                throw new InvalidOperationException(result);
            return result;
        });

        Step(report, "Reseau", "DNS", () =>
        {
            if (!NetworkOptimizationSettings.TrySetDns(false, out string result))
                throw new InvalidOperationException(result);
            return result;
        });

        Step(report, "Reseau", "Mise en veille adaptateurs", () =>
        {
            if (!NetworkAdapterPower.TrySet(false, out string result))
                throw new InvalidOperationException(result);
            return result;
        });

        Step(report, "Reseau", "WPAD", () =>
        {
            VerifiedRegistry.DeleteValue(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp",
                "DisableWpad");
            return "Override retire.";
        });

        Step(report, "Reseau", "NetworkThrottlingIndex", () =>
        {
            VerifiedRegistry.SetDword(
                Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "NetworkThrottlingIndex",
                RegistryValueLogic.NetworkThrottlingDefault);
            report.NeedsRestart = true;
            return $"{RegistryValueLogic.NetworkThrottlingDefault}, redemarrage conseille.";
        });
    }

    private static void RestorePrivacy(RestoreDefaultsReport report)
    {
        Step(report, "Confidentialite", "Telemetrie Windows", () =>
        {
            VerifiedRegistry.DeleteValue(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                "AllowTelemetry");
            RestoreServiceStart("DiagTrack", "auto", 2);
            RestoreServiceStart("dmwappushservice", "demand", 3);
            return "Strategie retiree, services remis en type de demarrage standard.";
        });

        Step(report, "Confidentialite", "Identifiant publicitaire", () =>
        {
            VerifiedRegistry.SetDword(
                Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                "Enabled", 1);
            return "Active.";
        });

        Step(report, "Confidentialite", "Historique d'activite", () =>
        {
            VerifiedRegistry.DeleteValue(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\System",
                "EnableActivityFeed");
            return "Strategie retiree.";
        });

        Step(report, "Confidentialite", "Recherche Bing", () =>
        {
            VerifiedRegistry.DeleteValue(Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
                "BingSearchEnabled");
            return "Override retire.";
        });

        Step(report, "Confidentialite", "Personnalisation de saisie", () =>
        {
            VerifiedRegistry.DeleteValue(Registry.CurrentUser,
                @"SOFTWARE\Microsoft\InputPersonalization",
                "RestrictImplicitInkCollection");
            VerifiedRegistry.DeleteValue(Registry.CurrentUser,
                @"SOFTWARE\Microsoft\InputPersonalization",
                "RestrictImplicitTextCollection");
            return "Overrides retires.";
        });

        Step(report, "Confidentialite", "Localisation", () =>
        {
            VerifiedRegistry.DeleteValue(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                "DisableLocation");
            return "Strategie retiree.";
        });

        Step(report, "Confidentialite", "Rapports d'erreurs Windows", () =>
        {
            VerifiedRegistry.DeleteValue(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
                "Disabled");
            RestoreServiceStart("WerSvc", "demand", 3);
            return "Strategie retiree, service remis en manuel.";
        });

        Step(report, "Confidentialite", "Experiences personnalisees", () =>
        {
            VerifiedRegistry.SetDword(
                Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy",
                "TailoredExperiencesWithDiagnosticDataEnabled", 1);
            return "Activees.";
        });

        Step(report, "Confidentialite", "Inventaire applications", () =>
        {
            VerifiedRegistry.DeleteValue(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\AppCompat",
                "DisableInventory");
            return "Strategie retiree.";
        });
    }

    private static void RestoreAppsTouchedByTweakly(RestoreDefaultsReport report)
    {
        Step(report, "Applications", "Acceleration materielle Discord", () =>
        {
            if (Process.GetProcessesByName("Discord").Length > 0)
                return Skip("Discord est ouvert, fichier laisse intact.");

            var path = FindDiscordSettingsPath();
            if (path == null) return Skip("settings.json introuvable.");

            JsonSettingsEditor.SetBooleanAtomically(path, "enableHardwareAcceleration", true);
            return "Activee.";
        });

        Step(report, "Applications", "Overlay Steam", () =>
        {
            if (Process.GetProcessesByName("steam").Length > 0)
                return Skip("Steam est ouvert, fichier laisse intact.");

            var path = FindSteamLocalConfigPath();
            if (path == null) return Skip("localconfig.vdf introuvable.");

            var content = File.ReadAllText(path);
            if (!Regex.IsMatch(content, @"""EnableGameOverlay""\s+""\d"""))
                return Skip("Entree EnableGameOverlay introuvable.");

            content = Regex.Replace(content,
                @"""EnableGameOverlay""\s+""\d""",
                "\"EnableGameOverlay\"\t\t\"1\"");
            File.WriteAllText(path, content);
            return "Active.";
        });
    }

    private static void Step(RestoreDefaultsReport report, string group, string name, Func<string> action)
    {
        try
        {
            var detail = action();
            report.Steps.Add(new RestoreStepResult(group, name, RestoreStepState.Ok, detail));
        }
        catch (RestoreSkippedException ex)
        {
            report.Steps.Add(new RestoreStepResult(group, name, RestoreStepState.Skipped, ex.Message));
        }
        catch (Exception ex)
        {
            report.Steps.Add(new RestoreStepResult(group, name, RestoreStepState.Error, ex.Message));
        }
    }

    private static string Skip(string detail) => throw new RestoreSkippedException(detail);

    private static void RestoreAccessibilityHotkey(string subKey, int defaultValue)
    {
        var path = $@"HKEY_CURRENT_USER\Control Panel\Accessibility\{subKey}";
        var raw = Registry.GetValue(path, "Flags", null)?.ToString();
        var flags = int.TryParse(raw, out var parsed) ? parsed : defaultValue;
        VerifiedRegistry.SetString(
            Registry.CurrentUser,
            $@"Control Panel\Accessibility\{subKey}",
            "Flags",
            RegistryValueLogic.EnsureBits(flags, 0x4).ToString());
    }

    private static void RestoreServiceStart(string name, string startType, int expected)
    {
        using RegistryKey? service = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{name}");
        if (service == null) return;

        RunCmd("sc", $"config \"{name}\" start= {startType}", 10_000);
        if (!VerifiedRegistry.IsDword(
                Registry.LocalMachine,
                $@"SYSTEM\CurrentControlSet\Services\{name}",
                "Start",
                expected))
            throw new InvalidOperationException(
                $"Windows n'a pas conserve le type de demarrage du service {name}.");
    }

    private static void RunCmd(string exe, string args, int timeoutMs)
    {
        ProcessCommandResult result = ProcessCommand.Run(exe, args, timeoutMs);
        if (!result.Success)
        {
            string detail = !string.IsNullOrWhiteSpace(result.Error)
                ? result.Error
                : !string.IsNullOrWhiteSpace(result.Output)
                    ? result.Output.Trim()
                    : $"code {result.ExitCode}";
            if (result.TimedOut)
                throw new TimeoutException($"{exe} : {detail}");
            throw new InvalidOperationException($"{exe} : {detail}");
        }
    }

    private static string? FindDiscordSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = Path.Combine(appData, "discord", "settings.json");
        return File.Exists(path) ? path : null;
    }

    private static string? FindSteamLocalConfigPath()
    {
        try
        {
            var steamPath = Registry.GetValue(
                @"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamPath", null)?.ToString();
            if (steamPath == null) return null;

            var userdata = Path.Combine(steamPath, "userdata");
            if (!Directory.Exists(userdata)) return null;

            foreach (var userDir in Directory.GetDirectories(userdata))
            {
                var vdf = Path.Combine(userDir, "config", "localconfig.vdf");
                if (File.Exists(vdf)) return vdf;
            }
        }
        catch { }

        return null;
    }

    private sealed class RestoreSkippedException(string message) : Exception(message);
}
