using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Optimisation_Tool.Pages
{
    public partial class PageBatteryCalibration
    {
        private void DrawGraph()
        {
            if (GraphCanvas.ActualWidth <= 0) return;

            GraphCanvas.Children.Clear();
            var samples = _session.Samples;
            if (samples.Count == 0)
            {
                AddGraphText("Aucun point enregistré.", 16, 16, "ThTextDim");
                return;
            }

            double width = GraphCanvas.ActualWidth;
            double height = GraphCanvas.ActualHeight > 0 ? GraphCanvas.ActualHeight : GraphCanvas.Height;
            double left = 42;
            double right = 18;
            double top = 18;
            double bottom = 30;
            double plotWidth = Math.Max(20, width - left - right);
            double plotHeight = Math.Max(20, height - top - bottom);

            DrawGrid(left, top, plotWidth, plotHeight);

            var minTime = samples.Min(sample => sample.Timestamp);
            var maxTime = samples.Max(sample => sample.Timestamp);
            if (maxTime <= minTime) maxTime = minTime.AddSeconds(1);

            double X(DateTime time) => left + (time - minTime).TotalSeconds / (maxTime - minTime).TotalSeconds * plotWidth;
            double YPercent(int percent) => top + (100 - Math.Clamp(percent, 0, 100)) / 100.0 * plotHeight;

            var volts = samples.Where(sample => sample.VoltageV.HasValue).Select(sample => sample.VoltageV!.Value).ToList();
            double minVolts = volts.Count > 0 ? volts.Min() : 0;
            double maxVolts = volts.Count > 0 ? volts.Max() : 1;
            if (maxVolts - minVolts < 0.2) { minVolts -= 0.1; maxVolts += 0.1; }
            double YVoltage(double voltsValue) => top + (maxVolts - voltsValue) / (maxVolts - minVolts) * plotHeight;

            var powers = samples.Where(sample => sample.PowerW.HasValue).Select(sample => Math.Abs(sample.PowerW!.Value)).ToList();
            double maxWatts = Math.Max(1, powers.Count > 0 ? powers.Max() : 1);
            double YPower(double watts) => top + (1 - Math.Abs(watts) / maxWatts) * plotHeight;

            DrawPolyline(samples.Where(sample => sample.ChargePercent.HasValue).Select(sample => new Point(X(sample.Timestamp), YPercent(sample.ChargePercent!.Value))), "ThAccentIcon", 2.2);
            DrawPolyline(samples.Where(sample => sample.VoltageV.HasValue).Select(sample => new Point(X(sample.Timestamp), YVoltage(sample.VoltageV!.Value))), "ThWarn", 1.8);
            DrawPolyline(samples.Where(sample => sample.PowerW.HasValue).Select(sample => new Point(X(sample.Timestamp), YPower(sample.PowerW!.Value))), "ThCyan", 1.5);

            AddGraphText("100 %", 0, top - 4, "ThTextDim");
            AddGraphText("0 %", 8, top + plotHeight - 8, "ThTextDim");
            if (volts.Count > 0)
                AddGraphText($"{maxVolts:0.00} V / {minVolts:0.00} V", left + 4, top + 4, "ThWarn");
        }

        private void DrawGrid(double left, double top, double width, double height)
        {
            var gridBrush = Brush("ThBorder");
            for (int i = 0; i <= 4; i++)
            {
                double y = top + height * i / 4.0;
                GraphCanvas.Children.Add(new Line
                {
                    X1 = left,
                    X2 = left + width,
                    Y1 = y,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1,
                    Opacity = 0.55
                });
            }
        }

        private void DrawPolyline(IEnumerable<Point> points, string role, double thickness)
        {
            var list = points.ToList();
            if (list.Count == 0) return;
            if (list.Count == 1)
            {
                var dot = new Ellipse { Width = 7, Height = 7, Fill = Brush(role) };
                Canvas.SetLeft(dot, list[0].X - 3.5);
                Canvas.SetTop(dot, list[0].Y - 3.5);
                GraphCanvas.Children.Add(dot);
                return;
            }

            var line = new Polyline
            {
                Stroke = Brush(role),
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            foreach (var point in list) line.Points.Add(point);
            GraphCanvas.Children.Add(line);
        }

        private void AddGraphText(string text, double x, double y, string role)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = Brush(role),
                FontFamily = (FontFamily)Application.Current.Resources["AppFont"],
                FontSize = 10.5
            };
            Canvas.SetLeft(textBlock, x);
            Canvas.SetTop(textBlock, y);
            GraphCanvas.Children.Add(textBlock);
        }

        private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();
    }
}
