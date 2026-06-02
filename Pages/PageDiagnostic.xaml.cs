using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class PageDiagnostic : UserControl
    {
        private readonly MainWindow _main;
        private bool _scanning;

        // Ordre d'affichage des catégories
        private static readonly string[] CatOrder =
            { "Stockage", "Mémoire", "Carte graphique", "Matériel", "Système" };

        public PageDiagnostic(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Scan automatique au premier affichage seulement
            if (ResultsPanel.Children.Count == 0 && !_scanning)
                await RunScanAsync();
        }

        private async void BtnScan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();

        private async Task RunScanAsync()
        {
            if (_scanning) return;
            _scanning         = true;
            BtnScan.IsEnabled = false;
            TxtStatus.Text    = "Analyse du système en cours…";
            _main.Log("Diagnostic : analyse du système…");

            var items = new List<HealthItem>();
            try { items = await Task.Run(HealthCheck.Scan); }
            catch (Exception ex) { _main.Log($"Diagnostic : erreur — {ex.Message}"); }

            Render(items);

            int ok   = items.Count(i => i.Status == HStatus.Ok);
            int warn = items.Count(i => i.Status == HStatus.Warning);
            int crit = items.Count(i => i.Status == HStatus.Critical);
            Anim.CountUp(TxtOk,   ok,   600);
            Anim.CountUp(TxtWarn, warn, 600);
            Anim.CountUp(TxtCrit, crit, 600);

            TxtStatus.Text = $"Analyse terminée — {items.Count} point(s) vérifié(s).";
            _main.Log($"Diagnostic : terminé — {ok} OK, {warn} avertissement(s), {crit} critique(s).");
            BtnScan.IsEnabled = true;
            _scanning         = false;
        }

        private void Render(List<HealthItem> items)
        {
            ResultsPanel.Children.Clear();
            int idx = 0;
            void Add(Border card)
            {
                ResultsPanel.Children.Add(card);
                Anim.FadeSlideIn(card, 14, 240, idx++ * 70);   // apparition en cascade
            }

            foreach (var cat in CatOrder)
            {
                var catItems = items.Where(i => i.Category == cat).ToList();
                if (catItems.Count > 0) Add(BuildCategory(cat, catItems));
            }
            // Catégories imprévues éventuelles
            foreach (var cat in items.Select(i => i.Category).Distinct().Where(c => !CatOrder.Contains(c)))
                Add(BuildCategory(cat, items.Where(i => i.Category == cat).ToList()));
        }

        private Border BuildCategory(string category, List<HealthItem> catItems)
        {
            var card  = new Border { Style = (Style)FindResource("DTile"), Margin = new Thickness(0, 0, 0, 12) };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = category.ToUpperInvariant(), Style = (Style)FindResource("DCat") });

            foreach (var it in catItems)
                stack.Children.Add(BuildRow(it));

            card.Child = stack;
            return card;
        }

        private Grid BuildRow(HealthItem it)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            bool storage = !string.IsNullOrEmpty(it.Detail);   // ligne stockage = 4 compartiments

            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // pastille
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) }); // titre
            if (storage)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });                  // espace
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Santé du SSD
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // état (Sain) — fin de ligne
            }
            else
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // message
            }

            // Pastille = pire des statuts présents
            var worst = it.Status;
            if (storage) { worst = Worst(worst, it.DetailStatus); worst = Worst(worst, it.ExtraStatus); }
            var dot = new Ellipse
            {
                Width = 10, Height = 10, Fill = StatusBrush(worst),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            };
            Grid.SetColumn(dot, 0);
            row.Children.Add(dot);

            var title = new TextBlock
            {
                Text = it.Title, Foreground = (Brush)FindResource("ThTextBody"),
                FontFamily = (FontFamily)FindResource("AppFont"), FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            };
            Grid.SetColumn(title, 1);
            row.Children.Add(title);

            // Colonne 2 (message / espace) : neutre si OK, coloré si problème
            var msg = new TextBlock
            {
                Text = it.Message,
                Foreground = it.Status == HStatus.Ok ? (Brush)FindResource("ThTextDim") : StatusBrush(it.Status),
                FontFamily = (FontFamily)FindResource("AppFont"), FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            Grid.SetColumn(msg, 2);
            row.Children.Add(msg);

            if (storage)
            {
                // Colonne 3 : Santé du SSD (vide si non disponible, ex. SATA sans attribut)
                if (!string.IsNullOrEmpty(it.Extra))
                {
                    var extra = new TextBlock
                    {
                        Text = it.Extra, Foreground = StatusBrush(it.ExtraStatus),
                        FontFamily = (FontFamily)FindResource("AppFont"), FontSize = 12.5,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                    };
                    Grid.SetColumn(extra, 3);
                    row.Children.Add(extra);
                }

                // Colonne 4 : état (Sain) — tout à la fin de la ligne
                var det = new TextBlock
                {
                    Text = it.Detail, Foreground = StatusBrush(it.DetailStatus), FontWeight = FontWeights.SemiBold,
                    FontFamily = (FontFamily)FindResource("AppFont"), FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
                };
                Grid.SetColumn(det, 4);
                row.Children.Add(det);
            }
            return row;
        }

        private static int Severity(HStatus s) => s switch
        {
            HStatus.Critical => 3, HStatus.Warning => 2, HStatus.Ok => 1, _ => 0,
        };
        private static HStatus Worst(HStatus a, HStatus b) => Severity(a) >= Severity(b) ? a : b;

        private static Brush StatusBrush(HStatus s) => s switch
        {
            HStatus.Ok       => new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0x6A)),
            HStatus.Warning  => new SolidColorBrush(Color.FromRgb(0xF5, 0xC2, 0x4A)),
            HStatus.Critical => new SolidColorBrush(Color.FromRgb(0xE0, 0x55, 0x55)),
            _                => new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xCC)),
        };
    }
}
