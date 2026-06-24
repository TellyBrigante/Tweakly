using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool
{
    /// <summary>
    /// Overlay compact « mode mini » : monitoring système + réseau fusionnés en une
    /// seule fenêtre légère, toujours au-dessus. Réutilise SystemMonitor + NetworkMonitor
    /// (aucune nouvelle source de données). Un seul sampler tourne à la fois : MainWindow
    /// vide sa page active avant d'afficher la mini.
    /// </summary>
    public partial class MiniMonitor : Window
    {
        private readonly MainWindow _main;
        private readonly DispatcherTimer _timer;
        private bool _busy;

        private readonly List<double> _pingHist = new();   // -1 = perte
        private const int PingWindow = 20;

        public MiniMonitor(MainWindow main)
        {
            _main = main;
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += async (_, _) => await TickAsync();
        }

        // ── Cycle de vie du sampler (piloté par MainWindow) ────────────────────

        public void StartSampling()
        {
            _pingHist.Clear();
            _timer.Start();
            _ = TickAsync();   // premier rafraîchissement immédiat
        }

        public void StopSampling() => _timer.Stop();

        // ── Boucle ─────────────────────────────────────────────────────────────

        private async Task TickAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var sysTask = Task.Run(() => SystemMonitor.Collect(MonCollectParts.Light));
                var netTask = NetworkMonitor.Instance.CollectAsync("1.1.1.1");
                // await sur chaque task : équivalent perf au WhenAll (les 2 tournent déjà en
                // parallèle), mais re-throw l'exception directement au lieu de l'envelopper en
                // AggregateException comme le ferait .Result.
                UpdateSystem(await sysTask);
                UpdateNetwork(await netTask);
            }
            catch { }
            finally { _busy = false; }
        }

        private void UpdateSystem(MonSnapshot s)
        {
            TxtCpu.Text     = $"{s.CpuUsage:F0}";
            // Température CPU si activée + lisible, sinon repli sur la fréquence
            TxtCpuFreq.Text = s.CpuTempC.HasValue
                ? $"{s.CpuTempC.Value:F0} °C"
                : (s.CpuMHz > 0 ? $"{s.CpuMHz / 1000.0:F2} GHz" : "—");

            TxtRam.Text     = $"{s.RamPct:F0}";
            TxtRamFree.Text = s.RamTotalGB > 0 ? $"{s.RamFreeGB:F1} Go libres" : "—";

            if (s.GpuOk)
            {
                TxtGpu.Text     = $"{s.GpuUsage:F0}";
                TxtGpuTemp.Text = $"{s.GpuTemp:F0} °C";
            }
            else
            {
                TxtGpu.Text     = "—";
                TxtGpuTemp.Text = "N/A";
            }
        }

        private void UpdateNetwork(NetSample s)
        {
            Push(_pingHist, s.PingMs);

            var valid = _pingHist.Where(p => p >= 0).ToList();
            double avg    = valid.Count > 0 ? valid.Average() : -1;
            double jitter = ComputeJitter(_pingHist);
            double loss   = _pingHist.Count > 0
                ? (double)_pingHist.Count(p => p < 0) / _pingHist.Count * 100.0 : 0;

            TxtPing.Text = s.PingMs >= 0 ? $"{s.PingMs:F0}" : "—";
            TxtDown.Text = $"{s.DownMbps:F1}";
            TxtUp.Text   = $"{s.UpMbps:F1}";

            UpdateVerdict(avg, jitter, loss, valid.Count);
        }

        // Mêmes seuils que la page Monitoring Réseau.
        private void UpdateVerdict(double avg, double jitter, double loss, int validCount)
        {
            if (validCount == 0)                 SetVerdict("HORS LIGNE", 0xE0, 0x55, 0x55);
            else if (loss > 5)                   SetVerdict("INSTABLE",   0xE0, 0x55, 0x55);
            else if (avg >= 150 || jitter >= 30) SetVerdict("MOYEN",      0xF5, 0xC2, 0x4A);
            else if (avg >= 80  || jitter >= 15) SetVerdict("BON",        0x5B, 0xA0, 0xFF);
            else                                 SetVerdict("EXCELLENT",  0x2E, 0xC4, 0x6A);
        }

        private void SetVerdict(string txt, byte r, byte g, byte b)
        {
            TxtVerdict.Text        = txt;
            PillVerdict.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private static double ComputeJitter(List<double> data)
        {
            double sum = 0; int n = 0; double? prev = null;
            foreach (var v in data)
            {
                if (v < 0) { prev = null; continue; }
                if (prev.HasValue) { sum += Math.Abs(v - prev.Value); n++; }
                prev = v;
            }
            return n > 0 ? sum / n : 0;
        }

        private static void Push(List<double> list, double v)
        {
            list.Add(v);
            if (list.Count > PingWindow) list.RemoveAt(0);
        }

        // ── Fenêtre ────────────────────────────────────────────────────────────

        private void Root_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void BtnReturn_Click(object sender, RoutedEventArgs e) => _main.ExitMiniMode();

        // Fermer la mini (Alt+F4 / barre des tâches) = revenir à la version complète,
        // sauf si l'application est réellement en train de quitter.
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_main.ShuttingDown)
            {
                e.Cancel = true;
                _main.ExitMiniMode();
            }
        }
    }
}
