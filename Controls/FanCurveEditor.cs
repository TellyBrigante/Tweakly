using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FanControl.Core;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Controls;

public sealed class FanCurveEditor : FrameworkElement
{
    public static readonly DependencyProperty TrackBrushProperty = RegisterBrush(nameof(TrackBrush), SystemColors.ControlDarkBrush);
    public static readonly DependencyProperty BorderBrushProperty = RegisterBrush(nameof(BorderBrush), SystemColors.ControlDarkBrush);
    public static readonly DependencyProperty GridBrushProperty = RegisterBrush(nameof(GridBrush), SystemColors.GrayTextBrush);
    public static readonly DependencyProperty TextBrushProperty = RegisterBrush(nameof(TextBrush), SystemColors.GrayTextBrush);
    public static readonly DependencyProperty LineBrushProperty = RegisterBrush(nameof(LineBrush), SystemColors.HighlightBrush);
    public static readonly DependencyProperty PointBrushProperty = RegisterBrush(nameof(PointBrush), SystemColors.HighlightBrush);
    public static readonly DependencyProperty LivePointBrushProperty = RegisterBrush(nameof(LivePointBrush), Brushes.Gold);
    public static readonly DependencyProperty PanelBrushProperty = RegisterBrush(nameof(PanelBrush), SystemColors.ControlBrush);

    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve),
        typeof(IList<FanCurvePoint>),
        typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumDutyProperty = DependencyProperty.Register(
        nameof(MinimumDuty),
        typeof(double),
        typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentTemperatureProperty = DependencyProperty.Register(
        nameof(CurrentTemperature),
        typeof(double),
        typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentDutyProperty = DependencyProperty.Register(
        nameof(CurrentDuty),
        typeof(double),
        typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double MinimumTemperatureC = 20;
    private const double MaximumTemperatureC = 100;
    private const double LeftInset = 42;
    private const double RightInset = 18;
    private const double TopInset = 16;
    private const double BottomInset = 28;
    private int _draggedPoint = -1;

    public FanCurveEditor()
    {
        MinWidth = 260;
        MinHeight = 170;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;

        RefreshThemeVisuals();
    }

    public void RefreshThemeVisuals()
    {
        TrackBrush = ThemeManager.Brush("ThTrack");
        BorderBrush = ThemeManager.Brush("ThBorder");
        GridBrush = ThemeManager.Brush("ThTextDim");
        TextBrush = ThemeManager.Brush("ThTextDim");
        LineBrush = ThemeManager.Brush("ThBlueLine");
        PointBrush = ThemeManager.Brush("ThAccentIcon");
        LivePointBrush = ThemeManager.Brush("ThWarn");
        PanelBrush = ThemeManager.Brush("ThInfoTint");
        InvalidateVisual();
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush BorderBrush
    {
        get => (Brush)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush PointBrush
    {
        get => (Brush)GetValue(PointBrushProperty);
        set => SetValue(PointBrushProperty, value);
    }

    public Brush LivePointBrush
    {
        get => (Brush)GetValue(LivePointBrushProperty);
        set => SetValue(LivePointBrushProperty, value);
    }

    public Brush PanelBrush
    {
        get => (Brush)GetValue(PanelBrushProperty);
        set => SetValue(PanelBrushProperty, value);
    }

    public IList<FanCurvePoint>? Curve
    {
        get => (IList<FanCurvePoint>?)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public double MinimumDuty
    {
        get => (double)GetValue(MinimumDutyProperty);
        set => SetValue(MinimumDutyProperty, value);
    }

    public double CurrentTemperature
    {
        get => (double)GetValue(CurrentTemperatureProperty);
        set => SetValue(CurrentTemperatureProperty, value);
    }

    public double CurrentDuty
    {
        get => (double)GetValue(CurrentDutyProperty);
        set => SetValue(CurrentDutyProperty, value);
    }

    public event EventHandler? CurveChanged;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Rect plot = PlotBounds();
        Brush track = TrackBrush;
        Brush border = BorderBrush;
        Brush grid = GridBrush.CloneCurrentValue();
        grid.Opacity = 0.32;
        Brush text = TextBrush;
        Brush line = LineBrush;
        Brush point = PointBrush;
        Brush livePoint = LivePointBrush;
        Brush panel = PanelBrush;

        drawingContext.DrawRoundedRectangle(panel, new Pen(border, 1), new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)), 7, 7);
        for (int i = 0; i <= 4; i++)
        {
            double x = plot.Left + (plot.Width * i / 4);
            double y = plot.Top + (plot.Height * i / 4);
            drawingContext.DrawLine(new Pen(grid, 0.9), new Point(x, plot.Top), new Point(x, plot.Bottom));
            drawingContext.DrawLine(new Pen(grid, 0.9), new Point(plot.Left, y), new Point(plot.Right, y));
        }

        DrawLabel(drawingContext, "100 %", text, 6, plot.Top - 6);
        DrawLabel(drawingContext, "0 %", text, 13, plot.Bottom - 7);
        DrawLabel(drawingContext, "20 \u00b0C", text, plot.Left - 4, plot.Bottom + 7);
        DrawLabel(drawingContext, "100 \u00b0C", text, plot.Right - 34, plot.Bottom + 7);

        if (Curve is not { Count: >= 2 })
        {
            drawingContext.DrawLine(new Pen(track, 2), new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
            return;
        }

        Point[] points = Curve.Select(ToPoint).ToArray();
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(line, 2.7), geometry);

        foreach (Point curvePoint in points)
            drawingContext.DrawEllipse(point, new Pen(panel, 2), curvePoint, 6, 6);

        DrawPointLabels(drawingContext, points, text, panel, border);


        if (double.IsFinite(CurrentTemperature) && double.IsFinite(CurrentDuty))
        {
            Point live = ToPoint(new FanCurvePoint(CurrentTemperature, CurrentDuty));
            drawingContext.DrawEllipse(panel, null, live, 8, 8);
            drawingContext.DrawEllipse(livePoint, new Pen(panel, 2), live, 5.5, 5.5);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Curve is not { Count: >= 2 }) return;

        Point mouse = e.GetPosition(this);
        _draggedPoint = Enumerable.Range(0, Curve.Count - 1)
            .Select(index => (Index: index, Distance: (ToPoint(Curve[index]) - mouse).Length))
            .Where(static item => item.Distance <= 14)
            .OrderBy(static item => item.Distance)
            .Select(static item => item.Index)
            .DefaultIfEmpty(-1)
            .First();
        if (_draggedPoint >= 0)
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggedPoint < 0 || e.LeftButton != MouseButtonState.Pressed || Curve is null) return;

        Rect plot = PlotBounds();
        Point mouse = e.GetPosition(this);
        double temperature = MinimumTemperatureC + ((mouse.X - plot.Left) / plot.Width * (MaximumTemperatureC - MinimumTemperatureC));
        double duty = 100 - ((mouse.Y - plot.Top) / plot.Height * 100);

        double minimumTemperature = _draggedPoint == 0 ? 25 : Curve[_draggedPoint - 1].TemperatureC + 2;
        double maximumTemperature = Curve[_draggedPoint + 1].TemperatureC - 2;
        double minimumDuty = _draggedPoint == 0 ? MinimumDuty : Curve[_draggedPoint - 1].DutyPercent;
        double maximumDuty = Curve[_draggedPoint + 1].DutyPercent;
        Curve[_draggedPoint] = new FanCurvePoint(
            Math.Round(Math.Clamp(temperature, minimumTemperature, maximumTemperature)),
            Math.Round(Math.Clamp(duty, minimumDuty, maximumDuty)));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_draggedPoint < 0) return;
        _draggedPoint = -1;
        ReleaseMouseCapture();
        CurveChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private Rect PlotBounds() => new(
        LeftInset,
        TopInset,
        Math.Max(1, ActualWidth - LeftInset - RightInset),
        Math.Max(1, ActualHeight - TopInset - BottomInset));

    private Point ToPoint(FanCurvePoint point)
    {
        Rect plot = PlotBounds();
        double x = plot.Left + ((Math.Clamp(point.TemperatureC, MinimumTemperatureC, MaximumTemperatureC) - MinimumTemperatureC) /
                                (MaximumTemperatureC - MinimumTemperatureC) * plot.Width);
        double y = plot.Bottom - (Math.Clamp(point.DutyPercent, 0, 100) / 100 * plot.Height);
        return new Point(x, y);
    }

    private static DependencyProperty RegisterBrush(string name, Brush defaultValue) =>
        DependencyProperty.Register(
            name,
            typeof(Brush),
            typeof(FanCurveEditor),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private void DrawPointLabels(
        DrawingContext context,
        IReadOnlyList<Point> points,
        Brush textBrush,
        Brush background,
        Brush border)
    {
        if (Curve is null) return;
        var occupied = new List<Rect>();
        for (int index = 0; index < points.Count; index++)
        {
            FanCurvePoint value = Curve[index];
            FormattedText label = CreateLabel($"{value.TemperatureC:0} °C  ·  {value.DutyPercent:0} %", textBrush);
            Point point = points[index];
            Rect leftAbove = ClampLabel(new Rect(
                point.X - label.Width - 9, point.Y - label.Height - 6,
                label.Width, label.Height));
            Rect rightBelow = ClampLabel(new Rect(
                point.X + 9, point.Y + 6,
                label.Width, label.Height));
            Rect leftBelow = ClampLabel(new Rect(
                point.X - label.Width - 9, point.Y + 6,
                label.Width, label.Height));
            Rect rightAbove = ClampLabel(new Rect(
                point.X + 9, point.Y - label.Height - 6,
                label.Width, label.Height));
            Rect[] candidates = index % 2 == 0
                ? [leftAbove, leftBelow, rightAbove, rightBelow]
                : [rightBelow, rightAbove, leftBelow, leftAbove];
            Rect selected = candidates.FirstOrDefault(candidate =>
                occupied.All(existing => !Inflate(existing, 3).IntersectsWith(candidate)));
            if (selected.IsEmpty)
                selected = FindFreeLabelRow(label, occupied, index);

            Point labelAnchor = new(
                point.X < selected.Left ? selected.Left : point.X > selected.Right ? selected.Right : point.X,
                point.Y < selected.Top ? selected.Top : point.Y > selected.Bottom ? selected.Bottom : point.Y);
            context.DrawLine(new Pen(border, 0.7), point, labelAnchor);
            context.DrawRoundedRectangle(background, new Pen(border, 0.6), Inflate(selected, 2), 3, 3);
            context.DrawText(label, selected.TopLeft);
            occupied.Add(selected);
        }
    }

    private Rect FindFreeLabelRow(FormattedText label, IReadOnlyList<Rect> occupied, int index)
    {
        Rect plot = PlotBounds();
        for (int row = 0; row < 4; row++)
        {
            double y = 2 + (row * (label.Height + 3));
            double x = Math.Clamp(plot.Left + (index * (plot.Width - label.Width) / Math.Max(1, Curve!.Count - 1)),
                plot.Left, plot.Right - label.Width);
            Rect candidate = new(x, y, label.Width, label.Height);
            if (occupied.All(existing => !Inflate(existing, 3).IntersectsWith(candidate)))
                return candidate;
        }
        return ClampLabel(new Rect(plot.Left, 2, label.Width, label.Height));
    }

    private Rect ClampLabel(Rect label)
    {
        Rect plot = PlotBounds();
        double x = Math.Clamp(label.X, plot.Left, Math.Max(plot.Left, plot.Right - label.Width));
        double y = Math.Clamp(label.Y, 2, Math.Max(2, ActualHeight - label.Height - 2));
        return new Rect(x, y, label.Width, label.Height);
    }

    private static Rect Inflate(Rect rect, double amount)
    {
        rect.Inflate(amount, amount);
        return rect;
    }

    private FormattedText CreateLabel(string value, Brush brush) => new(
        value,
        CultureInfo.GetCultureInfo("fr-FR"),
        FlowDirection.LeftToRight,
        new Typeface("Segoe UI"),
        10.2,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawLabel(DrawingContext context, string value, Brush brush, double x, double y)
    {
        FormattedText text = CreateLabel(value, brush);
        context.DrawText(text, new Point(x, y));
    }
}
