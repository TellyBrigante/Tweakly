using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class PageReseauMonitoring : UserControl
    {
        private readonly MainWindow _main;
        private readonly DispatcherTimer _timer;
        private bool _busy;

        private const int MaxPoints = 60;
        private readonly List<double> _pingHist = new();   // -1 = perte
        private string _target = "1.1.1.1";
        private int _infoTick;                              // pour rafraîchir les infos moins souvent

        public PageReseauMonitoring(MainWindow main)
        {
            _main = main;
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += async (_, _) => await TickAsync();
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            TxtTarget.Text = _target;
            DrawGrid();
            await RefreshInfoAsync();
            _timer.Start();
            await TickAsync();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e) => _timer.Stop();

        // ── Boucle ───────────────────────────────────────────────────────────────

        private async Task TickAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var s = await NetworkMonitor.Instance.CollectAsync(_target);

                Push(_pingHist, s.PingMs);

                // Stats sur la fenêtre
                double jitter = ComputeJitter(_pingHist);
                double loss   = _pingHist.Count > 0
                    ? (double)_pingHist.Count(p => p < 0) / _pingHist.Count * 100.0 : 0;
                var valid = _pingHist.Where(p => p >= 0).ToList();
                double avg = valid.Count > 0 ? valid.Average() : -1;

                TxtPing.Text   = s.PingMs >= 0 ? $"{s.PingMs:F0}" : "—";
                TxtJitter.Text = valid.Count > 1 ? $"{jitter:F0} ms" : "—";
                TxtLoss.Text   = $"{loss:F0} %";
                TxtDown.Text   = $"{s.DownMbps:F1}";
                TxtUp.Text     = $"{s.UpMbps:F1}";

                UpdateVerdict(avg, jitter, loss, valid.Count);

                DrawLine();
                DrawLoss();

                // Infos connexion : toutes les ~10 s (et au 1er tick)
                if (++_infoTick >= 10) { _infoTick = 0; await RefreshInfoAsync(); }
            }
            catch { }
            finally { _busy = false; }
        }

        private async Task RefreshInfoAsync()
        {
            try
            {
                var info = await Task.Run(() => NetworkMonitor.Instance.GetInfo());
                TxtConnType.Text    = info.Type == "Wi-Fi" && info.WifiSignal >= 0
                    ? $"Wi-Fi · {info.WifiSignal} %" : info.Type;
                TxtConnAdapter.Text = info.Adapter;
                TxtLinkSpeed.Text   = info.LinkSpeed;
                TxtLocalIp.Text     = info.LocalIp;
                TxtGateway.Text     = info.Gateway;
                TxtDns.Text         = info.Dns;
            }
            catch { }
        }

        private void UpdateVerdict(double avg, double jitter, double loss, int validCount)
        {
            if (validCount == 0)
            {
                SetVerdict("HORS LIGNE", "ThCrit");
                return;
            }
            if (loss > 5)                       SetVerdict("INSTABLE",  "ThCrit");
            else if (avg >= 150 || jitter >= 30) SetVerdict("MOYEN",     "ThWarn");
            else if (avg >= 80  || jitter >= 15) SetVerdict("BON",       "ThAccentIcon");
            else                                 SetVerdict("EXCELLENT", "ThOk");
        }

        private void SetVerdict(string txt, string role)
        {
            TxtVerdict.Text        = txt;
            PillVerdict.SetResourceReference(Border.BackgroundProperty, role);
        }

        // ── Sélecteur de cible ─────────────────────────────────────────────────

        private void BtnTgtInternet_Click(object sender, RoutedEventArgs e) => SetTarget("1.1.1.1");

        private void BtnTgtGateway_Click(object sender, RoutedEventArgs e)
        {
            var gw = NetworkMonitor.Instance.GetGatewayIp();
            if (string.IsNullOrEmpty(gw)) { _main.Log("Réseau : passerelle introuvable."); return; }
            SetTarget(gw);
        }

        private void BtnTgtCustom_Click(object sender, RoutedEventArgs e)
        {
            var t = TxtCustomTarget.Text?.Trim();
            if (!string.IsNullOrEmpty(t)) SetTarget(t);
        }

        private void SetTarget(string target)
        {
            _target = target;
            TxtTarget.Text = target;
            _pingHist.Clear();
            PingLine.Points = new PointCollection();
            LossCanvas.Children.Clear();
            _main.Log($"Réseau : cible du ping → {target}.");
        }

        // ── Calculs ───────────────────────────────────────────────────────────

        private static double ComputeJitter(List<double> data)
        {
            double sum = 0; int n = 0; double? prev = null;
            foreach (var v in data)
            {
                if (v < 0) { prev = null; continue; }   // une perte casse la continuité
                if (prev.HasValue) { sum += Math.Abs(v - prev.Value); n++; }
                prev = v;
            }
            return n > 0 ? sum / n : 0;
        }

        private static void Push(List<double> list, double v)
        {
            list.Add(v);
            if (list.Count > MaxPoints) list.RemoveAt(0);
        }

        private double Scale()
        {
            double maxV = _pingHist.Where(p => p >= 0).DefaultIfEmpty(0).Max();
            return Math.Max(50, Math.Ceiling(maxV * 1.2 / 25.0) * 25.0);
        }

        // ── Graphe ──────────────────────────────────────────────────────────────

        private void GraphArea_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawGrid();
            DrawLine();
            DrawLoss();
        }

        private void DrawGrid()
        {
            GridCanvas.Children.Clear();
            double w = GraphArea.ActualWidth, h = GraphArea.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // v1.3.5 : grille pointillée discrète (alignée sur Monitoring Système)
            foreach (var p in new[] { 25, 50, 75 })
            {
                double y = h - (p / 100.0) * h;
                var line = new Line
                {
                    X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    StrokeThickness = 1, Opacity = 0.35,
                    StrokeDashArray = new DoubleCollection { 3, 5 },
                };
                line.SetResourceReference(Shape.StrokeProperty, "ThBorder");   // suit le thème
                GridCanvas.Children.Add(line);
            }
        }

        private void DrawLine()
        {
            TxtScale.Text = $"éch. {Scale():F0} ms";

            double w = GraphArea.ActualWidth, h = GraphArea.ActualHeight;
            if (w <= 0 || h <= 0 || _pingHist.Count < 2) { PingLine.Points = new PointCollection(); return; }

            double scale = Scale();
            double step  = w / (MaxPoints - 1);
            int count    = _pingHist.Count;
            var pts = new PointCollection();
            for (int i = 0; i < count; i++)
            {
                double v = _pingHist[i];
                if (v < 0) continue;   // perte : pas de point (marqueur rouge à part)
                double x = w - (count - 1 - i) * step;
                double y = h - Math.Min(1.0, v / scale) * h;
                pts.Add(new Point(x, y));
            }
            // v1.3.5 : courbe lissée (Catmull-Rom, aligné sur Monitoring Système)
            PingLine.Points = pts.Count >= 2 ? CatmullRom(pts, 6, h) : pts;
        }

        // Interpolation Catmull-Rom : passe par TOUS les points mesurés, n'arrondit que
        // le trajet entre eux (même rendu que le graphe de Monitoring Système).
        private static PointCollection CatmullRom(PointCollection p, int subdiv, double maxY)
        {
            var outPts = new PointCollection((p.Count - 1) * subdiv + 1);
            for (int i = 0; i < p.Count - 1; i++)
            {
                var p0 = p[Math.Max(0, i - 1)];
                var p1 = p[i];
                var p2 = p[i + 1];
                var p3 = p[Math.Min(p.Count - 1, i + 2)];
                for (int s = 0; s < subdiv; s++)
                {
                    double t = s / (double)subdiv, t2 = t * t, t3 = t2 * t;
                    double x = 0.5 * ((2 * p1.X) + (-p0.X + p2.X) * t
                             + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2
                             + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
                    double y = 0.5 * ((2 * p1.Y) + (-p0.Y + p2.Y) * t
                             + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2
                             + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
                    outPts.Add(new Point(x, Math.Max(0, Math.Min(maxY, y))));
                }
            }
            outPts.Add(p[p.Count - 1]);
            return outPts;
        }

        private void DrawLoss()
        {
            LossCanvas.Children.Clear();
            double w = GraphArea.ActualWidth, h = GraphArea.ActualHeight;
            if (w <= 0 || h <= 0 || _pingHist.Count < 2) return;

            double step = w / (MaxPoints - 1);
            int count   = _pingHist.Count;
            for (int i = 0; i < count; i++)
            {
                if (_pingHist[i] >= 0) continue;
                double x = w - (count - 1 - i) * step;
                var dot = new Ellipse { Width = 6, Height = 6 };
                dot.SetResourceReference(Shape.FillProperty, "ThCrit");
                Canvas.SetLeft(dot, x - 3);
                Canvas.SetTop(dot, 3);
                LossCanvas.Children.Add(dot);
            }
        }
    }
}
