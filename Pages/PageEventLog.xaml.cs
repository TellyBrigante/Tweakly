using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class PageEventLog : UserControl
    {
        private readonly MainWindow _main;
        private bool _scanning;
        private bool _firstDone;
        private int  _days = 7;
        private bool _incidentView = true;   // vue par défaut = corrélée

        private List<LogEntry> _bySource = new();
        private List<Incident> _byIncident = new();

        public PageEventLog(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_firstDone && !_scanning) { _firstDone = true; await RunScanAsync(); }
        }

        private async void Btn7_Click(object sender, RoutedEventArgs e)  { _days = 7;  await RunScanAsync(); }
        private async void Btn30_Click(object sender, RoutedEventArgs e) { _days = 30; await RunScanAsync(); }
        private async void BtnScan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();

        private void BtnViewIncident_Click(object sender, RoutedEventArgs e) { _incidentView = true;  Render(); }
        private void BtnViewSource_Click(object sender, RoutedEventArgs e)   { _incidentView = false; Render(); }

        private async Task RunScanAsync()
        {
            if (_scanning) return;
            _scanning         = true;
            BtnScan.IsEnabled = false;
            TxtStatus.Text    = $"Lecture des journaux Windows ({_days} derniers jours)…";
            _main.Log($"Erreurs Windows : analyse des journaux ({_days} j)…");

            try
            {
                var (src, inc) = await Task.Run(() =>
                    (EventLogDecoder.Scan(_days), EventLogDecoder.ScanIncidents(_days)));
                _bySource   = src;
                _byIncident = inc;
            }
            catch (Exception ex) { _main.Log($"Erreurs Windows : erreur — {ex.Message}"); }

            Render();
            _main.Log($"Erreurs Windows : {_byIncident.Count} incident(s), {_bySource.Count} source(s).");

            BtnScan.IsEnabled = true;
            _scanning         = false;
        }

        private void Render()
        {
            if (_incidentView) RenderIncidents();
            else               RenderSource();
        }

        // ══ VUE PAR INCIDENT (corrélée) ══════════════════════════════════════
        private void RenderIncidents()
        {
            ResultsPanel.Children.Clear();
            TxtSummary.Text = $"Vue par incident · {_byIncident.Count} incident(s) corrélé(s) sur {_days} j";
            TxtStatus.Text  = "Événements regroupés par proximité temporelle (probablement liés).";

            if (_byIncident.Count == 0)
            {
                ResultsPanel.Children.Add(Tb("Aucun incident corrélé sur la période. 🎉", "ThTextDim", 13,
                    wrap: true, margin: new Thickness(2, 8, 0, 0)));
                return;
            }

            int idx = 0;
            foreach (var inc in _byIncident) AddIncidentCard(inc, ref idx);
        }

        private void AddIncidentCard(Incident inc, ref int idx)
        {
            var card  = new Border { Style = (Style)FindResource("DTile"), Margin = new Thickness(0, 0, 0, 10) };
            var stack = new StackPanel();

            // En-tête : pastille + cause racine | date · n events · durée
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            var hdot = new Ellipse
            {
                Width = 10, Height = 10,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            hdot.SetResourceReference(Shape.FillProperty, SevRole(inc.Sev));
            titleRow.Children.Add(hdot);
            var title = Tb(inc.Title, "ThTextTitle", 13.5, bold: true);
            title.TextTrimming = TextTrimming.CharacterEllipsis; title.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(title);
            Grid.SetColumn(titleRow, 0); head.Children.Add(titleRow);

            int span = (int)Math.Round((inc.End - inc.Start).TotalSeconds);
            var meta = Tb($"{inc.Start:dd/MM HH:mm}  ·  {inc.Count} évts  ·  {span}s", "ThTextDim", 11.5,
                          margin: new Thickness(10, 0, 0, 0));
            meta.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(meta, 1); head.Children.Add(meta);
            stack.Children.Add(head);

            // Enchaînement
            stack.Children.Add(Tb("Enchaînement : " + inc.Chain, "ThTextDim", 12, wrap: true,
                                  margin: new Thickness(0, 10, 0, 0)));

            // Recommandation (le cœur)
            stack.Children.Add(new TextBlock
            {
                Text = "Recommandation",
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0xA0, 0xFF)),
                FontFamily = (FontFamily)FindResource("AppFont"), FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 2),
            });
            stack.Children.Add(Tb(inc.Advice, "ThTextBody", 12.5, wrap: true, margin: new Thickness(0, 0, 0, 0)));

            // Détail des événements (capé)
            int showN = Math.Min(8, inc.Events.Count);
            for (int i = 0; i < showN; i++)
            {
                var (t, ttl, sev) = inc.Events[i];
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, i == 0 ? 10 : 3, 0, 0) };
                var edot = new Ellipse
                {
                    Width = 6, Height = 6,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0),
                };
                edot.SetResourceReference(Shape.FillProperty, SevRole(sev));
                line.Children.Add(edot);
                line.Children.Add(Tb($"{t:HH:mm:ss}   {ttl}", "ThTextDim", 11));
                stack.Children.Add(line);
            }
            if (inc.Events.Count > showN)
                stack.Children.Add(Tb($"+ {inc.Events.Count - showN} autre(s) événement(s)", "ThTextDim", 11,
                                      margin: new Thickness(16, 3, 0, 0)));

            card.Child = stack;
            ResultsPanel.Children.Add(card);
            Anim.FadeSlideIn(card, 14, 240, idx++ * 55);
        }

        // ══ VUE PAR SOURCE ═══════════════════════════════════════════════════
        private void RenderSource()
        {
            ResultsPanel.Children.Clear();

            var aRegarder = _bySource.Where(i => i.Sev != LogSev.Benign).ToList();
            var benins    = _bySource.Where(i => i.Sev == LogSev.Benign).ToList();
            int total     = _bySource.Sum(i => i.Count);
            TxtSummary.Text = $"Vue par source · {aRegarder.Count} à regarder · {benins.Count} bénin(s) · {total} évts sur {_days} j";
            TxtStatus.Text  = $"{_bySource.Count} source(s) distincte(s).";

            if (_bySource.Count == 0)
            {
                ResultsPanel.Children.Add(Tb("Aucune erreur ni événement critique sur la période. 🎉", "ThTextDim", 13,
                    wrap: true, margin: new Thickness(2, 8, 0, 0)));
                return;
            }

            int idx = 0;
            if (aRegarder.Count > 0) { AddHeader("À REGARDER");                foreach (var it in aRegarder) AddSourceCard(it, ref idx); }
            if (benins.Count > 0)    { AddHeader("GÉNÉRALEMENT INOFFENSIF");   foreach (var it in benins)    AddSourceCard(it, ref idx); }
        }

        private void AddHeader(string text)
            => ResultsPanel.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("DCat"), Margin = new Thickness(2, 6, 0, 8) });

        private void AddSourceCard(LogEntry it, ref int idx)
        {
            var card  = new Border { Style = (Style)FindResource("DTile"), Margin = new Thickness(0, 0, 0, 10) };
            var stack = new StackPanel();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            var sdot = new Ellipse
            {
                Width = 10, Height = 10,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            sdot.SetResourceReference(Shape.FillProperty, SevRole(it.Sev));
            titleRow.Children.Add(sdot);
            var title = Tb(it.Title, "ThTextTitle", 13.5, bold: true);
            title.TextTrimming = TextTrimming.CharacterEllipsis; title.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(title);
            Grid.SetColumn(titleRow, 0); head.Children.Add(titleRow);

            var meta = Tb($"{it.Count}×  ·  {it.Last:dd/MM HH:mm}", "ThTextDim", 11.5, margin: new Thickness(10, 0, 0, 0));
            meta.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(meta, 1); head.Children.Add(meta);
            stack.Children.Add(head);

            stack.Children.Add(Tb(it.What, "ThTextBody", 12.5, wrap: true, margin: new Thickness(0, 10, 0, 0)));
            stack.Children.Add(Labeled("Cause", it.Cause));
            stack.Children.Add(Labeled("Piste de correction", it.Fix, accent: true));

            if (!string.IsNullOrWhiteSpace(it.Raw))
                stack.Children.Add(Tb("Détail : " + it.Raw, "ThTextDim", 11, wrap: true, italic: true, margin: new Thickness(0, 8, 0, 0)));

            if (!it.Known)
            {
                var btn = new Button
                {
                    Content = "Rechercher sur le web",
                    Style = (Style)FindResource("SecondaryBtnStyle"),
                    Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                string q = it.Provider + " " + it.Id + " event windows";
                btn.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(q)) { UseShellExecute = true }); }
                    catch { }
                };
                stack.Children.Add(btn);
            }

            card.Child = stack;
            ResultsPanel.Children.Add(card);
            Anim.FadeSlideIn(card, 14, 240, idx++ * 55);
        }

        private StackPanel Labeled(string label, string value, bool accent = false)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var lbl = new TextBlock
            {
                Text = label + " : ",
                FontFamily = (FontFamily)FindResource("AppFont"), FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Top,
            };
            if (accent) lbl.Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0xA0, 0xFF));
            else        lbl.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            sp.Children.Add(lbl);
            sp.Children.Add(Tb(value, "ThTextBody", 12.5, wrap: true, maxWidth: 720));
            return sp;
        }

        // TextBlock dont la couleur SUIT le thème (DynamicResource via SetResourceReference)
        private TextBlock Tb(string text, string fgKey, double size,
                             bool bold = false, bool wrap = false, bool italic = false,
                             Thickness margin = default, double maxWidth = double.PositiveInfinity)
        {
            var tb = new TextBlock
            {
                Text         = text,
                FontFamily   = (FontFamily)FindResource("AppFont"),
                FontSize     = size,
                FontWeight   = bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle    = italic ? FontStyles.Italic : FontStyles.Normal,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Margin       = margin,
            };
            if (!double.IsPositiveInfinity(maxWidth)) tb.MaxWidth = maxWidth;
            tb.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            return tb;
        }

        // Rôle de couleur thémable selon la gravité (jaune vif en sombre, ambre lisible en clair)
        private static string SevRole(LogSev s) => s switch
        {
            LogSev.Serious => "ThCrit",
            LogSev.Warning => "ThWarn",
            _              => "ThTextDim",
        };
    }
}
