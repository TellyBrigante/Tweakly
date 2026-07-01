using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class BatteryCalibrationReportWindow : Window
    {
        private readonly BatteryCalibrationSession _session;
        private readonly BatterySnapshot _snapshot;

        public BatteryCalibrationReportWindow(BatteryCalibrationSession session, BatterySnapshot snapshot)
        {
            _session = session;
            _snapshot = snapshot;
            InitializeComponent();
            Loaded += (_, _) =>
            {
                RenderReport();
                DrawChart();
            };
        }

        private void RenderReport()
        {
            var samples = _session.Samples;
            var first = samples.FirstOrDefault();
            var last = samples.LastOrDefault();
            var drainLast = samples.LastOrDefault(s => s.Phase == BatteryCalibrationPhase.Drain);

            TxtReportMeta.Text =
                $"Généré le {DateTime.Now:yyyy-MM-dd HH:mm:ss} | Session {_session.StartedAt:yyyy-MM-dd HH:mm:ss} | Batterie : {FirstNonEmpty(_session.BatteryName, _snapshot.Name, "Batterie")}";
            TxtProtocolState.Text = "Protocole : " + PhaseTitle(_session.Phase);

            TxtHealth.Text = FormatHealth(_snapshot.HealthPercent);
            TxtCapacity.Text = $"{FormatMWh(_snapshot.FullChargeCapacityMWh ?? _session.FullChargeCapacityMWh)} / {FormatMWh(_snapshot.DesignCapacityMWh ?? _session.DesignCapacityMWh)}";

            TxtLastCharge.Text = FormatPercent(last?.ChargePercent ?? _snapshot.ChargePercent);
            TxtLastDetail.Text = $"{FormatV(last?.VoltageV ?? _snapshot.VoltageV)} | {FormatW(last?.PowerW ?? _snapshot.PowerW)} | {FormatC(last?.TemperatureC ?? _snapshot.TemperatureC)}";

            TxtDrainCut.Text = FormatPercent(drainLast?.ChargePercent);
            TxtDrainDetail.Text = drainLast == null
                ? "Aucun point de drain enregistré"
                : $"{drainLast.Timestamp:HH:mm:ss} | {FormatV(drainLast.VoltageV)} | {FormatMWh(drainLast.RemainingCapacityMWh)}";

            TxtSampleCount.Text = $"{samples.Count} point(s)";
            TxtSource.Text = $"Source : {FirstNonEmpty(_snapshot.Source, last?.Source ?? "--")}";

            RenderProtocolPanel(first, last, drainLast);
            RenderKeyRows();
        }

        private void RenderProtocolPanel(BatteryCalibrationSample? first, BatteryCalibrationSample? last, BatteryCalibrationSample? drainLast)
        {
            ProtocolPanel.Children.Clear();
            AddProtocolLine("Équilibrage 2 h continu", !_session.BalanceInterrupted, _session.BalanceInterrupted ? "Secteur retiré au moins une fois." : "Aucune coupure secteur notée.");
            AddProtocolLine("Recharge finale continue", !_session.RechargeInterrupted, _session.RechargeInterrupted ? "Secteur retiré avant 100 %." : "Aucune coupure secteur notée.");
            AddProtocolLine("Action critique Windows restaurée", !_session.PowerPlanGuardApplied, _session.PowerPlanGuardApplied ? "Restauration encore en attente." : "Plan d'alimentation revenu à l'état initial.");
            AddProtocolLine("Dernier drain connu", drainLast != null, drainLast == null ? "Aucun point de drain." : $"{FormatPercent(drainLast.ChargePercent)} à {drainLast.Timestamp:HH:mm:ss}.");
            AddProtocolLine("Durée totale mesurée", last != null && first != null, first != null && last != null ? FormatDuration(last.Timestamp - first.Timestamp) : "--");

            if (!string.IsNullOrWhiteSpace(_session.PowerPlanGuardError))
                AddProtocolLine("Powercfg", false, _session.PowerPlanGuardError);
        }

        private void AddProtocolLine(string title, bool ok, string detail)
        {
            var border = new Border
            {
                Background = Brush(ok ? "ThOkTint" : "ThWarnTint"),
                BorderBrush = Brush(ok ? "ThOkBorderTint" : "ThWarn"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brush("ThTextBody"),
                FontFamily = AppFont(),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = Brush("ThTextDim"),
                FontFamily = AppFont(),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
            border.Child = panel;
            ProtocolPanel.Children.Add(border);
        }

        private void RenderKeyRows()
        {
            KeyRowsPanel.Children.Clear();
            AddKeyHeader();

            foreach (var phase in new[]
            {
                BatteryCalibrationPhase.ChargeToFull,
                BatteryCalibrationPhase.CellBalance,
                BatteryCalibrationPhase.Drain,
                BatteryCalibrationPhase.Rest,
                BatteryCalibrationPhase.Recharge
            })
            {
                var points = _session.Samples.Where(s => s.Phase == phase).ToList();
                if (points.Count == 0)
                {
                    AddKeyRow(PhaseTitle(phase), "aucun point", null);
                    continue;
                }

                AddKeyRow(PhaseTitle(phase), "début", points[0]);
                if (points.Count > 1)
                    AddKeyRow(PhaseTitle(phase), "fin", points[^1]);
            }
        }

        private void AddKeyHeader()
        {
            var row = CreateKeyRow();
            AddCell(row, "Phase", true);
            AddCell(row, "Point", true);
            AddCell(row, "Heure", true);
            AddCell(row, "%", true);
            AddCell(row, "V", true);
            AddCell(row, "W", true);
            AddCell(row, "°C", true);
            AddCell(row, "mWh", true);
            AddCell(row, "Secteur", true);
            KeyRowsPanel.Children.Add(row);
        }

        private void AddKeyRow(string phase, string point, BatteryCalibrationSample? sample)
        {
            var row = CreateKeyRow();
            AddCell(row, phase, false);
            AddCell(row, point, false);
            AddCell(row, sample?.Timestamp.ToString("HH:mm:ss") ?? "--", false);
            AddCell(row, FormatPercent(sample?.ChargePercent), false);
            AddCell(row, FormatV(sample?.VoltageV), false);
            AddCell(row, FormatW(sample?.PowerW), false);
            AddCell(row, FormatC(sample?.TemperatureC), false);
            AddCell(row, FormatMWh(sample?.RemainingCapacityMWh), false);
            AddCell(row, AcText(sample?.OnAcPower), false);
            KeyRowsPanel.Children.Add(row);
        }

        private Grid CreateKeyRow()
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.55, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
            return row;
        }

        private void AddCell(Grid row, string text, bool header)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = Brush(header ? "ThTextTitle" : "ThTextBody"),
                FontFamily = AppFont(),
                FontSize = header ? 11 : 10.5,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(tb, row.Children.Count);
            row.Children.Add(tb);
        }

        private void DrawChart()
        {
            if (ChartCanvas.ActualWidth <= 0) return;

            ChartCanvas.Children.Clear();
            var samples = _session.Samples;
            if (samples.Count == 0)
            {
                AddChartText("Aucun point enregistré.", 16, 16, "ThTextDim");
                return;
            }

            double width = ChartCanvas.ActualWidth;
            double height = ChartCanvas.ActualHeight > 0 ? ChartCanvas.ActualHeight : ChartCanvas.Height;
            double left = 44;
            double right = 20;
            double top = 18;
            double bottom = 32;
            double plotW = Math.Max(20, width - left - right);
            double plotH = Math.Max(20, height - top - bottom);

            DrawGrid(left, top, plotW, plotH);

            var minTime = samples.Min(s => s.Timestamp);
            var maxTime = samples.Max(s => s.Timestamp);
            if (maxTime <= minTime) maxTime = minTime.AddSeconds(1);

            double X(DateTime t) => left + (t - minTime).TotalSeconds / (maxTime - minTime).TotalSeconds * plotW;
            double YPercent(int p) => top + (100 - Math.Clamp(p, 0, 100)) / 100.0 * plotH;

            var volts = samples.Where(s => s.VoltageV.HasValue).Select(s => s.VoltageV!.Value).ToList();
            double minV = volts.Count > 0 ? volts.Min() : 0;
            double maxV = volts.Count > 0 ? volts.Max() : 1;
            if (maxV - minV < 0.2) { minV -= 0.1; maxV += 0.1; }
            double YVolt(double v) => top + (maxV - v) / (maxV - minV) * plotH;

            var powers = samples.Where(s => s.PowerW.HasValue).Select(s => Math.Abs(s.PowerW!.Value)).ToList();
            double maxW = Math.Max(1, powers.Count > 0 ? powers.Max() : 1);
            double YPower(double w) => top + (1 - Math.Abs(w) / maxW) * plotH;

            DrawPolyline(samples.Where(s => s.ChargePercent.HasValue).Select(s => new Point(X(s.Timestamp), YPercent(s.ChargePercent!.Value))), "ThAccentIcon", 2.3);
            DrawPolyline(samples.Where(s => s.VoltageV.HasValue).Select(s => new Point(X(s.Timestamp), YVolt(s.VoltageV!.Value))), "ThWarn", 1.9);
            DrawPolyline(samples.Where(s => s.PowerW.HasValue).Select(s => new Point(X(s.Timestamp), YPower(s.PowerW!.Value))), "ThCyan", 1.6);

            AddChartText("100 %", 0, top - 5, "ThTextDim");
            AddChartText("0 %", 10, top + plotH - 9, "ThTextDim");
            AddChartText($"{minTime:HH:mm} -> {maxTime:HH:mm}", left, top + plotH + 12, "ThTextDim");
            if (volts.Count > 0)
                AddChartText($"{maxV:0.00} V / {minV:0.00} V", left + 6, top + 5, "ThWarn");
            if (powers.Count > 0)
                AddChartText($"max {maxW:0.0} W", left + 6, top + 21, "ThCyan");
        }

        private void DrawGrid(double left, double top, double width, double height)
        {
            for (int i = 0; i <= 4; i++)
            {
                double y = top + height * i / 4.0;
                ChartCanvas.Children.Add(new Line
                {
                    X1 = left,
                    X2 = left + width,
                    Y1 = y,
                    Y2 = y,
                    Stroke = Brush("ThBorder"),
                    StrokeThickness = 1,
                    Opacity = 0.55
                });
            }
        }

        private void DrawPolyline(IEnumerable<Point> points, string role, double thickness)
        {
            var list = points.ToList();
            if (list.Count == 0) return;

            var line = new Polyline
            {
                Stroke = Brush(role),
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            foreach (var p in list) line.Points.Add(p);
            ChartCanvas.Children.Add(line);
        }

        private void AddChartText(string text, double x, double y, string role)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = Brush(role),
                FontFamily = AppFont(),
                FontSize = 10.5
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            ChartCanvas.Children.Add(tb);
        }

        private void BtnExportPng_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Exporter le rapport calibrage batterie",
                Filter = "Image PNG (*.png)|*.png",
                FileName = $"Tweakly-Rapport-Batterie-{DateTime.Now:yyyyMMdd-HHmmss}.png"
            };
            if (dlg.ShowDialog(this) != true) return;

            ExportReportPng(dlg.FileName);
        }

        private void ExportReportPng(string path)
        {
            ReportSurface.UpdateLayout();

            var size = new Size(ReportSurface.ActualWidth, ReportSurface.ActualHeight);
            if (size.Width <= 0 || size.Height <= 0) return;

            ReportSurface.Measure(size);
            ReportSurface.Arrange(new Rect(size));
            ReportSurface.UpdateLayout();

            var rtb = new RenderTargetBitmap(
                (int)Math.Ceiling(size.Width),
                (int)Math.Ceiling(size.Height),
                96,
                96,
                PixelFormats.Pbgra32);
            rtb.Render(ReportSurface);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            using var fs = File.Create(path);
            encoder.Save(fs);
        }

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private string PhaseTitle(BatteryCalibrationPhase phase) => phase switch
        {
            BatteryCalibrationPhase.ChargeToFull => "Charge complète",
            BatteryCalibrationPhase.CellBalance => "Équilibrage cellules",
            BatteryCalibrationPhase.Drain => "Drain contrôlé",
            BatteryCalibrationPhase.Rest => "Repos total",
            BatteryCalibrationPhase.Recharge => "Recharge complète",
            BatteryCalibrationPhase.Complete => "Calibrage terminé",
            _ => "Prêt"
        };

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes:00} min";
            return $"{Math.Max(0, (int)t.TotalMinutes)} min {t.Seconds:00} s";
        }

        private static string FormatPercent(int? value) => value.HasValue ? $"{value.Value} %" : "-- %";
        private static string FormatV(double? value) => value.HasValue ? $"{value.Value:0.000} V" : "-- V";
        private static string FormatW(double? value) => value.HasValue ? $"{value.Value:0.0} W" : "-- W";
        private static string FormatC(double? value) => value.HasValue ? $"{value.Value:0.0} °C" : "-- °C";
        private static string FormatMWh(int? value) => value.HasValue ? $"{value.Value:N0} mWh" : "-- mWh";
        private static string FormatHealth(double? value) => value.HasValue ? $"{value.Value:0} %" : "-- %";
        private static string AcText(bool? onAc) => onAc == true ? "branché" : onAc == false ? "batterie" : "inconnu";
        private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

        private static SolidColorBrush Brush(string role)
        {
            if (Application.Current.Resources[role] is SolidColorBrush brush) return brush;
            return ThemeManager.Brush(role);
        }

        private static FontFamily AppFont()
            => Application.Current.Resources["AppFont"] is FontFamily font ? font : new FontFamily("Segoe UI");
    }
}
