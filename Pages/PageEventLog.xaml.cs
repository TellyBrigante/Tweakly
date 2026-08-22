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
                ? $"{inc.Start:dd/MM} → {inc.End:dd/MM}  ·  {inc.Episodes} épisodes  ·  {inc.Count} signaux"
                : $"{inc.Start:dd/MM HH:mm}  ·  {inc.Count} évts  ·  {spanSeconds} s";
            stack.Children.Add(BuildHeader(inc.Icon, inc.Title, inc.Sev, meta));

            // Enchaînement (sous-titre discret)
            if (!string.IsNullOrWhiteSpace(inc.Chain))
                stack.Children.Add(Tb("Enchaînement : " + inc.Chain, "ThTextDim", 12, wrap: true,
                                      margin: new Thickness(58, 6, 0, 0)));

            // Diagnostic v2 : une conclusion courte, le niveau de preuve et les faits
            // retenus. Les anciennes listes Advice/Steps/Actions restent dans le modèle
            // pour la compatibilité de la vue technique, mais ne sont plus présentées
            // comme des corrections dans la vue principale.
            stack.Children.Add(BuildDiagnosisBlock(inc));

            if (inc.Evidence.Count > 0)
            {
                stack.Children.Add(BuildSectionHeader("", "Preuves retenues"));
                stack.Children.Add(BuildEvidenceList(inc.Evidence));
            }

            stack.Children.Add(BuildInvestigationBlock(inc));
            stack.Children.Add(BuildRepairBlock(inc));

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
            TxtSummary.Text = $"Détails techniques · {aRegarder.Count} à regarder · {benins.Count} bénin(s) · {total} évts sur {_days} j";
            TxtStatus.Text  = $"{_bySource.Count} source(s) distincte(s), affichées sans conseil automatique.";

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

            if (!string.IsNullOrWhiteSpace(it.Raw))
                stack.Children.Add(Tb("Détail : " + it.Raw, "ThTextDim", 11, wrap: true, italic: true,
                                      margin: new Thickness(58, 10, 0, 0)));

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

        private Grid BuildDiagnosisBlock(Incident incident)
        {
            var grid = new Grid { Margin = new Thickness(58, 12, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            string label = incident.CauseState switch
            {
                IncidentCauseState.Established => "CAUSE ÉTABLIE",
                IncidentCauseState.Probable => "CAUSE PROBABLE",
                _ => "CAUSE NON ÉTABLIE",
            };
            string role = CauseRole(incident.CauseState);

            var badge = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            badge.SetResourceReference(Border.BorderBrushProperty, role);
            var badgeText = Tb(label, role, 9.5, bold: true);
            badge.Child = badgeText;
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var conclusion = Tb(
                string.IsNullOrWhiteSpace(incident.Conclusion)
                    ? "La conclusion n'est pas disponible."
                    : incident.Conclusion,
                "ThTextBody", 12.5, wrap: true);
            conclusion.LineHeight = 17;
            Grid.SetColumn(conclusion, 1);
            grid.Children.Add(conclusion);
            return grid;
        }

        private StackPanel BuildEvidenceList(IReadOnlyList<string> evidence)
        {
            var panel = new StackPanel { Margin = new Thickness(58, 2, 0, 0) };
            foreach (string item in evidence.Take(7))
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dot = new Ellipse
                {
                    Width = 5,
                    Height = 5,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                dot.SetResourceReference(Shape.FillProperty, "ThAccentIcon");
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);

                var text = Tb(item, "ThTextDim", 11.5, wrap: true);
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                panel.Children.Add(row);
            }
            return panel;
        }

        private StackPanel BuildRepairBlock(Incident incident)
        {
            var block = new StackPanel();
            block.Children.Add(BuildSectionHeader("", "Correction"));

            if (incident.Repair == null)
            {
                block.Children.Clear();
                return block;
            }

            IncidentRepairPlan plan = incident.Repair;
            string title = plan.Phase switch
            {
                IncidentRepairPhase.Ready => "Correction prête",
                IncidentRepairPhase.Running => "Opération en cours",
                IncidentRepairPhase.Corrected => "Correction validée",
                IncidentRepairPhase.NotPresent => "Défaut absent",
                IncidentRepairPhase.Blocked => "Correction non disponible",
                _ => plan.Title,
            };
            string role = RepairRole(plan.Phase);

            var titleText = Tb(title, role, 12.5, bold: true,
                margin: new Thickness(58, 2, 0, 0));
            block.Children.Add(titleText);

            string detail = plan.Status.Length > 0 ? plan.Status : plan.Detail;
            if (detail.Length > 0)
                block.Children.Add(Tb(detail, "ThTextBody", 12, wrap: true,
                    margin: new Thickness(58, 4, 0, 0)));

            if (plan.Phase is IncidentRepairPhase.NeedsDiagnosis or IncidentRepairPhase.Ready)
            {
                bool correction = plan.Phase == IncidentRepairPhase.Ready;
                var button = new Button
                {
                    Content = correction ? "CORRIGER" : "POURSUIVRE LE DIAGNOSTIC",
                    Style = (Style)FindResource(correction ? "CleanRunBtn" : "SecondaryBtnStyle"),
                    Padding = new Thickness(14, 8, 14, 8),
                    Margin = new Thickness(58, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                button.Click += async (_, _) => await ExecuteRepairPlanAsync(incident);
                block.Children.Add(button);
            }

            return block;
        }

        private StackPanel BuildInvestigationBlock(Incident incident)
        {
            var block = new StackPanel();
            IncidentInvestigationPlan? plan = incident.Investigation;
            if (plan == null) return block;

            block.Children.Add(BuildSectionHeader("", "Investigation active"));

            string title = plan.Phase switch
            {
                IncidentInvestigationPhase.Capturing => "Surveillance en cours",
                IncidentInvestigationPhase.Analyzing => "Analyse des dernières secondes",
                IncidentInvestigationPhase.Completed => "Analyse terminée",
                IncidentInvestigationPhase.Failed => "Capture interrompue",
                _ => plan.Title,
            };
            string role = plan.Phase == IncidentInvestigationPhase.Failed ? "ThWarn" : "ThAccentIcon";
            block.Children.Add(Tb(title, role, 12.5, bold: true,
                margin: new Thickness(58, 2, 0, 0)));

            string detail = plan.Status.Length > 0 ? plan.Status : plan.Detail;
            if (detail.Length > 0)
                block.Children.Add(Tb(detail, "ThTextBody", 12, wrap: true,
                    margin: new Thickness(58, 4, 0, 0)));

            if (plan.Phase is IncidentInvestigationPhase.Ready or IncidentInvestigationPhase.Capturing)
            {
                bool capturing = plan.Phase == IncidentInvestigationPhase.Capturing;
                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(58, 10, 0, 0),
                };
                var primary = new Button
                {
                    Content = capturing ? "LE PROBLÈME VIENT DE SE PRODUIRE" : "SURVEILLER LE PROCHAIN INCIDENT",
                    Style = (Style)FindResource(capturing ? "CleanRunBtn" : "SecondaryBtnStyle"),
                    Padding = new Thickness(14, 8, 14, 8),
                    IsEnabled = capturing || !WindowsFreezeInvestigator.IsCapturing,
                };
                primary.Click += async (_, _) => await ExecuteInvestigationAsync(incident);
                buttons.Children.Add(primary);

                if (capturing)
                {
                    var cancel = new Button
                    {
                        Content = "ANNULER",
                        Style = (Style)FindResource("SecondaryBtnStyle"),
                        Padding = new Thickness(14, 8, 14, 8),
                        Margin = new Thickness(8, 0, 0, 0),
                    };
                    cancel.Click += (_, _) =>
                    {
                        WindowsFreezeInvestigator.Cancel();
                        plan.Phase = IncidentInvestigationPhase.Ready;
                        plan.Status = "Surveillance annulée.";
                        Render();
                    };
                    buttons.Children.Add(cancel);
                }
                block.Children.Add(buttons);
            }

            return block;
        }

        private async Task ExecuteInvestigationAsync(Incident incident)
        {
            IncidentInvestigationPlan? plan = incident.Investigation;
            if (plan == null || plan.Phase == IncidentInvestigationPhase.Analyzing) return;

            try
            {
                if (plan.Phase == IncidentInvestigationPhase.Ready)
                {
                    await WindowsFreezeInvestigator.StartAsync();
                    plan.Phase = IncidentInvestigationPhase.Capturing;
                    plan.Status = $"Capture active depuis {WindowsFreezeInvestigator.StartedAt:HH:mm:ss}. Reproduis {plan.Target}, puis clique sur le bouton dès que Windows répond de nouveau.";
                    _main.Log($"Erreurs Windows : capture active pour {incident.Title}");
                    Render();
                    return;
                }

                if (plan.Phase != IncidentInvestigationPhase.Capturing) return;
                plan.Phase = IncidentInvestigationPhase.Analyzing;
                plan.Status = "Arrêt de la capture et analyse des 20 dernières secondes…";
                Render();

                FreezeInvestigationReport report = await WindowsFreezeInvestigator.StopAndAnalyzeAsync();
                plan.Phase = report.IsValid
                    ? IncidentInvestigationPhase.Completed
                    : IncidentInvestigationPhase.Failed;
                plan.Status = report.Conclusion;
                incident.CauseState = report.CauseState;
                incident.Conclusion = report.Conclusion;
                foreach (string evidence in report.Evidence)
                    if (!incident.Evidence.Contains(evidence, StringComparer.OrdinalIgnoreCase))
                        incident.Evidence.Add(evidence);
                _main.Log($"Erreurs Windows : analyse de capture — {report.Conclusion}");
            }
            catch (Exception ex)
            {
                AppLog.Error("Erreurs Windows : capture active", ex);
                WindowsFreezeInvestigator.Cancel();
                plan.Phase = IncidentInvestigationPhase.Failed;
                plan.Status = "La capture n'a pas pu être analysée : " + ex.Message;
            }

            Render();
        }

        private async Task ExecuteRepairPlanAsync(Incident incident)
        {
            IncidentRepairPlan? plan = incident.Repair;
            if (plan == null || plan.Phase == IncidentRepairPhase.Running) return;

            IncidentRepairPhase requested = plan.Phase;
            if (requested == IncidentRepairPhase.Ready)
            {
                string targets = plan.VerifiedTargets.Count > 0
                    ? "\n\nComposants concernés : " + string.Join(", ", plan.VerifiedTargets)
                    : "";
                var answer = MessageBox.Show(
                    plan.Detail + targets + "\n\nTweakly vérifiera le résultat avant de déclarer l'incident corrigé.",
                    "Tweakly — Confirmer la correction",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK) return;
            }

            plan.Phase = IncidentRepairPhase.Running;
            plan.Status = requested == IncidentRepairPhase.Ready
                ? "Correction en cours, puis contrôle du résultat…"
                : "Diagnostic approfondi en cours…";
            Render();

            try
            {
                IncidentRepairResult result = requested == IncidentRepairPhase.Ready
                    ? await WindowsIncidentRemediator.RepairAsync(plan)
                    : await WindowsIncidentRemediator.DiagnoseAsync(plan);

                plan.Phase = result.Phase;
                plan.Status = result.Message;
                plan.VerifiedTargets = result.VerifiedTargets;
                foreach (string evidence in result.Evidence)
                    if (!incident.Evidence.Contains(evidence, StringComparer.OrdinalIgnoreCase))
                        incident.Evidence.Add(evidence);

                if (result.Phase == IncidentRepairPhase.Corrected)
                {
                    incident.CauseState = IncidentCauseState.Established;
                    incident.Conclusion = result.Message;
                    _main.Log($"Erreurs Windows : correction validée — {incident.Title}");
                }
                else
                {
                    _main.Log($"Erreurs Windows : diagnostic — {result.Message}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Erreurs Windows : diagnostic/correction", ex);
                plan.Phase = IncidentRepairPhase.Blocked;
                plan.Status = "L'opération a été interrompue avant validation. Aucune correction n'est annoncée.";
            }

            Render();
        }

        private static string CauseRole(IncidentCauseState state) => state switch
        {
            IncidentCauseState.Established => "ThOk",
            IncidentCauseState.Probable => "ThWarn",
            _ => "ThTextDim",
        };

        private static string RepairRole(IncidentRepairPhase phase) => phase switch
        {
            IncidentRepairPhase.Corrected or IncidentRepairPhase.NotPresent => "ThOk",
            IncidentRepairPhase.Blocked => "ThWarn",
            _ => "ThAccentIcon",
        };

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
                        OpenTrustedUrl(act.Target);
                        break;

                    case LogActionKind.Diag:
                        OpenTrustedDiagnostic(act.Target);
                        break;

                    case LogActionKind.Command:
                        RunTrustedCommand(act.Target);
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

        private static void OpenTrustedUrl(string target)
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !IsTrustedSupportHost(uri.Host))
                throw new InvalidOperationException("Lien externe non autorisé.");

            using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }

        private static bool IsTrustedSupportHost(string host)
        {
            string[] suffixes =
            [
                "microsoft.com", "intel.fr", "intel.com", "nvidia.fr", "nvidia.com",
                "amd.com", "memtest86.com", "wagnardsoft.com", "nirsoft.net"
            ];
            return suffixes.Any(suffix =>
                host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
        }

        private static void OpenTrustedDiagnostic(string target)
        {
            string[] allowed = ["services.msc", "devmgmt.msc", "appwiz.cpl", "timedate.cpl", "inetcpl.cpl"];
            if (!allowed.Contains(target, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Outil de diagnostic non autorisé.");

            string path = System.IO.Path.Combine(Environment.SystemDirectory, target);
            if (!System.IO.File.Exists(path))
                throw new System.IO.FileNotFoundException("Outil Windows introuvable.", path);
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        private static void RunTrustedCommand(string target)
        {
            string normalized = (target ?? "").Trim();
            string executable;
            string arguments;

            if (normalized.Equals("cmd /k chkdsk C: /f", StringComparison.OrdinalIgnoreCase))
            {
                executable = System.IO.Path.Combine(Environment.SystemDirectory, "chkdsk.exe");
                arguments = "C: /f";
            }
            else if (normalized.Equals("cmd /c SystemPropertiesProtection.exe", StringComparison.OrdinalIgnoreCase))
            {
                executable = System.IO.Path.Combine(Environment.SystemDirectory, "SystemPropertiesProtection.exe");
                arguments = "";
            }
            else if (normalized.Equals("cmd /c taskmgr", StringComparison.OrdinalIgnoreCase))
            {
                executable = System.IO.Path.Combine(Environment.SystemDirectory, "Taskmgr.exe");
                arguments = "";
            }
            else if (normalized.Equals("cmd /c SystemPropertiesPerformance.exe", StringComparison.OrdinalIgnoreCase))
            {
                executable = System.IO.Path.Combine(Environment.SystemDirectory, "SystemPropertiesPerformance.exe");
                arguments = "";
            }
            else if (normalized.Equals("cmd /c ipconfig /flushdns && pause", StringComparison.OrdinalIgnoreCase))
            {
                executable = System.IO.Path.Combine(Environment.SystemDirectory, "ipconfig.exe");
                arguments = "/flushdns";
            }
            else if (normalized.Equals("cmd /k w32tm /resync", StringComparison.OrdinalIgnoreCase))
            {
                executable = System.IO.Path.Combine(Environment.SystemDirectory, "w32tm.exe");
                arguments = "/resync";
            }
            else if (normalized.StartsWith("explorer ", StringComparison.OrdinalIgnoreCase))
            {
                executable = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                arguments = normalized["explorer ".Length..];
            }
            else
            {
                throw new InvalidOperationException("Commande non autorisée.");
            }

            if (!System.IO.File.Exists(executable))
                throw new System.IO.FileNotFoundException("Outil Windows introuvable.", executable);
            using var _ = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true });
        }
    }
}
