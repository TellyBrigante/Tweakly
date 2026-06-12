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

        // Bloc d'état n°2 (tweaks v1.3.6 — bloc séparé pour ne pas gonfler le tuple historique)
        private (bool GameMode, bool WindowedOpt, bool NoAccessPopups) _state2;

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

            var s2 = await Task.Run(ReadState2);
            ChkGameMode.IsChecked       = s2.GameMode;
            ChkWindowedOpt.IsChecked    = s2.WindowedOpt;
            ChkNoAccessPopups.IsChecked = s2.NoAccessPopups;
            _state2 = s2;

            BtnAppliquer.IsEnabled = true;
            _main.Log("Windows : état chargé.");
        }

        // ── Lecture état — bloc n°2 (v1.3.6, formats validés en réel le 2026-06-12) ──
        private static (bool GameMode, bool WindowedOpt, bool NoAccessPopups) ReadState2()
        {
            // Mode Jeu : AutoGameModeEnabled — ABSENT = activé par défaut, 0 = désactivé
            bool gameMode = true;
            try
            {
                var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar",
                                          "AutoGameModeEnabled", null);
                gameMode = v == null || Convert.ToInt32(v) == 1;
            }
            catch { }

            // Optimisations jeux fenêtrés (Win11) : chaîne à paires « k=v; » — on cherche
            // SwapEffectUpgradeEnable=1 (vérifié en réel : « SwapEffectUpgradeEnable=1;VRROptimizeEnable=1; »)
            bool windowedOpt = false;
            try
            {
                var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences",
                                          "DirectXUserGlobalSettings", null) as string;
                windowedOpt = v != null && v.Contains("SwapEffectUpgradeEnable=1");
            }
            catch { }

            // Popups d'accessibilité coupés : bit 0x4 (HOTKEYACTIVE) ABSENT des Flags
            // des 3 mécanismes (valeurs par défaut vérifiées : 510 / 62 / 126 = bit posé)
            bool noPopups = false;
            try
            {
                noPopups = !AccessHotkeyActive("StickyKeys") &&
                           !AccessHotkeyActive("ToggleKeys") &&
                           !AccessHotkeyActive("Keyboard Response");
            }
            catch { }

            return (gameMode, windowedOpt, noPopups);
        }

        private static bool AccessHotkeyActive(string subKey)
        {
            try
            {
                var v = Registry.GetValue($@"HKEY_CURRENT_USER\Control Panel\Accessibility\{subKey}",
                                          "Flags", null);
                // Flags est une CHAÎNE (REG_SZ) contenant un entier — vérifié en réel
                if (v is string s && int.TryParse(s, out var f)) return (f & 0x4) != 0;
            }
            catch { }
            return true;   // doute → considéré actif (le switch s'affiche décoché, honnête)
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

        /// <summary>
        /// DA optim v1.3.5 : cliquer n'importe ou sur une rangee actionne son switch
        /// (Tag de la rangee = sa CheckBox via x:Reference). Garde anti-double-toggle.
        /// </summary>
        private void Row_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            for (var d = e.OriginalSource as DependencyObject; d != null;
                 d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                if (d is CheckBox) return;
            if (sender is System.Windows.Controls.Border row && row.Tag is CheckBox chk)
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

            if (!(chHAGS.HasValue || chGameBar.HasValue || chDVR.HasValue || chGPU.HasValue
                  || chMSI.HasValue || chDiscord.HasValue || chSteam.HasValue
                  || chGMode.HasValue || chWinOpt.HasValue || chNoPop.HasValue))
            {
                Helpers.TweakFeedback.ShowInfo(StatusBanner, StatusDot, StatusText, "Aucune modification à appliquer.");
                BtnAppliquer.IsEnabled = true;
                return;
            }

            _main.Log("Windows : application des optimisations…");
            var msgs = new System.Collections.Generic.List<string>();
            await Task.Run(() =>
            {
                ApplyChanges(chHAGS, chGameBar, chDVR, chGPU, chMSI, chDiscord, chSteam,
                             msg => { _main.Log(msg); msgs.Add(msg); });
                ApplyChanges2(chGMode, chWinOpt, chNoPop,
                             msg => { _main.Log(msg); msgs.Add(msg); });
            });

            _state = (ChkHAGS.IsChecked == true, ChkDisableGameBar.IsChecked == true,
                      ChkDisableDVR.IsChecked == true, ChkGPUPriority.IsChecked == true,
                      ChkMSIMode.IsChecked == true, ChkDiscordHWAccel.IsChecked == true,
                      ChkSteamOverlay.IsChecked == true);
            _state2 = (ChkGameMode.IsChecked == true, ChkWindowedOpt.IsChecked == true,
                       ChkNoAccessPopups.IsChecked == true);
            _main.Log("Windows : optimisations appliquées.");
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
                    Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar",
                                      "AutoGameModeEnabled", doGameMode == true ? 1 : 0,
                                      RegistryValueKind.DWord);
                    log($"Mode Jeu Windows : {(doGameMode == true ? "ACTIVÉ" : "désactivé")}.");
                }
                catch (Exception ex) { log($"Mode Jeu : erreur — {ex.Message}"); }
            }

            // Optimisations jeux fenêtrés (Win11) : chaîne à paires « k=v; » — on remplace
            // UNIQUEMENT la paire SwapEffectUpgradeEnable en PRÉSERVANT les autres
            // (ex. VRROptimizeEnable=1, vérifié en réel sur la machine de référence).
            if (doWindowedOpt.HasValue)
            {
                try
                {
                    const string dxPath = @"HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences";
                    var cur = (Registry.GetValue(dxPath, "DirectXUserGlobalSettings", null) as string) ?? "";
                    var pairs = new System.Collections.Generic.List<string>();
                    foreach (var p in cur.Split(';'))
                        if (p.Trim().Length > 0 && !p.TrimStart().StartsWith("SwapEffectUpgradeEnable=", StringComparison.OrdinalIgnoreCase))
                            pairs.Add(p.Trim());
                    pairs.Add("SwapEffectUpgradeEnable=" + (doWindowedOpt == true ? "1" : "0"));
                    Registry.SetValue(dxPath, "DirectXUserGlobalSettings",
                                      string.Join(";", pairs) + ";", RegistryValueKind.String);
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
                        Registry.SetValue(path, "Flags", f.ToString(), RegistryValueKind.String);
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
                // Pas de package → rien à ré-enregistrer : on coupe le déclencheur direct.
                var st2 = AddGoStep("Neutralisation du déclencheur (GameDVR)");
                bool ok = DisableGameDvrTrigger();
                SetGoStep(st2, ok ? GoState.Pass : GoState.Fail,
                    ok ? "AppCaptureEnabled=0 + GameDVR_Enabled=0 — le popup n'a plus de cause"
                       : "écriture registre refusée");
                _main.Log(ok
                    ? "ms-gamingoverlay : Game Bar absente → déclencheur GameDVR coupé. Réinstallation possible via le Microsoft Store."
                    : "ms-gamingoverlay : Game Bar absente ET échec d'écriture GameDVR.");
                Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, ok,
                    "Popup neutralisé (Game Bar absente — Store pour la réinstaller)",
                    "Réparation impossible — voir le journal d'activité.");
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
                "Réparation impossible — voir le journal d'activité.");
            BtnFixGamingOverlay.IsEnabled = true;
        }

        // ── Exécution PowerShell dé-élevée avec résultat fichier (fallback élevé) ──
        private static string RunGoPs(string body)
        {
            var outFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tweakly_gamingoverlay.txt");
            try { System.IO.File.Delete(outFile); } catch { }
            var ps = "$ErrorActionPreference='SilentlyContinue';" +
                     $"$r=&{{{body}}};Set-Content -Path '{outFile.Replace("\\", "\\\\")}' -Value $r";
            try
            {
                int code = Helpers.DeElevatedLauncher.StartAndWait(
                    "powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"",
                    timeoutMs: 90_000);
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
                    p?.WaitForExit(60_000);
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
            icon.Foreground = state switch
            {
                GoState.Pass => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xC4, 0x6A)),
                GoState.Warn => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xA0, 0x2E)),
                GoState.Fail => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x55, 0x55)),
                _ => icon.Foreground,
            };
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
