using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Optimisation_Tool.Pages
{
    public partial class PageWindows : UserControl
    {
        private readonly MainWindow _main;
        private bool _loaded = false;

        // État lu au chargement → sert de référence pour n'appliquer que ce qui change
        private (bool HAGS, bool DisableGameBar, bool DisableDVR, bool GPUPriority,
                 bool MSIMode, bool DiscordHWAccel, bool SteamOverlay) _state;

        public PageWindows(MainWindow main)
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

            ChkHAGS.IsChecked           = s.HAGS;
            ChkDisableGameBar.IsChecked = s.DisableGameBar;
            ChkDisableDVR.IsChecked     = s.DisableDVR;
            ChkGPUPriority.IsChecked    = s.GPUPriority;
            ChkMSIMode.IsChecked        = s.MSIMode;
            // Discord et Steam commencent toujours à false (lecture fichier complexe)
            ChkDiscordHWAccel.IsChecked = s.DiscordHWAccel;
            ChkSteamOverlay.IsChecked   = s.SteamOverlay;

            _state = s;
            BtnAppliquer.IsEnabled = true;
            _main.Log("Windows : état chargé.");
        }

        private static (bool HAGS, bool DisableGameBar, bool DisableDVR,
                        bool GPUPriority, bool MSIMode,
                        bool DiscordHWAccel, bool SteamOverlay) ReadState()
        {
            // HAGS
            bool hags = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                    "HwSchMode", null);
                hags = v != null ? Convert.ToInt32(v) == 2
                                 : Environment.OSVersion.Version.Build >= 22621; // Win11 22H2+ : défaut activé
            }
            catch { }

            // Game Bar (AppCaptureEnabled = 0 → désactivé = coché)
            bool disableGameBar = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
                    "AppCaptureEnabled", null);
                disableGameBar = v != null && Convert.ToInt32(v) == 0;
            }
            catch { }

            // DVR
            bool disableDVR = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
                    "HistoricalCaptureEnabled", null);
                disableDVR = v != null && Convert.ToInt32(v) == 0;
            }
            catch { }

            // GPU Priority
            bool gpuPriority = false;
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games",
                    "GPU Priority", null);
                gpuPriority = v != null && Convert.ToInt32(v) >= 8;
            }
            catch { }

            // MSI Mode : chercher le premier GPU PCI dans le registre
            bool msiMode = false;
            try
            {
                var msiPath = FindMSIPath();
                if (msiPath != null)
                {
                    var v = Registry.GetValue(msiPath, "MSISupported", null);
                    msiMode = v != null && Convert.ToInt32(v) == 1;
                }
            }
            catch { }

            // Discord HW Accel
            bool discordHWAccel = false;
            try
            {
                var settingsPath = FindDiscordSettingsPath();
                if (settingsPath != null && File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("enableHardwareAcceleration", out var prop))
                        discordHWAccel = !prop.GetBoolean(); // coché = désactivé dans Discord
                }
            }
            catch { }

            // Steam Overlay
            bool steamOverlay = false;
            try
            {
                var vdfPath = FindSteamLocalConfigPath();
                if (vdfPath != null && File.Exists(vdfPath))
                {
                    var content = File.ReadAllText(vdfPath);
                    var m = Regex.Match(content, @"""EnableGameOverlay""\s+""(\d)""");
                    steamOverlay = m.Success && m.Groups[1].Value == "0";
                }
            }
            catch { }

            return (hags, disableGameBar, disableDVR, gpuPriority, msiMode, discordHWAccel, steamOverlay);
        }

        // ── Appliquer ─────────────────────────────────────────────────────────

        private async void BtnAppliquer_Click(object sender, RoutedEventArgs e)
        {
            BtnAppliquer.IsEnabled = false;

            // N'appliquer QUE ce qui a changé depuis le chargement (null = inchangé → ignoré)
            bool? chHAGS    = Helpers.TweakFeedback.Changed(ChkHAGS,           _state.HAGS);
            bool? chGameBar = Helpers.TweakFeedback.Changed(ChkDisableGameBar, _state.DisableGameBar);
            bool? chDVR     = Helpers.TweakFeedback.Changed(ChkDisableDVR,     _state.DisableDVR);
            bool? chGPU     = Helpers.TweakFeedback.Changed(ChkGPUPriority,    _state.GPUPriority);
            bool? chMSI     = Helpers.TweakFeedback.Changed(ChkMSIMode,        _state.MSIMode);
            bool? chDiscord = Helpers.TweakFeedback.Changed(ChkDiscordHWAccel, _state.DiscordHWAccel);
            bool? chSteam   = Helpers.TweakFeedback.Changed(ChkSteamOverlay,   _state.SteamOverlay);

            if (!(chHAGS.HasValue || chGameBar.HasValue || chDVR.HasValue || chGPU.HasValue
                  || chMSI.HasValue || chDiscord.HasValue || chSteam.HasValue))
            {
                Helpers.TweakFeedback.ShowInfo(StatusBanner, StatusDot, StatusText, "Aucune modification à appliquer.");
                BtnAppliquer.IsEnabled = true;
                return;
            }

            _main.Log("Windows : application des optimisations…");
            var msgs = new System.Collections.Generic.List<string>();
            await Task.Run(() =>
                ApplyChanges(chHAGS, chGameBar, chDVR, chGPU, chMSI, chDiscord, chSteam,
                             msg => { _main.Log(msg); msgs.Add(msg); }));

            _state = (ChkHAGS.IsChecked == true, ChkDisableGameBar.IsChecked == true,
                      ChkDisableDVR.IsChecked == true, ChkGPUPriority.IsChecked == true,
                      ChkMSIMode.IsChecked == true, ChkDiscordHWAccel.IsChecked == true,
                      ChkSteamOverlay.IsChecked == true);
            _main.Log("Windows : optimisations appliquées.");
            Helpers.TweakFeedback.Show(StatusBanner, StatusDot, StatusText, msgs, "Optimisations Windows appliquées");
            BtnAppliquer.IsEnabled = true;
        }

        // ── Réparation du popup « ms-gamingoverlay » ───────────────────────────
        // Ré-enregistre la Xbox Game Bar (restaure le gestionnaire du protocole ms-gamingoverlay).
        private async void BtnFixGamingOverlay_Click(object sender, RoutedEventArgs e)
        {
            BtnFixGamingOverlay.IsEnabled = false;
            _main.Log("Réparation ms-gamingoverlay : ré-enregistrement de la Xbox Game Bar…");

            // Réparation en 3 temps : 1) RESET du package (purge l'état corrompu), 2) RÉ-ENREGISTREMENT
            // (restaure le gestionnaire ms-gamingoverlay), 3) VÉRIFICATION du statut du package.
            const string ps =
                "$ErrorActionPreference='SilentlyContinue';" +
                "$n='Microsoft.XboxGamingOverlay';" +
                "$p=Get-AppxPackage -Name $n; if(-not $p){$p=Get-AppxPackage -AllUsers -Name $n};" +
                "if(-not $p){'NOTINSTALLED'}else{" +
                "try{Reset-AppxPackage -Package $p[0].PackageFullName}catch{};" +
                "foreach($pk in $p){$m=Join-Path $pk.InstallLocation 'AppXManifest.xml';" +
                "if(Test-Path $m){Add-AppxPackage -DisableDevelopmentMode -Register $m}};" +
                "$a=Get-AppxPackage -Name $n;" +
                "if($a){'VERIFIED:'+($a|Select-Object -First 1).Status}else{'FAILED'}}";

            string result = await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo("powershell",
                        $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"")
                    {
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                    };
                    using var p = Process.Start(psi);
                    string outp = p?.StandardOutput.ReadToEnd() ?? "";
                    p?.WaitForExit(60_000);
                    return outp;
                }
                catch (Exception ex) { return "ERR:" + ex.Message; }
            });

            if (result.Contains("VERIFIED:Ok"))
            {
                _main.Log("Réparation ms-gamingoverlay : reset + ré-enregistrement OK, package vérifié SAIN. Le popup ne devrait plus apparaître.");
                Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, true,
                    "Réparé et vérifié — Game Bar réinitialisée + ré-enregistrée (package sain)", "");
            }
            else if (result.Contains("VERIFIED:"))
            {
                var st = result.Substring(result.IndexOf("VERIFIED:", StringComparison.Ordinal) + 9).Trim();
                _main.Log($"Réparation ms-gamingoverlay : appliquée, statut du package = « {st} ». Redémarre si le popup persiste.");
                Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, true,
                    $"Réparation appliquée (statut : {st}) — redémarre si ça persiste", "");
            }
            else if (result.Contains("NOTINSTALLED"))
            {
                _main.Log("Réparation ms-gamingoverlay : Xbox Game Bar absente → ouverture du Microsoft Store.");
                Helpers.TweakFeedback.ShowInfo(StatusBanner, StatusDot, StatusText,
                    "Xbox Game Bar absente — ouverture du Store pour la réinstaller.");
                try { Process.Start(new ProcessStartInfo("ms-windows-store://search/?query=Xbox Game Bar") { UseShellExecute = true }); } catch { }
            }
            else
            {
                _main.Log($"Réparation ms-gamingoverlay : échec — {result.Trim()}");
                Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, false, "",
                    "Réparation impossible — voir le journal d'activité.");
            }

            BtnFixGamingOverlay.IsEnabled = true;
        }

        private static void ApplyChanges(
            bool? doHAGS, bool? doDisableGameBar, bool? doDisableDVR,
            bool? doGPUPriority, bool? doMSI, bool? doDiscord, bool? doSteam,
            Action<string> log)
        {
            // HAGS
            if (doHAGS.HasValue)
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                    "HwSchMode", doHAGS.Value ? 2 : 1, RegistryValueKind.DWord);
                log($"HAGS : {(doHAGS.Value ? "ACTIVÉ" : "DÉSACTIVÉ")} — redémarrage requis.");
            }
            catch (Exception ex) { log($"HAGS : erreur — {ex.Message}"); }

            // Game Bar
            if (doDisableGameBar.HasValue)
            try
            {
                const string pathDVR =
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR";
                Registry.SetValue(pathDVR, "AppCaptureEnabled",
                    doDisableGameBar.Value ? 0 : 1, RegistryValueKind.DWord);
                log($"Barre de jeu Xbox : {(doDisableGameBar.Value ? "DÉSACTIVÉE" : "ACTIVÉE")}.");
            }
            catch (Exception ex) { log($"Barre de jeu : erreur — {ex.Message}"); }

            // DVR
            if (doDisableDVR.HasValue)
            try
            {
                const string pathDVR =
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR";
                Registry.SetValue(pathDVR, "HistoricalCaptureEnabled",
                    doDisableDVR.Value ? 0 : 1, RegistryValueKind.DWord);
                log($"Enregistrement DVR : {(doDisableDVR.Value ? "DÉSACTIVÉ" : "ACTIVÉ")}.");
            }
            catch (Exception ex) { log($"DVR : erreur — {ex.Message}"); }

            // GPU Priority
            if (doGPUPriority.HasValue)
            try
            {
                const string pathGames =
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
                if (doGPUPriority.Value)
                {
                    Registry.SetValue(pathGames, "GPU Priority",        8,      RegistryValueKind.DWord);
                    Registry.SetValue(pathGames, "Priority",            6,      RegistryValueKind.DWord);
                    Registry.SetValue(pathGames, "Scheduling Category", "High", RegistryValueKind.String);
                    log("Priorité GPU : ÉLEVÉE.");
                }
                else
                {
                    Registry.SetValue(pathGames, "GPU Priority",        2,        RegistryValueKind.DWord);
                    Registry.SetValue(pathGames, "Priority",            2,        RegistryValueKind.DWord);
                    Registry.SetValue(pathGames, "Scheduling Category", "Medium", RegistryValueKind.String);
                    log("Priorité GPU : RESTAURÉE (par défaut).");
                }
            }
            catch (Exception ex) { log($"GPU Priority : erreur — {ex.Message}"); }

            // Mode MSI GPU
            if (doMSI.HasValue)
            try
            {
                var msiPath = FindMSIPath();
                if (msiPath != null)
                {
                    Registry.SetValue(msiPath, "MSISupported", doMSI.Value ? 1 : 0, RegistryValueKind.DWord);
                    log($"Mode MSI GPU : {(doMSI.Value ? "ACTIVÉ" : "DÉSACTIVÉ")} — redémarrage requis.");
                }
                else log("Mode MSI GPU : aucun GPU détecté.");
            }
            catch (Exception ex) { log($"Mode MSI GPU : erreur — {ex.Message}"); }

            // Discord HW Accel
            if (doDiscord.HasValue)
            try
            {
                if (Process.GetProcessesByName("Discord").Length > 0)
                {
                    log("Discord : fermez Discord avant d'appliquer ce réglage.");
                }
                else
                {
                    var settingsPath = FindDiscordSettingsPath();
                    if (settingsPath != null && File.Exists(settingsPath))
                    {
                        var json = File.ReadAllText(settingsPath);
                        using var doc = JsonDocument.Parse(json);
                        var dict = new System.Collections.Generic.Dictionary<string, object>();
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == "enableHardwareAcceleration") continue;
                            dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.True ? (object)true
                                            : prop.Value.ValueKind == JsonValueKind.False ? false
                                            : prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetDouble()
                                            : prop.Value.GetString()!;
                        }
                        // enableHardwareAcceleration = true si on N'a PAS coché "désactiver"
                        dict["enableHardwareAcceleration"] = !doDiscord.Value;
                        File.WriteAllText(settingsPath, JsonSerializer.Serialize(dict,
                            new JsonSerializerOptions { WriteIndented = true }),
                            System.Text.Encoding.UTF8);
                        log($"Discord HW Accel : {(doDiscord.Value ? "DÉSACTIVÉE" : "ACTIVÉE")}. Redémarrez Discord.");
                    }
                    else log("Discord : settings.json introuvable.");
                }
            }
            catch (Exception ex) { log($"Discord : erreur — {ex.Message}"); }

            // Steam Overlay
            if (doSteam.HasValue)
            try
            {
                if (Process.GetProcessesByName("steam").Length > 0)
                {
                    log("Overlay Steam : fermez Steam avant d'appliquer ce réglage.");
                }
                else
                {
                    var vdfPath = FindSteamLocalConfigPath();
                    if (vdfPath != null && File.Exists(vdfPath))
                    {
                        var content = File.ReadAllText(vdfPath);
                        var newVal = doSteam.Value ? "0" : "1";
                        content = Regex.Replace(content,
                            @"""EnableGameOverlay""\s+""\d""",
                            $"\"EnableGameOverlay\"\t\t\"{newVal}\"");
                        File.WriteAllText(vdfPath, content, System.Text.Encoding.UTF8);
                        log($"Overlay Steam : {(doSteam.Value ? "DÉSACTIVÉ" : "ACTIVÉ")}.");
                    }
                    else log("Overlay Steam : localconfig.vdf introuvable.");
                }
            }
            catch (Exception ex) { log($"Overlay Steam : erreur — {ex.Message}"); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string? FindMSIPath()
        {
            const string classRoot =
                @"SYSTEM\CurrentControlSet\Enum";
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(classRoot);
                if (root == null) return null;

                // Chercher parmi les GPU PCI (DISPLAY ou VEN_ dans le chemin)
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

                            var msiSubPath =
                                $@"{classRoot}\{busName}\{devName}\{instName}" +
                                @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                            return @"HKEY_LOCAL_MACHINE\" + msiSubPath;
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
            // Chercher Steam via registre
            try
            {
                var steamPath = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamPath", null)?.ToString();
                if (steamPath == null) return null;
                // Parcourir userdata/<id>/config/localconfig.vdf
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
    }
}
