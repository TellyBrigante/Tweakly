using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string MmPath =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

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
            RunCmd("powercfg", "/setactive SCHEME_BALANCED", 15_000);
            return "Equilibre.";
        });

        Step(report, "CPU", "Power Throttling", () =>
        {
            DeleteValue(Registry.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                "PowerThrottlingOff");
            return "Override supprime, Windows gere le bridage normalement.";
        });

        Step(report, "CPU", "SystemResponsiveness", () =>
        {
            Registry.SetValue(MmPath, "SystemResponsiveness",
                RegistryValueLogic.SystemResponsivenessDefault, RegistryValueKind.DWord);
            return $"{RegistryValueLogic.SystemResponsivenessDefault}.";
        });

        Step(report, "CPU", "Memory Integrity (HVCI)", () =>
        {
            return Skip("Valeur dependante de l'installation Windows; reglage laisse intact.");
        });
    }

    private static void RestoreWindows(RestoreDefaultsReport report)
    {
        Step(report, "Windows", "HAGS", () =>
        {
            DeleteValue(Registry.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                "HwSchMode");
            report.NeedsRestart = true;
            return "Override supprime, Windows reprend son comportement par defaut.";
        });

        Step(report, "Windows", "Barre de jeu Xbox", () =>
        {
            Registry.SetValue(
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
                "AppCaptureEnabled", 1, RegistryValueKind.DWord);
            Registry.SetValue(
                @"HKEY_CURRENT_USER\System\GameConfigStore",
                "GameDVR_Enabled", 1, RegistryValueKind.DWord);
            return "Activee.";
        });

        Step(report, "Windows", "Enregistrement DVR", () =>
        {
            Registry.SetValue(
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
                "HistoricalCaptureEnabled", 1, RegistryValueKind.DWord);
            return "Active.";
        });

        Step(report, "Windows", "Priorite GPU jeux", () =>
        {
            const string path =
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
            Registry.SetValue(path, "GPU Priority", 8, RegistryValueKind.DWord);
            Registry.SetValue(path, "Priority", 2, RegistryValueKind.DWord);
            Registry.SetValue(path, "Scheduling Category", "Medium", RegistryValueKind.String);
            Registry.SetValue(path, "SFIO Priority", "Normal", RegistryValueKind.String);
            return "8 / 2 / Medium / Normal.";
        });

        Step(report, "Windows", "Mode MSI GPU", () =>
        {
            var path = FindGpuMsiPath();
            if (path == null)
                return Skip("Aucun GPU Display trouve dans le registre.");

            return Skip("Valeur definie par le pilote GPU; reglage laisse intact.");
        });

        Step(report, "Windows", "Mode Jeu", () =>
        {
            DeleteValue(Registry.CurrentUser,
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
                key.DeleteValue("DirectXUserGlobalSettings", false);
            else
                key.SetValue("DirectXUserGlobalSettings", updated, RegistryValueKind.String);

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
            DeleteValue(Registry.LocalMachine,
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                "TcpAckFrequency");
            DeleteValue(Registry.LocalMachine,
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                "TcpNoDelay");

            using var root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces",
                writable: true);
            if (root != null)
            {
                foreach (var name in root.GetSubKeyNames())
                {
                    using var ifKey = root.OpenSubKey(name, writable: true);
                    ifKey?.DeleteValue("TcpAckFrequency", false);
                    ifKey?.DeleteValue("TcpNoDelay", false);
                }
            }
            return "Overrides TCP retires.";
        });

        Step(report, "Reseau", "DNS", () =>
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
            foreach (ManagementObject obj in searcher.Get())
            {
                obj.InvokeMethod("SetDNSServerSearchOrder", new object?[] { null });
                obj.Dispose();
            }
            return "Automatique via DHCP.";
        });

        Step(report, "Reseau", "Mise en veille adaptateurs", () =>
        {
            return Skip("Valeur propre a chaque pilote reseau; reglages laisses intacts.");
        });

        Step(report, "Reseau", "WPAD", () =>
        {
            DeleteValue(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp",
                "DisableWpad");
            return "Override retire.";
        });

        Step(report, "Reseau", "NetworkThrottlingIndex", () =>
        {
            Registry.SetValue(MmPath, "NetworkThrottlingIndex",
                RegistryValueLogic.NetworkThrottlingDefault, RegistryValueKind.DWord);
            report.NeedsRestart = true;
            return $"{RegistryValueLogic.NetworkThrottlingDefault}, redemarrage conseille.";
        });
    }

    private static void RestorePrivacy(RestoreDefaultsReport report)
    {
        Step(report, "Confidentialite", "Telemetrie Windows", () =>
        {
            DeleteValue(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                "AllowTelemetry");
            RunCmd("sc", "config \"DiagTrack\" start= auto", 10_000, throwOnError: false);
            RunCmd("sc", "config \"dmwappushservice\" start= demand", 10_000, throwOnError: false);
            return "Strategie retiree, services remis en type de demarrage standard.";
        });

        Step(report, "Confidentialite", "Identifiant publicitaire", () =>
        {
            Registry.SetValue(
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                "Enabled", 1, RegistryValueKind.DWord);
            return "Active.";
        });

        Step(report, "Confidentialite", "Historique d'activite", () =>
        {
            DeleteValue(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\System",
                "EnableActivityFeed");
            return "Strategie retiree.";
        });

        Step(report, "Confidentialite", "Recherche Bing", () =>
        {
            DeleteValue(Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
                "BingSearchEnabled");
            return "Override retire.";
        });

        Step(report, "Confidentialite", "Personnalisation de saisie", () =>
        {
            DeleteValue(Registry.CurrentUser,
                @"SOFTWARE\Microsoft\InputPersonalization",
                "RestrictImplicitInkCollection");
            DeleteValue(Registry.CurrentUser,
                @"SOFTWARE\Microsoft\InputPersonalization",
                "RestrictImplicitTextCollection");
            return "Overrides retires.";
        });

        Step(report, "Confidentialite", "Localisation", () =>
        {
            DeleteValue(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                "DisableLocation");
            return "Strategie retiree.";
        });

        Step(report, "Confidentialite", "Rapports d'erreurs Windows", () =>
        {
            DeleteValue(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
                "Disabled");
            RunCmd("sc", "config \"WerSvc\" start= demand", 10_000, throwOnError: false);
            return "Strategie retiree, service remis en manuel.";
        });

        Step(report, "Confidentialite", "Experiences personnalisees", () =>
        {
            Registry.SetValue(
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy",
                "TailoredExperiencesWithDiagnosticDataEnabled", 1, RegistryValueKind.DWord);
            return "Activees.";
        });

        Step(report, "Confidentialite", "Inventaire applications", () =>
        {
            DeleteValue(Registry.LocalMachine,
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

            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node == null) return Skip("settings.json non modifiable.");

            node["enableHardwareAcceleration"] = true;
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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

    private static void DeleteValue(RegistryKey root, string subKey, string valueName)
    {
        using var key = root.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(valueName, false);
    }

    private static void RestoreAccessibilityHotkey(string subKey, int defaultValue)
    {
        var path = $@"HKEY_CURRENT_USER\Control Panel\Accessibility\{subKey}";
        var raw = Registry.GetValue(path, "Flags", null)?.ToString();
        var flags = int.TryParse(raw, out var parsed) ? parsed : defaultValue;
        Registry.SetValue(path, "Flags",
            RegistryValueLogic.EnsureBits(flags, 0x4).ToString(), RegistryValueKind.String);
    }

    private static void RunCmd(string exe, string args, int timeoutMs, bool throwOnError = true)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });

        if (p == null)
        {
            if (throwOnError) throw new InvalidOperationException($"{exe} : lancement impossible.");
            return;
        }

        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (throwOnError) throw new TimeoutException($"{exe} : delai depasse.");
            return;
        }

        if (throwOnError && p.ExitCode != 0)
        {
            var err = p.StandardError.ReadToEnd().Trim();
            if (string.IsNullOrWhiteSpace(err)) err = p.StandardOutput.ReadToEnd().Trim();
            if (string.IsNullOrWhiteSpace(err)) err = $"code {p.ExitCode}";
            throw new InvalidOperationException($"{exe} : {err}");
        }
    }

    private static string? FindGpuMsiPath()
    {
        const string classRoot = @"SYSTEM\CurrentControlSet\Enum";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(classRoot);
            if (root == null) return null;

            foreach (var busName in root.GetSubKeyNames())
            {
                if (!busName.StartsWith("PCI", StringComparison.OrdinalIgnoreCase)) continue;
                using var bus = root.OpenSubKey(busName);
                if (bus == null) continue;

                foreach (var devName in bus.GetSubKeyNames())
                {
                    using var dev = bus.OpenSubKey(devName);
                    if (dev == null) continue;

                    foreach (var instName in dev.GetSubKeyNames())
                    {
                        using var inst = dev.OpenSubKey(instName);
                        var cls = inst?.GetValue("Class")?.ToString() ?? "";
                        if (!cls.Equals("Display", StringComparison.OrdinalIgnoreCase)) continue;

                        return @"HKEY_LOCAL_MACHINE\" + $@"{classRoot}\{busName}\{devName}\{instName}" +
                               @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                    }
                }
            }
        }
        catch { }

        return null;
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
