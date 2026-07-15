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
        private const string GamesTaskRegistryPath =
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
        private const string SteamOverlayPattern = @"""EnableGameOverlay""\s+""(\d)""";

        // État lu au chargement → sert de référence pour n'appliquer que ce qui change
        private (bool HAGS, bool DisableGameBar, bool DisableDVR, bool GPUPriority,
                 bool MSIMode, bool DiscordHWAccel, bool SteamOverlay) _state;

        // Bloc d'état n°2 (tweaks v1.3.6 — bloc séparé pour ne pas gonfler le tuple historique)
        private (bool GameMode, bool WindowedOpt, bool NoAccessPopups) _state2;

        private sealed record StateRead(
            Helpers.ProbeResult<bool> HAGS,
            Helpers.ProbeResult<bool> DisableGameBar,
            Helpers.ProbeResult<bool> DisableDVR,
            Helpers.ProbeResult<bool> GPUPriority,
            Helpers.ProbeResult<bool> MSIMode,
            Helpers.ProbeResult<bool> DiscordHWAccel,
            Helpers.ProbeResult<bool> SteamOverlay)
        {
            public (bool HAGS, bool DisableGameBar, bool DisableDVR, bool GPUPriority,
                    bool MSIMode, bool DiscordHWAccel, bool SteamOverlay) Values
                => (HAGS.Value, DisableGameBar.Value, DisableDVR.Value, GPUPriority.Value,
                    MSIMode.Value, DiscordHWAccel.Value, SteamOverlay.Value);
        }

        private sealed record StateRead2(
            Helpers.ProbeResult<bool> GameMode,
            Helpers.ProbeResult<bool> WindowedOpt,
            Helpers.ProbeResult<bool> NoAccessPopups)
        {
            public (bool GameMode, bool WindowedOpt, bool NoAccessPopups) Values
                => (GameMode.Value, WindowedOpt.Value, NoAccessPopups.Value);
        }

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

            StateRead read = await Task.Run(ReadStateDetailed);

            Helpers.TweakFeedback.ApplyDetectedState(ChkHAGS, read.HAGS, _main.Log, "HAGS");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkDisableGameBar, read.DisableGameBar, _main.Log, "Barre de jeu Xbox");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkDisableDVR, read.DisableDVR, _main.Log, "Enregistrement DVR");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkGPUPriority, read.GPUPriority, _main.Log, "Priorité GPU");
            Helpers.TweakFeedback.ApplyDetectedState(ChkMSIMode, read.MSIMode, _main.Log, "Mode MSI GPU");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkDiscordHWAccel, read.DiscordHWAccel, _main.Log, "Accélération matérielle Discord");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkSteamOverlay, read.SteamOverlay, _main.Log, "Overlay Steam");

            _state = read.Values;

            StateRead2 read2 = await Task.Run(ReadState2Detailed);
            Helpers.TweakFeedback.ApplyDetectedState(ChkGameMode, read2.GameMode, _main.Log, "Mode Jeu");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkWindowedOpt, read2.WindowedOpt, _main.Log, "Optimisations pour jeux fenêtrés");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkNoAccessPopups, read2.NoAccessPopups, _main.Log, "Popups d'accessibilité");
            _state2 = read2.Values;

            BtnAppliquer.IsEnabled = true;
            _main.Log("Windows : état chargé.");
        }

        // ── Lecture état — bloc n°2 (v1.3.6, formats validés en réel le 2026-06-12) ──
        private static (bool GameMode, bool WindowedOpt, bool NoAccessPopups) ReadState2()
            => ReadState2Detailed().Values;

        private static StateRead2 ReadState2Detailed()
        {
            // Mode Jeu : AutoGameModeEnabled — ABSENT = activé par défaut, 0 = désactivé
            var gameMode = Helpers.ProbeResult<bool>.Capture(
                "Windows : lecture du Mode Jeu",
                () =>
                {
                    var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar",
                                              "AutoGameModeEnabled", null);
                    return v == null || Convert.ToInt32(v) == 1;
                },
                fallback: true);

            // Optimisations jeux fenêtrés (Win11) : chaîne à paires « k=v; » — on cherche
            // SwapEffectUpgradeEnable=1 (vérifié en réel : « SwapEffectUpgradeEnable=1;VRROptimizeEnable=1; »)
            var windowedOpt = Helpers.ProbeResult<bool>.Capture(
                "Windows : lecture des optimisations pour jeux fenêtrés",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences",
                        "DirectXUserGlobalSettings", null) as string;
                    return Helpers.RegistryValueLogic.HasSemicolonValue(
                        v, "SwapEffectUpgradeEnable", "1");
                },
                fallback: false);

            // Popups d'accessibilité coupés : bit 0x4 (HOTKEYACTIVE) ABSENT des Flags
            // des 3 mécanismes (valeurs par défaut vérifiées : 510 / 62 / 126 = bit posé)
            var noPopups = Helpers.ProbeResult<bool>.Capture(
                "Windows : lecture des popups d'accessibilité",
                () => !AccessHotkeyActive("StickyKeys") &&
                      !AccessHotkeyActive("ToggleKeys") &&
                      !AccessHotkeyActive("Keyboard Response"),
                fallback: false);

            return new StateRead2(gameMode, windowedOpt, noPopups);
        }

        private static bool AccessHotkeyActive(string subKey)
        {
            var v = Registry.GetValue($@"HKEY_CURRENT_USER\Control Panel\Accessibility\{subKey}",
                                      "Flags", null);
            if (v == null) return true;
            // Flags est une CHAÎNE (REG_SZ) contenant un entier — vérifié en réel.
            if (v is string s && int.TryParse(s, out var flags))
                return (flags & 0x4) != 0;

            throw new InvalidDataException($"Flags invalide pour {subKey}.");
        }

        private static (bool HAGS, bool DisableGameBar, bool DisableDVR,
                        bool GPUPriority, bool MSIMode,
                        bool DiscordHWAccel, bool SteamOverlay) ReadState()
            => ReadStateDetailed().Values;

        private static StateRead ReadStateDetailed()
        {
            // HAGS : seule la valeur explicite 2 prouve que le réglage est activé.
            // Une valeur absente rend la main à Windows et ne doit pas cocher la case.
            var hags = Helpers.ProbeResult<bool>.Capture(
                "Windows : lecture de HAGS",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                        "HwSchMode", null);
                    return v != null && Convert.ToInt32(v) == 2;
                },
                fallback: false);

            // Game Bar : les deux déclencheurs doivent être coupés.
            var disableGameBar = Helpers.ProbeResult<bool>.Capture(
                "Windows : lecture de la barre de jeu Xbox",
                () =>
                {
                    var capture = Registry.GetValue(
                        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
                        "AppCaptureEnabled", null);
                    var gameDvr = Registry.GetValue(
                        @"HKEY_CURRENT_USER\System\GameConfigStore",
                        "GameDVR_Enabled", null);
                    return capture != null && Convert.ToInt32(capture) == 0 &&
                           gameDvr != null && Convert.ToInt32(gameDvr) == 0;
                },
                fallback: false);

            // DVR
            var disableDvr = Helpers.ProbeResult<bool>.Capture(
                "Windows : lecture de l'enregistrement DVR",
                () =>
                {
                    var v = Registry.GetValue(
                        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
                        "HistoricalCaptureEnabled", null);
                    return v != null && Convert.ToInt32(v) == 0;
                },
                fallback: false);

            // GPU Priority
            var gpuPriority = Helpers.ProbeResult<bool>.Capture(
                "Windows : lecture de la priorité GPU",
                IsForcedGpuPriorityProfile,
                fallback: false);

            // MSI Mode : chercher le premier GPU PCI dans le registre
            bool msiAvailable = Helpers.GpuMsiMode.TryRead(out bool msiEnabled, out string msiError);
            var msiMode = Helpers.ProbeResult<bool>.FromTry(
                "Windows : lecture du mode MSI GPU",
                msiAvailable,
                msiEnabled,
                msiError,
                fallback: false);

            // Discord HW Accel
            var discordHwAccel = Helpers.ProbeResult<bool>.Capture(
                "Windows : lecture de l'accélération matérielle Discord",
                () =>
                {
                    var settingsPath = FindDiscordSettingsPath()
                        ?? throw new FileNotFoundException("settings.json de Discord introuvable");
                    var json = File.ReadAllText(settingsPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("enableHardwareAcceleration", out var prop))
                        return !prop.GetBoolean(); // coché = désactivé dans Discord
                    throw new InvalidDataException("enableHardwareAcceleration est absent du fichier Discord");
                },
                fallback: false);

            // Steam Overlay
            var steamOverlay = Helpers.ProbeResult<bool>.Capture(
                "Windows : lecture de l'overlay Steam",
                () =>
                {
                    var vdfPath = FindSteamLocalConfigPath()
                        ?? throw new FileNotFoundException("localconfig.vdf de Steam introuvable");
                    var content = File.ReadAllText(vdfPath);
                    var m = Regex.Match(content, SteamOverlayPattern);
                    if (!m.Success)
                        throw new InvalidDataException("EnableGameOverlay est absent du fichier Steam");
                    return m.Groups[1].Value == "0";
                },
                fallback: false);

            return new StateRead(
                hags, disableGameBar, disableDvr, gpuPriority, msiMode, discordHwAccel, steamOverlay);
        }

        // ── Appliquer ─────────────────────────────────────────────────────────

        /// <summary>
        /// DA optim v1.3.5 : cliquer n'importe ou sur une rangee actionne son switch
        /// (Tag de la rangee = sa CheckBox via x:Reference). Garde anti-double-toggle.
        /// </summary>
        private void Row_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            for (var d = e.OriginalSource as DependencyObject; d != null;
                 d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                if (d is CheckBox) return;
            if (sender is System.Windows.Controls.Border row && row.Tag is CheckBox chk && chk.IsEnabled)
                chk.IsChecked = chk.IsChecked != true;
        }
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
            bool? chGMode   = Helpers.TweakFeedback.Changed(ChkGameMode,       _state2.GameMode);
            bool? chWinOpt  = Helpers.TweakFeedback.Changed(ChkWindowedOpt,    _state2.WindowedOpt);
            bool? chNoPop   = Helpers.TweakFeedback.Changed(ChkNoAccessPopups, _state2.NoAccessPopups);

            var msgs = new System.Collections.Generic.List<string>();
            if (chDiscord.HasValue && Process.GetProcessesByName("Discord").Length > 0)
            {
                msgs.Add("Discord est ouvert : ferme-le avant de modifier son accélération matérielle.");
                chDiscord = null;
                ChkDiscordHWAccel.IsChecked = _state.DiscordHWAccel;
            }
            if (chSteam.HasValue && Process.GetProcessesByName("steam").Length > 0)
            {
                msgs.Add("Steam est ouvert : ferme-le avant de modifier son overlay.");
                chSteam = null;
                ChkSteamOverlay.IsChecked = _state.SteamOverlay;
            }

            if (!(chHAGS.HasValue || chGameBar.HasValue || chDVR.HasValue || chGPU.HasValue
                  || chMSI.HasValue || chDiscord.HasValue || chSteam.HasValue
                  || chGMode.HasValue || chWinOpt.HasValue || chNoPop.HasValue))
            {
                if (msgs.Count == 0)
                    Helpers.TweakFeedback.ShowInfo(StatusBanner, StatusDot, StatusText, "Aucune modification à appliquer.");
                else
                    Helpers.TweakFeedback.Show(StatusBanner, StatusDot, StatusText, msgs, "Optimisations Windows appliquées");
                BtnAppliquer.IsEnabled = true;
                return;
            }

            _main.Log("Windows : application des optimisations…");
            await Task.Run(() =>
            {
                ApplyChanges(chHAGS, chGameBar, chDVR, chGPU, chMSI, chDiscord, chSteam,
                             msg => { _main.Log(msg); msgs.Add(msg); });
                ApplyChanges2(chGMode, chWinOpt, chNoPop,
                             msg => { _main.Log(msg); msgs.Add(msg); });
            });

            var actual = await Task.Run(() =>
                (Main: ReadStateDetailed(), Extra: ReadState2Detailed()));
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "HAGS", chHAGS, actual.Main.HAGS);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Barre de jeu Xbox", chGameBar, actual.Main.DisableGameBar);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Enregistrement DVR", chDVR, actual.Main.DisableDVR);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Priorite GPU jeux", chGPU, actual.Main.GPUPriority);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Mode MSI GPU", chMSI, actual.Main.MSIMode);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Acceleration materielle Discord", chDiscord, actual.Main.DiscordHWAccel);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Overlay Steam", chSteam, actual.Main.SteamOverlay);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Mode Jeu Windows", chGMode, actual.Extra.GameMode);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Optimisations jeux fenetres", chWinOpt, actual.Extra.WindowedOpt);
            Helpers.TweakFeedback.VerifyApplied(msgs, _main.Log, "Popups d'accessibilite", chNoPop, actual.Extra.NoAccessPopups);

            Helpers.TweakFeedback.ApplyDetectedState(ChkHAGS, actual.Main.HAGS, _main.Log, "HAGS");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkDisableGameBar, actual.Main.DisableGameBar, _main.Log, "Barre de jeu Xbox");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkDisableDVR, actual.Main.DisableDVR, _main.Log, "Enregistrement DVR");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkGPUPriority, actual.Main.GPUPriority, _main.Log, "Priorité GPU");
            Helpers.TweakFeedback.ApplyDetectedState(ChkMSIMode, actual.Main.MSIMode, _main.Log, "Mode MSI GPU");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkDiscordHWAccel, actual.Main.DiscordHWAccel, _main.Log, "Accélération matérielle Discord");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkSteamOverlay, actual.Main.SteamOverlay, _main.Log, "Overlay Steam");
            _state = actual.Main.Values;

            Helpers.TweakFeedback.ApplyDetectedState(ChkGameMode, actual.Extra.GameMode, _main.Log, "Mode Jeu");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkWindowedOpt, actual.Extra.WindowedOpt, _main.Log, "Optimisations pour jeux fenêtrés");
            Helpers.TweakFeedback.ApplyDetectedState(
                ChkNoAccessPopups, actual.Extra.NoAccessPopups, _main.Log, "Popups d'accessibilité");
            _state2 = actual.Extra.Values;
            _main.Log("Windows : application terminée.");
            Helpers.TweakFeedback.Show(StatusBanner, StatusDot, StatusText, msgs, "Optimisations Windows appliquées");
            BtnAppliquer.IsEnabled = true;
        }

        // ── Appliquer — bloc n°2 (tweaks v1.3.6) ────────────────────────────────
        private static void ApplyChanges2(bool? doGameMode, bool? doWindowedOpt,
                                          bool? doNoPopups, Action<string> log)
        {
            // Mode Jeu Windows (officiel Microsoft : priorité au jeu + pas d'Update en partie)
            if (doGameMode.HasValue)
            {
                try
                {
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.CurrentUser,
                        @"Software\Microsoft\GameBar",
                        "AutoGameModeEnabled", doGameMode == true ? 1 : 0);
                    log($"Mode Jeu Windows : {(doGameMode == true ? "ACTIVÉ" : "désactivé")}.");
                }
                catch (Exception ex) { log($"Mode Jeu : erreur — {ex.Message}"); }
            }

            // Optimisations jeux fenêtrés (Win11) : chaîne à paires « k=v; » — on remplace
            // UNIQUEMENT la paire SwapEffectUpgradeEnable en PRÉSERVANT les autres.
            // Décoché = pas d'override Tweakly, donc on retire la paire au lieu d'écrire 0.
            // (ex. VRROptimizeEnable=1, vérifié en réel sur la machine de référence).
            if (doWindowedOpt.HasValue)
            {
                try
                {
                    const string dxPath = @"HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences";
                    var cur = (Registry.GetValue(dxPath, "DirectXUserGlobalSettings", null) as string) ?? "";
                    var updated = Helpers.RegistryValueLogic.SetSemicolonValue(
                        cur,
                        "SwapEffectUpgradeEnable",
                        doWindowedOpt == true ? "1" : null);

                    if (updated == null)
                    {
                        Helpers.VerifiedRegistry.DeleteValue(Registry.CurrentUser,
                            @"Software\Microsoft\DirectX\UserGpuPreferences",
                            "DirectXUserGlobalSettings");
                    }
                    else
                    {
                        Helpers.VerifiedRegistry.SetString(
                            Registry.CurrentUser,
                            @"Software\Microsoft\DirectX\UserGpuPreferences",
                            "DirectXUserGlobalSettings", updated);
                    }

                    log($"Optimisations jeux fenêtrés : {(doWindowedOpt == true ? "ACTIVÉES" : "désactivées")} (effet au prochain lancement des jeux).");
                }
                catch (Exception ex) { log($"Jeux fenêtrés : erreur — {ex.Message}"); }
            }

            // Popups d'accessibilité : on manipule UNIQUEMENT le bit 0x4 (HOTKEYACTIVE)
            // des Flags (REG_SZ contenant un entier — formats 510/62/126 vérifiés en réel),
            // en PRÉSERVANT les autres bits (réglages personnels d'accessibilité intacts).
            if (doNoPopups.HasValue)
            {
                try
                {
                    foreach (var sub in new[] { "StickyKeys", "ToggleKeys", "Keyboard Response" })
                    {
                        var path = $@"HKEY_CURRENT_USER\Control Panel\Accessibility\{sub}";
                        var v = Registry.GetValue(path, "Flags", null);
                        if (v is not string s || !int.TryParse(s, out var f)) continue;
                        f = doNoPopups == true ? (f & ~0x4) : (f | 0x4);
                        Helpers.VerifiedRegistry.SetString(
                            Registry.CurrentUser,
                            $@"Control Panel\Accessibility\{sub}",
                            "Flags", f.ToString());
                    }
                    log($"Popups d'accessibilité en jeu : {(doNoPopups == true ? "COUPÉS" : "restaurés")} (effet à la prochaine session).");
                }
                catch (Exception ex) { log($"Popups accessibilité : erreur — {ex.Message}"); }
            }
        }

        // ── Réparation du popup « ms-gamingoverlay » — PIPELINE DE TESTS (v1.3.6) ──
        // Chaque étape s'affiche en direct (PASS/FAIL + détail) sous le bouton, et la
        // correction s'adapte à l'étape qui échoue. Honnêteté assumée : la voie
        // reset+register n'a JAMAIS été validée sur machine réellement cassée — le
        // pipeline rend enfin visible CE QUI se passe quand un utilisateur l'exécute.
        // ⚠️ Les opérations AppX DOIVENT tourner DÉ-ÉLEVÉES (contexte utilisateur,
        // leçon v1.3.3) — résultat via fichier temp.
        private async void BtnFixGamingOverlay_Click(object sender, RoutedEventArgs e)
        {
            BtnFixGamingOverlay.IsEnabled = false;
            GoStepsPanel.Children.Clear();
            GoStepsPanel.Visibility = Visibility.Visible;
            _main.Log("Réparation ms-gamingoverlay : diagnostic étape par étape…");

            // ── Étape 1 : la Game Bar est-elle présente, et dans quel état ? ──
            var st1 = AddGoStep("Présence de la Xbox Game Bar");
            string probe = await Task.Run(() => RunGoPs(
                "$n='Microsoft.XboxGamingOverlay';$p=Get-AppxPackage -Name $n;" +
                "if(-not $p){'NOTINSTALLED'}else{'PRESENT:'+($p|Select-Object -First 1).Status}"));

            if (probe.StartsWith("ERR:"))
            {
                SetGoStep(st1, GoState.Fail, "sonde impossible — " + probe.Substring(4));
                FinishGoWithTriggerCut("le diagnostic n'a pas pu s'exécuter");
                return;
            }

            if (probe.Contains("NOTINSTALLED"))
            {
                SetGoStep(st1, GoState.Fail, "absente du système");

                // ── VRAIE réparation d'abord (retour utilisateur : couper le déclencheur
                //    sans réinstaller = « mettre le problème sous le tapis ») ──

                // Tentative A : le package est souvent encore STAGED dans WindowsApps
                // (visible -AllUsers) → ré-enregistrement pour l'utilisateur, zéro téléchargement.
                var stA = AddGoStep("Réinstallation depuis les fichiers Windows (staged)");
                string rega = await Task.Run(() => RunGoPs(
                    "$n='Microsoft.XboxGamingOverlay';" +
                    "$st=Get-AppxPackage -AllUsers -Name $n | Select-Object -First 1;" +
                    "if(-not $st){'NOSOURCE'}else{" +
                    "$m=Join-Path $st.InstallLocation 'AppxManifest.xml';" +
                    "if(Test-Path $m){Add-AppxPackage -DisableDevelopmentMode -Register $m};" +
                    "$a=Get-AppxPackage -Name $n;" +
                    "if($a){'REINSTALLED:'+($a|Select-Object -First 1).Status}else{'FAILED'}}"));

                if (rega.Contains("REINSTALLED:"))
                {
                    var stt = rega.Substring(rega.IndexOf("REINSTALLED:", StringComparison.Ordinal) + 12).Trim();
                    SetGoStep(stA, GoState.Pass, $"ré-enregistrée depuis WindowsApps — statut « {stt} »");
                    _main.Log($"ms-gamingoverlay : Game Bar RÉINSTALLÉE depuis les fichiers système (statut {stt}).");
                    Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, true,
                        "Game Bar RÉINSTALLÉE (fichiers système) — relance le diagnostic pour confirmer", "");
                    BtnFixGamingOverlay.IsEnabled = true;
                    return;
                }
                SetGoStep(stA, GoState.Fail,
                    rega.Contains("NOSOURCE") ? "aucun fichier source sur le disque"
                                              : "ré-enregistrement refusé");

                // Tentative B : réinstallation EN LIGNE via le Store (winget, ID officiel
                // 9NZKPSTSNW4P), dé-élevée comme tous nos appels winget. Peut prendre ~1 min.
                var stB = AddGoStep("Réinstallation via le Microsoft Store (winget)");
                string regb = await Task.Run(() => RunGoPs(
                    "winget install --id 9NZKPSTSNW4P --source msstore " +
                    "--accept-package-agreements --accept-source-agreements --silent | Out-Null;" +
                    "$a=Get-AppxPackage -Name 'Microsoft.XboxGamingOverlay';" +
                    "if($a){'REINSTALLED:'+($a|Select-Object -First 1).Status}else{'FAILED'}",
                    timeoutMs: 240_000));

                if (regb.Contains("REINSTALLED:"))
                {
                    var stt = regb.Substring(regb.IndexOf("REINSTALLED:", StringComparison.Ordinal) + 12).Trim();
                    SetGoStep(stB, GoState.Pass, $"téléchargée et installée — statut « {stt} »");
                    _main.Log($"ms-gamingoverlay : Game Bar RÉINSTALLÉE via le Store (statut {stt}).");
                    Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, true,
                        "Game Bar RÉINSTALLÉE (Store) — relance le diagnostic pour confirmer", "");
                    BtnFixGamingOverlay.IsEnabled = true;
                    return;
                }
                SetGoStep(stB, GoState.Fail,
                    regb.StartsWith("ERR:") ? regb.Substring(4) : "installation Store impossible (hors-ligne / Store indisponible ?)");

                // Dernier recours, ASSUMÉ comme contournement : couper le déclencheur pour
                // que le popup cesse, + bouton Store manuel via le verdict.
                var stC = AddGoStep("Contournement : neutralisation du déclencheur (GameDVR)");
                bool ok = DisableGameDvrTrigger();
                SetGoStep(stC, ok ? GoState.Warn : GoState.Fail,
                    ok ? "popup stoppé, mais la Game Bar reste absente — réinstalle-la via le Microsoft Store"
                       : "écriture registre refusée");
                _main.Log(ok
                    ? "ms-gamingoverlay : réinstallation impossible → contournement GameDVR appliqué. La Game Bar reste à réinstaller via le Store."
                    : "ms-gamingoverlay : réinstallation impossible ET échec d'écriture GameDVR.");
                try { using var _ = Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?ProductId=9NZKPSTSNW4P") { UseShellExecute = true }); } catch { }
                Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, ok,
                    "Popup stoppé (contournement) — le Store s'ouvre pour réinstaller la Game Bar",
                    "Réparation impossible : Windows a refusé la modification de GameDVR.");
                BtnFixGamingOverlay.IsEnabled = true;
                return;
            }

            var pkgStatus = probe.Substring(probe.IndexOf("PRESENT:", StringComparison.Ordinal) + 8).Trim();
            SetGoStep(st1, pkgStatus.StartsWith("Ok") ? GoState.Pass : GoState.Warn,
                      $"installée — statut « {pkgStatus} »");

            // ── Étape 2 : le protocole ms-gamingoverlay est-il associé ? (informatif) ──
            var st2b = AddGoStep("Association du protocole ms-gamingoverlay");
            bool protoOk = false;
            try { using var k = Registry.ClassesRoot.OpenSubKey("ms-gamingoverlay"); protoOk = k != null; }
            catch { }
            SetGoStep(st2b, protoOk ? GoState.Pass : GoState.Warn,
                      protoOk ? "enregistré dans le système" : "introuvable — le ré-enregistrement doit le restaurer");

            // ── Étape 3 : réinitialisation + ré-enregistrement (contexte utilisateur) ──
            var st3 = AddGoStep("Réinitialisation + ré-enregistrement (contexte utilisateur)");
            string rep = await Task.Run(() => RunGoPs(
                "$n='Microsoft.XboxGamingOverlay';$p=Get-AppxPackage -Name $n;" +
                "if(-not $p){'FAILED'}else{" +
                "try{Reset-AppxPackage -Package $p[0].PackageFullName}catch{};" +
                "foreach($pk in $p){$m=Join-Path $pk.InstallLocation 'AppXManifest.xml';" +
                "if(Test-Path $m){Add-AppxPackage -DisableDevelopmentMode -Register $m}};" +
                "$a=Get-AppxPackage -Name $n;" +
                "if($a){'VERIFIED:'+($a|Select-Object -First 1).Status}else{'FAILED'}}"));

            // ── Étape 4 : vérification finale ──
            var st4 = AddGoStep("Vérification finale du package");

            if (rep.Contains("VERIFIED:Ok"))
            {
                SetGoStep(st3, GoState.Pass, "reset + register exécutés");
                SetGoStep(st4, GoState.Pass, "package SAIN — le popup ne devrait plus apparaître");
                _main.Log("ms-gamingoverlay : réparé et vérifié (package sain).");
                Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, true,
                    "Réparé et vérifié — Game Bar ré-enregistrée (package sain)", "");
                BtnFixGamingOverlay.IsEnabled = true;
                return;
            }
            if (rep.Contains("VERIFIED:"))
            {
                var st = rep.Substring(rep.IndexOf("VERIFIED:", StringComparison.Ordinal) + 9).Trim();
                SetGoStep(st3, GoState.Pass, "reset + register exécutés");
                SetGoStep(st4, GoState.Warn, $"statut encore « {st} » — redémarre, puis relance ce diagnostic");
                _main.Log($"ms-gamingoverlay : réparation appliquée, statut « {st} » — redémarrage conseillé.");
                Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, true,
                    $"Réparation appliquée (statut : {st}) — redémarre si ça persiste", "");
                BtnFixGamingOverlay.IsEnabled = true;
                return;
            }

            // Échec du ré-enregistrement → filet déclencheur
            SetGoStep(st3, GoState.Fail, rep.StartsWith("ERR:") ? rep.Substring(4) : "le système a refusé l'opération");
            SetGoStep(st4, GoState.Fail, "package non réparable en l'état");
            FinishGoWithTriggerCut("ré-enregistrement échoué");
        }

        /// <summary>Étape filet : coupe le déclencheur GameDVR + verdict global.</summary>
        private void FinishGoWithTriggerCut(string reason)
        {
            var st = AddGoStep("Filet : neutralisation du déclencheur (GameDVR)");
            bool ok = DisableGameDvrTrigger();
            SetGoStep(st, ok ? GoState.Pass : GoState.Fail,
                ok ? "le popup n'a plus de cause, même sans réparation du package"
                   : "écriture registre refusée");
            _main.Log($"ms-gamingoverlay : {reason} — filet GameDVR {(ok ? "appliqué" : "ÉCHOUÉ")}.");
            Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, ok,
                "Réparation partielle : popup neutralisé (déclencheur GameDVR coupé)",
                "Réparation impossible : Windows a refusé la modification de GameDVR.");
            BtnFixGamingOverlay.IsEnabled = true;
        }

        // ── Exécution PowerShell dé-élevée avec résultat fichier (fallback élevé) ──
        private static string RunGoPs(string body, int timeoutMs = 90_000)
        {
            var outFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tweakly_gamingoverlay.txt");
            try { System.IO.File.Delete(outFile); } catch { }
            var ps = "$ErrorActionPreference='SilentlyContinue';" +
                     $"$r=&{{{body}}};Set-Content -Path '{outFile.Replace("\\", "\\\\")}' -Value $r";
            try
            {
                int code = Helpers.DeElevatedLauncher.StartAndWait(
                    "powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"",
                    timeoutMs: timeoutMs);
                if (System.IO.File.Exists(outFile))
                    return System.IO.File.ReadAllText(outFile).Trim();
                return $"ERR:pas de résultat (exit {code})";
            }
            catch
            {
                try
                {
                    var psi = new ProcessStartInfo("powershell",
                        $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"")
                    { UseShellExecute = false, CreateNoWindow = true,
                      RedirectStandardOutput = true, RedirectStandardError = true };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(timeoutMs);
                    if (System.IO.File.Exists(outFile))
                        return System.IO.File.ReadAllText(outFile).Trim();
                    return "ERR:exécution impossible";
                }
                catch (Exception ex) { return "ERR:" + ex.Message; }
            }
            finally { try { System.IO.File.Delete(outFile); } catch { } }
        }

        // ── Rangées d'étapes du diagnostic (icône d'état + libellé + détail) ──
        private enum GoState { Running, Pass, Warn, Fail }

        private (System.Windows.Controls.TextBlock icon, System.Windows.Controls.TextBlock txt) AddGoStep(string label)
        {
            var row = new System.Windows.Controls.StackPanel
            { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 5) };
            var icon = new System.Windows.Controls.TextBlock
            { Text = "…", Width = 20, FontSize = 12, FontWeight = FontWeights.Bold,
              VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThTextDim");
            var txt = new System.Windows.Controls.TextBlock
            { Text = label + "…", FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
              VerticalAlignment = VerticalAlignment.Center };
            txt.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThTextBody");
            txt.Tag = label;   // libellé d'origine, réutilisé par SetGoStep
            row.Children.Add(icon); row.Children.Add(txt);
            GoStepsPanel.Children.Add(row);
            return (icon, txt);
        }

        private void SetGoStep((System.Windows.Controls.TextBlock icon, System.Windows.Controls.TextBlock txt) step,
                               GoState state, string detail)
        {
            var (icon, txt) = step;
            icon.Text = state switch
            { GoState.Pass => "✓", GoState.Warn => "⚠", GoState.Fail => "✗", _ => "…" };
            string? role = state switch
            {
                GoState.Pass => "ThOk",
                GoState.Warn => "ThWarn",
                GoState.Fail => "ThCrit",
                _ => null,
            };
            if (role != null)
                icon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, role);
            txt.Text = $"{txt.Tag} — {detail}";
            _main.Log($"  [{icon.Text}] {txt.Tag} : {detail}");
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

        private static bool IsForcedGpuPriorityProfile()
        {
            int gpuPriority = ReadRegistryInt(GamesTaskRegistryPath, "GPU Priority", 0);
            int priority = ReadRegistryInt(GamesTaskRegistryPath, "Priority", 0);
            string scheduling = Convert.ToString(Registry.GetValue(
                GamesTaskRegistryPath, "Scheduling Category", "")) ?? "";
            string sfio = Convert.ToString(Registry.GetValue(
                GamesTaskRegistryPath, "SFIO Priority", "")) ?? "";

            return Helpers.RegistryValueLogic.IsForcedGpuPriority(
                gpuPriority, priority, scheduling, sfio);
        }

        private static int ReadRegistryInt(string path, string name, int fallback)
        {
            var value = Registry.GetValue(path, name, null);
            return value == null ? fallback : Convert.ToInt32(value);
        }

        private static void WriteGpuPriorityProfile(bool forced)
        {
            var profile = Helpers.RegistryValueLogic.GpuPriority(forced);
            const string subKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
            Helpers.VerifiedRegistry.SetDword(Registry.LocalMachine, subKey, "GPU Priority", profile.GpuPriority);
            Helpers.VerifiedRegistry.SetDword(Registry.LocalMachine, subKey, "Priority", profile.Priority);
            Helpers.VerifiedRegistry.SetString(Registry.LocalMachine, subKey, "Scheduling Category", profile.SchedulingCategory);
            Helpers.VerifiedRegistry.SetString(Registry.LocalMachine, subKey, "SFIO Priority", profile.SfioPriority);
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
                if (doHAGS.Value)
                {
                    Helpers.VerifiedRegistry.SetDword(
                        Registry.LocalMachine,
                        @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                        "HwSchMode", 2);
                }
                else
                {
                    Helpers.VerifiedRegistry.DeleteValue(Registry.LocalMachine,
                        @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                        "HwSchMode");
                }
                log(doHAGS.Value
                    ? "HAGS : ACTIVÉ — redémarrage requis."
                    : "HAGS : réglage Windows restauré — redémarrage requis.");
            }
            catch (Exception ex) { log($"HAGS : erreur — {ex.Message}"); }

            // Game Bar
            if (doDisableGameBar.HasValue)
            try
            {
                Helpers.VerifiedRegistry.SetDword(
                    Registry.CurrentUser,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
                    "AppCaptureEnabled", doDisableGameBar.Value ? 0 : 1);
                // Clé canonique du Game DVR (GameConfigStore) — plus complet, aide aussi à éviter
                // le popup « ms-gamingoverlay » côté déclencheur.
                Helpers.VerifiedRegistry.SetDword(
                    Registry.CurrentUser,
                    @"System\GameConfigStore",
                    "GameDVR_Enabled", doDisableGameBar.Value ? 0 : 1);
                log($"Barre de jeu Xbox : {(doDisableGameBar.Value ? "DÉSACTIVÉE" : "ACTIVÉE")}.");
            }
            catch (Exception ex) { log($"Barre de jeu : erreur — {ex.Message}"); }

            // DVR
            if (doDisableDVR.HasValue)
            try
            {
                Helpers.VerifiedRegistry.SetDword(
                    Registry.CurrentUser,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
                    "HistoricalCaptureEnabled", doDisableDVR.Value ? 0 : 1);
                log($"Enregistrement DVR : {(doDisableDVR.Value ? "DÉSACTIVÉ" : "ACTIVÉ")}.");
            }
            catch (Exception ex) { log($"DVR : erreur — {ex.Message}"); }

            // GPU Priority
            if (doGPUPriority.HasValue)
            try
            {
                if (doGPUPriority.Value)
                {
                    WriteGpuPriorityProfile(forced: true);
                    log("Priorité GPU jeux : forcée.");
                }
                else
                {
                    WriteGpuPriorityProfile(forced: false);
                    log("Priorité GPU jeux : profil Windows restauré.");
                }
            }
            catch (Exception ex) { log($"GPU Priority : erreur — {ex.Message}"); }

            // Mode MSI GPU
            if (doMSI.HasValue)
            {
                bool success = Helpers.GpuMsiMode.TrySet(doMSI.Value, out string result);
                log(success ? result : $"Erreur — {result}");
            }

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
                        Helpers.JsonSettingsEditor.SetBooleanAtomically(
                            settingsPath, "enableHardwareAcceleration", !doDiscord.Value);
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
                        SetSteamOverlay(vdfPath, disabled: doSteam.Value);
                        log($"Overlay Steam : {(doSteam.Value ? "DÉSACTIVÉ" : "ACTIVÉ")}.");
                    }
                    else log("Overlay Steam : localconfig.vdf introuvable.");
                }
            }
            catch (Exception ex) { log($"Overlay Steam : erreur — {ex.Message}"); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string? FindDiscordSettingsPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var path = Path.Combine(appData, "discord", "settings.json");
            return File.Exists(path) ? path : null;
        }

        private static string? FindSteamLocalConfigPath()
        {
            // Chercher Steam via registre
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
            return null;
        }

        private static void SetSteamOverlay(string path, bool disabled)
        {
            string content = File.ReadAllText(path);
            Match current = Regex.Match(content, SteamOverlayPattern);
            if (!current.Success)
                throw new InvalidDataException("EnableGameOverlay est absent du fichier Steam.");

            string expected = disabled ? "0" : "1";
            string updated = Regex.Replace(
                content,
                SteamOverlayPattern,
                $"\"EnableGameOverlay\"\t\t\"{expected}\"",
                RegexOptions.None,
                TimeSpan.FromSeconds(1));

            string temp = path + ".tweakly.tmp";
            try
            {
                File.WriteAllText(temp, updated, new System.Text.UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);

                Match verify = Regex.Match(File.ReadAllText(path), SteamOverlayPattern);
                if (!verify.Success || verify.Groups[1].Value != expected)
                    throw new IOException("Steam n'a pas conservé l'état demandé pour son overlay.");
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
