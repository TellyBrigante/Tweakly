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
            // Statut informatif : on dit ce qu'on FAIT pendant le scan (lecture XML enrichi,
            // recoupement des marqueurs, détection des récurrences) — l'utilisateur voit que
            // l'app travaille en profondeur plutôt que de cracher des résultats au hasard.
            TxtStatus.Text    = $"Lecture des journaux Windows + recoupement des preuves ({_days} derniers jours)…";
            _main.Log($"Erreurs Windows : analyse en profondeur ({_days} j) — extraction XML, tâches planifiées, récurrences…");

            try
            {
                var (src, inc) = await Task.Run(() => EventLogDecoder.ScanAll(_days));
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
            // Wrapper : bandeau couleur sévérité à gauche (4px) + carte (refonte UI v1.3.0)
            var wrapper = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var stripe = new Border { CornerRadius = new CornerRadius(2, 0, 0, 2) };
            stripe.SetResourceReference(Border.BackgroundProperty, SevRole(inc.Sev));
            Grid.SetColumn(stripe, 0); wrapper.Children.Add(stripe);

            var card  = new Border { Style = (Style)FindResource("DTile") };
            var stack = new StackPanel();

            // ── En-tête : grosse icône + titre 16 + badge sévérité + meta ──
            int spanSeconds = Math.Max(0, (int)(inc.End - inc.Start).TotalSeconds);
            string meta = inc.Episodes > 1
                ? $"{inc.Start:dd/MM HH:mm}  ·  {inc.Episodes} séquences  ·  {inc.Count} évts  ·  {spanSeconds} s"
                : $"{inc.Start:dd/MM HH:mm}  ·  {inc.Count} évts  ·  {spanSeconds} s";
            stack.Children.Add(BuildHeader(inc.Icon, inc.Title, inc.Sev, meta));

            // Enchaînement (sous-titre discret)
            if (!string.IsNullOrWhiteSpace(inc.Chain))
                stack.Children.Add(Tb("Enchaînement : " + inc.Chain, "ThTextDim", 12, wrap: true,
                                      margin: new Thickness(58, 6, 0, 0)));

            // ── Pourquoi (Advice court) ──
            if (!string.IsNullOrWhiteSpace(inc.Advice))
            {
                stack.Children.Add(BuildSectionHeader("", "Pourquoi"));  // Info glyph
                stack.Children.Add(BuildAdviceBlock(inc.Advice));
            }

            // ── Que faire (étapes numérotées) ──
            if (inc.Steps != null && inc.Steps.Count > 0)
            {
                stack.Children.Add(BuildSectionHeader("", "Que faire"));  // Repair glyph
                var box = BuildStepsList(inc.Steps);
                box.Margin = new Thickness(58, 2, 0, 0);
                stack.Children.Add(box);
            }

            // ── Boutons d'action ──
            if (inc.Actions != null && inc.Actions.Count > 0)
            {
                var ar = BuildActionsRow(inc.Actions);
                ar.Margin = new Thickness(58, 12, 0, 0);
                stack.Children.Add(ar);
            }

            // ── Détail des événements (capé) ──
            int showN = Math.Min(8, inc.Events.Count);
            for (int i = 0; i < showN; i++)
            {
                var (t, ttl, sev) = inc.Events[i];
                var line = new StackPanel { Orientation = Orientation.Horizontal,
                                            Margin = new Thickness(58, i == 0 ? 14 : 3, 0, 0) };
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
                                      margin: new Thickness(74, 3, 0, 0)));

            card.Child = stack;
            Grid.SetColumn(card, 1); wrapper.Children.Add(card);
            ResultsPanel.Children.Add(wrapper);
            Anim.FadeSlideIn(wrapper, 14, 240, idx++ * 55);
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
            // Wrapper : bandeau couleur sévérité à gauche (4px) + carte
            var wrapper = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var stripe = new Border { CornerRadius = new CornerRadius(2, 0, 0, 2) };
            stripe.SetResourceReference(Border.BackgroundProperty, SevRole(it.Sev));
            Grid.SetColumn(stripe, 0); wrapper.Children.Add(stripe);

            var card  = new Border { Style = (Style)FindResource("DTile") };
            var stack = new StackPanel();

            // ── En-tête : grosse icône + titre 16 + badge sévérité + meta ──
            stack.Children.Add(BuildHeader(it.Icon, it.Title, it.Sev,
                $"{it.Count}×  ·  {it.Last:dd/MM HH:mm}"));

            // ── Quoi (What) ──
            stack.Children.Add(Tb(it.What, "ThTextBody", 12.5, wrap: true,
                                  margin: new Thickness(58, 6, 0, 0)));

            // ── Pourquoi (Cause) ──
            if (!string.IsNullOrWhiteSpace(it.Cause))
            {
                stack.Children.Add(BuildSectionHeader("", "Pourquoi"));
                stack.Children.Add(Tb(it.Cause, "ThTextBody", 12.5, wrap: true,
                                      margin: new Thickness(58, 2, 0, 0)));
            }

            // ── Que faire ──
            if (it.Steps != null && it.Steps.Count > 0)
            {
                stack.Children.Add(BuildSectionHeader("", "Que faire"));
                var box = BuildStepsList(it.Steps);
                box.Margin = new Thickness(58, 2, 0, 0);
                stack.Children.Add(box);
            }
            else if (!string.IsNullOrWhiteSpace(it.Fix))
            {
                stack.Children.Add(BuildSectionHeader("", "Que faire"));
                stack.Children.Add(Tb(it.Fix, "ThTextBody", 12.5, wrap: true,
                                      margin: new Thickness(58, 2, 0, 0)));
            }

            // ── Boutons d'action ──
            if (it.Actions != null && it.Actions.Count > 0)
            {
                var ar = BuildActionsRow(it.Actions);
                ar.Margin = new Thickness(58, 12, 0, 0);
                stack.Children.Add(ar);
            }

            if (!string.IsNullOrWhiteSpace(it.Raw))
                stack.Children.Add(Tb("Détail : " + it.Raw, "ThTextDim", 11, wrap: true, italic: true,
                                      margin: new Thickness(58, 10, 0, 0)));

            if (!it.Known)
            {
                var btn = new Button
                {
                    Content = "Rechercher sur le web",
                    Style = (Style)FindResource("SecondaryBtnStyle"),
                    Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(58, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                string q = it.Provider + " " + it.Id + " event windows";
                btn.Click += (_, _) =>
                {
                    try { using var _ = Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(q)) { UseShellExecute = true }); }
                    catch { }
                };
                stack.Children.Add(btn);
            }

            card.Child = stack;
            Grid.SetColumn(card, 1); wrapper.Children.Add(card);
            ResultsPanel.Children.Add(wrapper);
            Anim.FadeSlideIn(wrapper, 14, 240, idx++ * 55);
        }

        private StackPanel BuildAdviceBlock(string text)
        {
            var sp = new StackPanel { Margin = new Thickness(58, 2, 0, 0) };
            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                var tb = Tb(line, "ThTextBody", 12.5, wrap: true,
                            bold: line.EndsWith(":", StringComparison.Ordinal),
                            margin: new Thickness(0, sp.Children.Count == 0 ? 0 : 5, 0, 0));
                tb.LineHeight = 17;
                sp.Children.Add(tb);
            }
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

        // ══ Helpers v1.3.0 : en-tête visuel + sections + étapes + boutons ══════

        /// <summary>
        /// En-tête de carte : grosse icône (44px) dans un cercle teinté sévérité, titre 16,
        /// badge "CRITIQUE / ATTENTION / INFO" coloré, meta date alignée à droite.
        /// </summary>
        private Grid BuildHeader(string icon, string title, LogSev sev, string meta)
        {
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });           // icône
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // titre + badge
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });           // meta

            // -- Col 0 : icône MDL2 dans un cercle teinté sévérité --
            // Centrage exact via Viewbox (Segoe MDL2 a un baseline shift qui décale
            // visuellement les glyphes dans un TextBlock — la Viewbox les normalise).
            // Glyphe par défaut selon sévérité si l'entrée n'a pas d'icône explicite
            // → plus jamais de cercle vide (cohérence visuelle v1.3.0).
            var iconCell = new Grid
            {
                Width = 44, Height = 44,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 14, 0),
            };
            var iconBg = new Ellipse { Opacity = 0.18 };
            iconBg.SetResourceReference(Shape.FillProperty, SevRole(sev));
            iconCell.Children.Add(iconBg);

            // Glyphe : celui de l'entrée, ou un fallback selon sévérité
            string glyph = !string.IsNullOrEmpty(icon)
                ? icon
                : DefaultIconFor(sev);

            var iconText = new TextBlock
            {
                Text       = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize   = 18,
                TextAlignment = TextAlignment.Center,
            };
            iconText.SetResourceReference(TextBlock.ForegroundProperty, SevRole(sev));
            // Viewbox : centrage géométrique exact, indépendant du baseline du glyphe
            var box = new Viewbox
            {
                Width  = 22, Height = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Stretch = Stretch.Uniform,
                Child   = iconText,
            };
            iconCell.Children.Add(box);

            Grid.SetColumn(iconCell, 0); head.Children.Add(iconCell);

            // -- Col 1 : badge sévérité (pilule colorée) + titre 16 --
            var titleCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            // Badge "CRITIQUE / ATTENTION / INFO" en pilule "outlined" :
            // - fond TRÈS PÂLE (Opacity 0.18 sur un Border séparé)
            // - texte en COULEUR PLEINE de la sévérité
            // → lisible en clair ET en sombre, indépendant de la teinte (résout le bug
            //   v1.3.0 "écriture blanche sur fond jaune ThWarn = illisible").
            var badgeCell = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(0, 0, 0, 4),
            };
            var badgeBg = new Border
            {
                CornerRadius = new CornerRadius(8),
                Opacity      = 0.18,
            };
            badgeBg.SetResourceReference(Border.BackgroundProperty, SevRole(sev));
            badgeCell.Children.Add(badgeBg);

            var badgeText = new TextBlock
            {
                Text       = SevLabel(sev),
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize   = 9.5,
                FontWeight = FontWeights.Bold,
                Margin     = new Thickness(8, 1, 8, 2),
            };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, SevRole(sev));
            badgeCell.Children.Add(badgeText);

            titleCol.Children.Add(badgeCell);

            // Titre 16 gras
            var titleTb = new TextBlock
            {
                Text         = title,
                FontFamily   = (FontFamily)FindResource("AppFont"),
                FontSize     = 15.5,
                FontWeight   = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "ThTextTitle");
            titleCol.Children.Add(titleTb);

            Grid.SetColumn(titleCol, 1); head.Children.Add(titleCol);

            // -- Col 2 : meta (date + compteur) --
            var metaTb = Tb(meta, "ThTextDim", 11.5, margin: new Thickness(10, 6, 0, 0));
            metaTb.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(metaTb, 2); head.Children.Add(metaTb);

            return head;
        }

        /// <summary>
        /// En-tête de section ("Pourquoi" / "Que faire") avec un glyphe Segoe MDL2
        /// à gauche et le texte en accent bleu. Aligne à 58px pour rester sous l'icône
        /// principale de l'en-tête (cohérence visuelle).
        /// </summary>
        private StackPanel BuildSectionHeader(string glyph, string label)
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(58, 14, 0, 0),
            };
            if (!string.IsNullOrEmpty(glyph))
            {
                var gl = new TextBlock
                {
                    Text       = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize   = 13,
                    Margin     = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                gl.SetResourceReference(TextBlock.ForegroundProperty, "ThAccentIcon");
                sp.Children.Add(gl);
            }
            var lblTb = new TextBlock
            {
                Text       = label,
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize   = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            lblTb.SetResourceReference(TextBlock.ForegroundProperty, "ThAccentIcon");
            sp.Children.Add(lblTb);
            return sp;
        }

        /// <summary>Libellé court de sévérité pour le badge.</summary>
        private static string SevLabel(LogSev s) => s switch
        {
            LogSev.Serious => "CRITIQUE",
            LogSev.Warning => "ATTENTION",
            _              => "INFO",
        };

        /// <summary>
        /// Glyphe MDL2 par défaut quand l'entrée n'a pas d'icône explicite — selon la
        /// sévérité (croix d'erreur / triangle warning / info). Évite les cercles vides
        /// dans les cartes des sources mineures pas encore enrichies (cohérence v1.3.0).
        /// </summary>
        private static string DefaultIconFor(LogSev s) => s switch
        {
            LogSev.Serious => "",   // ErrorBadge12 (croix dans cercle)
            LogSev.Warning => "",   // Warning (triangle !)
            _              => "",   // Info
        };

        // ══ Anciens helpers (étapes + actions) ════════════════════════════════

        /// <summary>
        /// Construit une liste d'étapes visuellement claire : chaque étape sur sa propre ligne
        /// avec un numéro (1. 2. 3.) en couleur accent. Bien plus lisible qu'un bloc de texte
        /// avec des "1) 2) 3)" embarqués dans une string.
        /// </summary>
        private StackPanel BuildStepsList(System.Collections.Generic.List<string> steps)
        {
            var container = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            int n = 1;
            foreach (var step in steps)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var num = new TextBlock
                {
                    Text       = n + ".",
                    FontFamily = (FontFamily)FindResource("AppFont"),
                    FontSize   = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                num.SetResourceReference(TextBlock.ForegroundProperty, "ThAccentIcon");
                Grid.SetColumn(num, 0); row.Children.Add(num);

                var txt = Tb(step, "ThTextBody", 12.5, wrap: true);
                Grid.SetColumn(txt, 1); row.Children.Add(txt);

                container.Children.Add(row);
                n++;
            }
            return container;
        }

        /// <summary>
        /// Construit une ligne de boutons d'action (navigation interne, commandes, URL, diag).
        /// WrapPanel pour gérer plusieurs boutons qui passent à la ligne sur les petits écrans.
        /// </summary>
        private WrapPanel BuildActionsRow(System.Collections.Generic.List<LogAction> actions)
        {
            var wp = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var act in actions)
            {
                var btn = new Button
                {
                    Content  = act.Label,
                    Style    = (Style)FindResource("SecondaryBtnStyle"),
                    Padding  = new Thickness(12, 6, 12, 6),
                    Margin   = new Thickness(0, 0, 8, 6),
                    ToolTip  = string.IsNullOrWhiteSpace(act.Tooltip) ? null : act.Tooltip,
                };
                btn.Click += (_, _) => ExecuteAction(act);
                wp.Children.Add(btn);
            }
            return wp;
        }

        /// <summary>
        /// Exécute une action (selon son Kind). Pour les commandes potentiellement longues ou
        /// risquées (Confirm = true), demande confirmation à l'utilisateur d'abord.
        /// </summary>
        private void ExecuteAction(LogAction act)
        {
            try
            {
                if (act.Confirm)
                {
                    var r = MessageBox.Show(
                        $"Lancer cette commande ?\n\n{act.Target}\n\n{act.Tooltip}",
                        "Tweakly — Confirmer l'action",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question);
                    if (r != MessageBoxResult.OK) return;
                }

                switch (act.Kind)
                {
                    case LogActionKind.Navigate:
                        // Navigation interne : on cherche le bouton nav par Tag puis on l'invoque.
                        // MainWindow.NavigateTo prend un Button (BtnNavXxx) en paramètre.
                        TryNavigate(act.Target);
                        break;

                    case LogActionKind.Url:
                        using (var _ = Process.Start(new ProcessStartInfo(act.Target) { UseShellExecute = true })) { }
                        break;

                    case LogActionKind.Diag:
                        // Outils Windows standards (services.msc, devmgmt.msc, appwiz.cpl…)
                        using (var _ = Process.Start(new ProcessStartInfo(act.Target) { UseShellExecute = true })) { }
                        break;

                    case LogActionKind.Command:
                        // Commande système — on lance via cmd.exe (target est attendu type
                        // "cmd /k ..." ou "cmd /c ..."). Pas de redirection, fenêtre visible.
                        var (file, args) = SplitCmd(act.Target);
                        using (var _ = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true })) { }
                        break;
                }
                _main.Log($"Erreurs Windows : action exécutée — {act.Label}");
            }
            catch (Exception ex)
            {
                _main.Log($"Erreurs Windows : action « {act.Label} » impossible — {ex.Message}");
            }
        }

        /// <summary>Tente de naviguer vers une page interne via son Tag (ex. "Diagnostic", "Monitoring").</summary>
        private void TryNavigate(string tag)
        {
            // Le sidebar de MainWindow utilise des boutons avec Tag = clé de page. On parcourt
            // l'arbre logique en cherchant le bouton dont Tag == target, puis on appelle
            // MainWindow.NavigateTo (public).
            try
            {
                var btn = FindNavButton(_main, tag);
                if (btn != null) _main.NavigateTo(btn);
            }
            catch { }
        }

        private static Button? FindNavButton(DependencyObject root, string tag)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Button b && b.Tag is string s && s == tag) return b;
                var found = FindNavButton(child, tag);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Sépare "cmd /k sfc /scannow" en (file="cmd", args="/k sfc /scannow").</summary>
        private static (string file, string args) SplitCmd(string s)
        {
            s = (s ?? "").Trim();
            int i = s.IndexOf(' ');
            if (i <= 0) return (s, "");
            return (s.Substring(0, i), s.Substring(i + 1));
        }
    }
}
