using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

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

            if (!doTemp && !doSysTemp && !doPrefetch && !doDX &&
                !doNv && !doTrim && !doEventLogs)
            {
                _main.Log("Nettoyage : aucune option sélectionnée.");
                BtnLancer.IsEnabled = true;
                return;
            }

            _main.Log("Nettoyage en cours…");

            var (freed, ops) = await Task.Run(() =>
                RunCleanup(doTemp, doSysTemp, doPrefetch, doDX, doNv, doTrim, doEventLogs));

            _main.Log($"Nettoyage terminé — {FormatBytes(freed)} libérés ({ops} opérations).");

            // Décocher UNIQUEMENT les cases qui viennent d'être traitées
            if (doTemp)      ChkTemp.IsChecked       = false;
            if (doSysTemp)   ChkSystemTemp.IsChecked = false;
            if (doPrefetch)  ChkPrefetch.IsChecked   = false;
            if (doDX)        ChkDXCache.IsChecked     = false;
            if (doNv)        ChkNvCache.IsChecked     = false;
            if (doTrim)      ChkTrimSSD.IsChecked     = false;
            if (doEventLogs) ChkEventLogs.IsChecked   = false;

            BtnLancer.IsEnabled = true;
        }

        private static (long freed, int ops) RunCleanup(
            bool doTemp, bool doSysTemp, bool doPrefetch,
            bool doDX, bool doNv, bool doTrim, bool doEventLogs)
        {
            long freed = 0;
            int  ops   = 0;

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
                try
                {
                    using var p = Process.Start(new ProcessStartInfo("defrag", "/C /H /U /RETRIM")
                    {
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    });
                    p?.WaitForExit(120_000);
                    ops++;
                }
                catch { }
            }

            if (doEventLogs)
            {
                ops += ClearEventLogs();
            }

            return (freed, ops);
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

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} Go";
            if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} Mo";
            if (bytes >= 1_024)         return $"{bytes / 1_024.0:F0} Ko";
            return $"{bytes} octets";
        }
    }
}
