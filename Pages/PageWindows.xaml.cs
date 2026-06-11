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

            // ⚠️ FIX v1.3.3 — POURQUOI ça ne marchait pas avant : Tweakly tourne en ADMIN
            // (requireAdministrator), or les opérations AppX (Reset/Add-AppxPackage -Register)
            // s'appliquent au PROFIL DE L'UTILISATEUR COURANT. Exécutées depuis le contexte
            // élevé, elles « réussissaient » sans réparer le profil utilisateur réel.
            // → On exécute désormais le PowerShell DÉ-ÉLEVÉ (token d'explorer.exe, via
            //   Helpers/DeElevatedLauncher), dans le vrai contexte utilisateur.
            // Comme on ne peut pas capturer la sortie d'un process à token différent,
            // le script écrit son verdict dans un fichier temporaire qu'on relit.
            var outFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tweakly_gamingoverlay.txt");
            try { System.IO.File.Delete(outFile); } catch { }

            string ps =
                "$ErrorActionPreference='SilentlyContinue';" +
                $"$out='{outFile.Replace("\\", "\\\\")}';" +
                "$n='Microsoft.XboxGamingOverlay';" +
                "$p=Get-AppxPackage -Name $n;" +
                "if(-not $p){Set-Content -Path $out -Value 'NOTINSTALLED'}else{" +
                "try{Reset-AppxPackage -Package $p[0].PackageFullName}catch{};" +
                "foreach($pk in $p){$m=Join-Path $pk.InstallLocation 'AppXManifest.xml';" +
                "if(Test-Path $m){Add-AppxPackage -DisableDevelopmentMode -Register $m}};" +
                "$a=Get-AppxPackage -Name $n;" +
                "if($a){Set-Content -Path $out -Value ('VERIFIED:'+($a|Select-Object -First 1).Status)}" +
                "else{Set-Content -Path $out -Value 'FAILED'}}";

            string result = await Task.Run(() =>
            {
                try
                {
                    // Voie principale : DÉ-ÉLEVÉ (contexte utilisateur — la bonne façon pour AppX)
                    int code = Helpers.DeElevatedLauncher.StartAndWait(
                        "powershell.exe",
                        $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"",
                        timeoutMs: 90_000);
                    if (System.IO.File.Exists(outFile))
                        return System.IO.File.ReadAllText(outFile).Trim();
                    return $"ERR:pas de résultat (exit {code})";
                }
                catch (Exception exDe)
                {
                    // Fallback : voie élevée historique (moins fiable pour AppX, mais mieux que rien
                    // si le lancement dé-élevé échoue — ex. session sans explorer).
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
                        p?.WaitForExit(60_000);
                        if (System.IO.File.Exists(outFile))
                            return System.IO.File.ReadAllText(outFile).Trim() + " (mode élevé — fallback)";
                        return "ERR:" + exDe.Message;
                    }
                    catch (Exception ex) { return "ERR:" + ex.Message; }
                }
                finally
                {
                    try { System.IO.File.Delete(outFile); } catch { }
                }
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
                // Game Bar ABSENTE : le popup vient de GameDVR qui invoque ms-gamingoverlay
                // dans le vide. Fix DOCUMENTÉ (Microsoft Q&A 3739326, le plus confirmé) :
                // couper le DÉCLENCHEUR — les 2 mêmes clés HKCU que notre tweak « Désactiver
                // Game Bar » (ApplyChanges plus bas). Le popup disparaît SANS réinstaller.
                bool dvrOff = DisableGameDvrTrigger();
                _main.Log(dvrOff
                    ? "Réparation ms-gamingoverlay : Game Bar absente → déclencheur GameDVR coupé (AppCaptureEnabled=0 + GameDVR_Enabled=0). Le popup ne devrait plus apparaître. Pour retrouver la Game Bar : Microsoft Store."
                    : "Réparation ms-gamingoverlay : Game Bar absente ET échec d'écriture des clés GameDVR — voir le journal technique.");
                if (dvrOff)
                    Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, true,
                        "Popup neutralisé (Game Bar absente : enregistrement GameDVR désactivé)", "");
                else
                    Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, false, "",
                        "Réparation impossible — voir le journal d'activité.");
            }
            else
            {
                // Échec du ré-enregistrement : même neutralisation du déclencheur en filet
                // de secours (le package est peut-être irrécupérable, mais le popup, lui,
                // peut être stoppé à coup sûr).
                bool dvrOff = DisableGameDvrTrigger();
                _main.Log($"Réparation ms-gamingoverlay : ré-enregistrement échoué ({result.Trim()})"
                        + (dvrOff ? " — déclencheur GameDVR coupé en secours, le popup ne devrait plus apparaître."
                                  : " — et échec d'écriture des clés GameDVR."));
                if (dvrOff)
                    Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, true,
                        "Réparation partielle : popup neutralisé (enregistrement GameDVR désactivé)", "");
                else
                    Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, false, "",
                        "Réparation impossible — voir le journal d'activité.");
            }

            BtnFixGamingOverlay.IsEnabled = true;
        }

        /// <summary>
        /// Coupe le déclencheur du popup ms-gamingoverlay : GameDVR n'invoque plus l'overlay.
        /// MÊMES clés que le tweak « Désactiver Game Bar » (cohérence avec ApplyChanges) ;
        /// HKCU reste celui de l'utilisateur réel même en admin (vérifié de longue date).
        /// Source : learn.microsoft.com/en-us/answers/questions/3739326 (fix le plus confirmé).
        /// </summary>
        private static bool DisableGameDvrTrigger()
        {
            try
            {
                Registry.SetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
                    "AppCaptureEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(
                    @"HKEY_CURRENT_USER\System\GameConfigStore",
                    "GameDVR_Enabled", 0, RegistryValueKind.DWord);
                return true;
            }
            catch (Exception ex)
            {
                Helpers.AppLog.Error("ms-gamingoverlay : écriture clés GameDVR", ex);
                return false;
            }
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
                // Clé canonique du Game DVR (GameConfigStore) — plus complet, aide aussi à éviter
                // le popup « ms-gamingoverlay » côté déclencheur.
                Registry.SetValue(@"HKEY_CURRENT_USER\System\GameConfigStore",
                    "GameDVR_Enabled", doDisableGameBar.Value ? 0 : 1, RegistryValueKind.DWord);
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
                    // Vrais défauts Windows de la tâche « Games » (avant : 2/2/Medium = sous le défaut)
                    Registry.SetValue(pathGames, "GPU Priority",        8,      RegistryValueKind.DWord);
                    Registry.SetValue(pathGames, "Priority",            6,      RegistryValueKind.DWord);
                    Registry.SetValue(pathGames, "Scheduling Category", "High", RegistryValueKind.String);
                    log("Priorité GPU : restaurée aux défauts Windows.");
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
