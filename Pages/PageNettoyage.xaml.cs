using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Optimisation_Tool.Pages
{
    public partial class PageNettoyage : UserControl
    {
        private readonly MainWindow _main;

        public PageNettoyage(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e) { }

        // ── Nettoyage ─────────────────────────────────────────────────────────

        private async void BtnLancer_Click(object sender, RoutedEventArgs e)
        {
            BtnLancer.IsEnabled = false;

            // Capturer l'état des cases sur le thread UI
            bool doTemp      = ChkTemp.IsChecked      == true;
            bool doSysTemp   = ChkSystemTemp.IsChecked == true;
            bool doPrefetch  = ChkPrefetch.IsChecked   == true;
            bool doDX        = ChkDXCache.IsChecked    == true;
            bool doNv        = ChkNvCache.IsChecked    == true;
            bool doTrim      = ChkTrimSSD.IsChecked    == true;
            bool doEventLogs = ChkEventLogs.IsChecked  == true;
            bool doResidues  = ChkResidues.IsChecked   == true;

            if (!doTemp && !doSysTemp && !doPrefetch && !doDX &&
                !doNv && !doTrim && !doEventLogs && !doResidues)
            {
                _main.Log("Nettoyage : aucune option sélectionnée.");
                BtnLancer.IsEnabled = true;
                return;
            }

            _main.Log("Nettoyage en cours…");

            long freed = 0; int ops = 0; int residues = 0; bool error = false;
            List<string> details = new();
            try
            {
                (freed, ops, residues, details) = await Task.Run(() =>
                    RunCleanup(doTemp, doSysTemp, doPrefetch, doDX, doNv, doTrim, doEventLogs, doResidues));
            }
            catch (Exception ex) { error = true; _main.Log($"Nettoyage : erreur — {ex.Message}"); }

            // Détail par opération dans le journal (ex. quel disque a été trim)
            foreach (var d in details) _main.Log("Nettoyage : " + d);
            _main.Log($"Nettoyage terminé — {FormatBytes(freed)} libérés ({ops} opération(s)).");

            // Résumé clair : on liste ce qui a été fait, sans « 0 octet » trompeur
            var parts = new List<string>();
            if (freed > 0)      parts.Add($"{FormatBytes(freed)} libérés");
            if (doTrim)         parts.Add("SSD optimisé (TRIM)");
            if (doEventLogs)    parts.Add("journaux Windows vidés");
            if (doResidues)     parts.Add(residues > 0 ? $"{residues} résidu(s) retiré(s)" : "aucun résidu sûr");
            var summary = parts.Count > 0
                ? "Nettoyage terminé — " + string.Join(" · ", parts)
                : "Nettoyage terminé — rien à nettoyer";
            Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, !error, summary,
                "Erreur pendant le nettoyage — voir le journal.");

            // Décocher UNIQUEMENT les cases qui viennent d'être traitées
            if (doTemp)      ChkTemp.IsChecked       = false;
            if (doSysTemp)   ChkSystemTemp.IsChecked = false;
            if (doPrefetch)  ChkPrefetch.IsChecked   = false;
            if (doDX)        ChkDXCache.IsChecked     = false;
            if (doNv)        ChkNvCache.IsChecked     = false;
            if (doTrim)      ChkTrimSSD.IsChecked     = false;
            if (doEventLogs) ChkEventLogs.IsChecked   = false;
            if (doResidues)  ChkResidues.IsChecked    = false;

            BtnLancer.IsEnabled = true;
        }

        private static (long freed, int ops, int residues, List<string> details) RunCleanup(
            bool doTemp, bool doSysTemp, bool doPrefetch,
            bool doDX, bool doNv, bool doTrim, bool doEventLogs, bool doResidues)
        {
            long freed = 0;
            int  ops   = 0;
            int  residues = 0;
            var  details = new List<string>();   // lignes de journal (détail par opération)

            if (doTemp)
            {
                freed += CleanFolder(Path.GetTempPath(), ref ops);
            }

            if (doSysTemp)
            {
                freed += CleanFolder(@"C:\Windows\Temp", ref ops);
            }

            if (doPrefetch)
            {
                freed += CleanFolder(@"C:\Windows\Prefetch", ref ops);
            }

            if (doDX)
            {
                var dxPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "D3DSCache");
                freed += CleanFolder(dxPath, ref ops);
            }

            if (doNv)
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                freed += CleanFolder(Path.Combine(local,   "NVIDIA", "DXCache"),  ref ops);
                freed += CleanFolder(Path.Combine(roaming, "NVIDIA", "GLCache"),  ref ops);
            }

            if (doTrim)
            {
                // TRIM = retrim (defrag /L) par volume fixe. /RETRIM n'existe PAS comme flag → c'était /L le bon.
                foreach (var di in DriveInfo.GetDrives())
                {
                    if (di.DriveType != DriveType.Fixed || !di.IsReady) continue;
                    var letter = di.Name.TrimEnd('\\');   // ex. "C:"
                    try
                    {
                        var psi = new ProcessStartInfo("defrag", $"{letter} /L")
                        {
                            UseShellExecute        = false,
                            CreateNoWindow         = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError  = true,
                            StandardOutputEncoding = Encoding.UTF8,
                        };
                        using var p = Process.Start(psi);
                        string outp = (p?.StandardOutput.ReadToEnd() ?? "") + (p?.StandardError.ReadToEnd() ?? "");
                        p?.WaitForExit(120_000);
                        bool ok = p != null && p.ExitCode == 0;
                        details.Add(ok
                            ? $"TRIM (optimisation SSD) — {letter} : terminé"
                            : $"TRIM — {letter} : ignoré (volume non compatible ou occupé)");
                        ops++;
                    }
                    catch (Exception ex) { details.Add($"TRIM — {letter} : erreur ({ex.Message})"); }
                }
            }

            if (doEventLogs)
            {
                ops += ClearEventLogs();
            }

            if (doResidues)
            {
                residues = CleanSoftwareResidues();
                ops += residues;
            }

            return (freed, ops, residues, details);
        }

        private static long CleanFolder(string path, ref int ops)
        {
            long freed = 0;
            if (!Directory.Exists(path)) return 0;

            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(f);
                    freed += fi.Length;
                    fi.Delete();
                    ops++;
                }
                catch { }
            }

            foreach (var d in Directory.EnumerateDirectories(path))
            {
                try { Directory.Delete(d, true); ops++; } catch { }
            }

            return freed;
        }

        private static int ClearEventLogs()
        {
            int count = 0;
            try
            {
                // Lister les journaux via wevtutil el
                var listProc = Process.Start(new ProcessStartInfo("wevtutil", "el")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true
                });
                if (listProc == null) return 0;

                var logs = listProc.StandardOutput.ReadToEnd();
                listProc.WaitForExit(15_000);

                foreach (var log in logs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = log.Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    try
                    {
                        using var p = Process.Start(new ProcessStartInfo("wevtutil", $"cl \"{name}\"")
                        {
                            UseShellExecute = false,
                            CreateNoWindow  = true
                        });
                        p?.WaitForExit(5_000);
                        count++;
                    }
                    catch { }
                }
            }
            catch { }
            return count;
        }

        // ── Résidus de logiciels désinstallés (signaux SÛRS uniquement) ───────
        // Entrées Uninstall orphelines + raccourcis cassés. On NE touche PAS aux dossiers AppData
        // (risque de faux positif) — ça reste le flux avec revue de l'onglet Applications.
        // Tout est blindé : aucune exception ne doit remonter (sinon crash de l'app).
        private static int CleanSoftwareResidues()
        {
            int n = 0;
            try { n += CleanOrphanUninstallEntries(); } catch { }

            // WScript.Shell = COM → EXIGE un thread STA (sinon plantage depuis le thread pool MTA).
            try
            {
                int shortcuts = 0;
                var th = new Thread(() => { try { shortcuts = CleanBrokenShortcuts(); } catch { } });
                th.SetApartmentState(ApartmentState.STA);
                th.IsBackground = true;
                th.Start();
                th.Join(20000);
                n += shortcuts;
            }
            catch { }
            return n;
        }

        private static int CleanOrphanUninstallEntries()
        {
            int n = 0;
            var roots = new (RegistryKey hive, string path)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };
            foreach (var (hive, path) in roots)
            {
                try
                {
                    using var root = hive.OpenSubKey(path, writable: true);
                    if (root == null) continue;
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        try
                        {
                            string? name, loc;
                            using (var k = root.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                name = k.GetValue("DisplayName") as string;
                                loc  = k.GetValue("InstallLocation") as string;
                            }
                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(loc)) continue;
                            loc = loc.Trim().Trim('"').TrimEnd('\\');
                            if (loc.Length < 4 || !loc.Contains(":\\")) continue;            // chemin absolu requis
                            var drive = Path.GetPathRoot(loc);
                            if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive)) continue;  // disque absent → on s'abstient
                            if (Directory.Exists(loc)) continue;                              // dossier présent → pas orphelin
                            root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);          // entrée morte → retirée
                            n++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return n;
        }

        // Appelé sur un thread STA (COM). EnumerationOptions.IgnoreInaccessible évite les exceptions
        // d'accès pendant le parcours récursif.
        private static int CleanBrokenShortcuts()
        {
            int n = 0;
            var dirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            }.Where(d => !string.IsNullOrEmpty(d)).Distinct();

            dynamic? shell;
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return 0;
                shell = Activator.CreateInstance(t);
            }
            catch { return 0; }
            if (shell == null) return 0;

            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                IEnumerable<string> lnks;
                try { lnks = Directory.EnumerateFiles(d, "*.lnk", opts).ToList(); } catch { continue; }
                foreach (var lnk in lnks)
                {
                    try
                    {
                        dynamic sc = shell.CreateShortcut(lnk);
                        string target = sc.TargetPath as string ?? "";
                        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        var drive = Path.GetPathRoot(target);
                        if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive)) continue;  // disque absent → s'abstient
                        if (File.Exists(target)) continue;                              // cible présente → ok
                        File.Delete(lnk);
                        n++;
                    }
                    catch { }
                }
            }
            return n;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} Go";
            if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} Mo";
            if (bytes >= 1_024)         return $"{bytes / 1_024.0:F0} Ko";
            return $"{bytes} octets";
        }
    }
}
