using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FPSHubRTX.App.Controls;

/// <summary>
/// Lightweight line/area chart drawn directly with DrawingContext (no charting dependency).
/// Draws exactly the values it is given; with no values it shows an "empty" caption instead of a fake line.
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IEnumerable<double>), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(LineChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinValueProperty = DependencyProperty.Register(nameof(MinValue), typeof(double), typeof(LineChart),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(LineChart),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(LineChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(40, 160, 160, 190)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(LineChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("No data yet", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(LineChart),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowAxisLabelsProperty = DependencyProperty.Register(nameof(ShowAxisLabels), typeof(bool), typeof(LineChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double>? Values { get => (IEnumerable<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    /// <summary>Fixed top of the scale (e.g. 100 for %). NaN = auto with headroom.</summary>
    public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }
    public double MinValue { get => (double)GetValue(MinValueProperty); set => SetValue(MinValueProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public bool ShowAxisLabels { get => (bool)GetValue(ShowAxisLabelsProperty); set => SetValue(ShowAxisLabelsProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 10 || h < 10) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var values = Values?.Where(double.IsFinite).ToList() ?? new List<double>();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labelWidth = ShowAxisLabels ? 40.0 : 0.0;
        var plot = new Rect(labelWidth, 6, Math.Max(1, w - labelWidth - 4), Math.Max(1, h - 12));

        var gridPen = new Pen(GridBrush, 1);
        gridPen.Freeze();
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + plot.Height * i / 4;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        if (values.Count < 2)
        {
            var empty = new FormattedText(EmptyText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, LabelBrush, dpi);
            dc.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
            return;
        }

        var min = MinValue;
        var max = double.IsNaN(MaxValue) ? NiceMax(values.Max()) : MaxValue;
        if (max <= min) max = min + 1;

        if (ShowAxisLabels)
        {
            for (var i = 0; i <= 4; i++)
            {
                var v = max - (max - min) * i / 4;
                var t = new FormattedText(v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture) + Unit, CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, LabelBrush, dpi);
                dc.DrawText(t, new Point(labelWidth - t.Width - 6, plot.Top + plot.Height * i / 4 - t.Height / 2));
            }
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var x = plot.Left + plot.Width * i / (values.Count - 1);
                var y = plot.Bottom - plot.Height * (Math.Clamp(values[i], min, max) - min) / (max - min);
                if (i == 0) ctx.BeginFigure(new Point(x, y), Fill is not null, false);
                else ctx.LineTo(new Point(x, y), true, true);
            }
            if (Fill is not null)
            {
                ctx.LineTo(new Point(plot.Right, plot.Bottom), false, false);
                ctx.LineTo(new Point(plot.Left, plot.Bottom), false, false);
            }
        }
        geometry.Freeze();

        if (Fill is not null) dc.DrawGeometry(Fill, null, geometry);
        var pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();

        // Stroke only the line (not the closing edge of the filled area).
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var x = plot.Left + plot.Width * i / (values.Count - 1);
                var y = plot.Bottom - plot.Height * (Math.Clamp(values[i], min, max) - min) / (max - min);
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else ctx.LineTo(new Point(x, y), true, true);
            }
        }
        line.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    private static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
        {
            if (m * exp >= v * 1.1) return m * exp;
        }
        return 10 * exp;
    }
}

/// <summary>Simple vertical bar chart (e.g. minutes played per day).</summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IEnumerable<double>), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(nameof(Labels), typeof(IEnumerable<string>), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double>? Values { get => (IEnumerable<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IEnumerable<string>? Labels { get => (IEnumerable<string>?)GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values?.ToList() ?? new List<double>();
        var labels = Labels?.ToList() ?? new List<string>();
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 10 || h < 30) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (values.Count == 0 || values.All(v => v <= 0))
        {
            var empty = new FormattedText("No playtime recorded in this period", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, LabelBrush, dpi);
            dc.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
            return;
        }

        var max = values.Max();
        var plotH = h - 20;
        var slot = w / values.Count;
        var barW = Math.Max(2, slot * 0.62);
        for (var i = 0; i < values.Count; i++)
        {
            var bh = max <= 0 ? 0 : plotH * values[i] / max;
            var x = i * slot + (slot - barW) / 2;
            if (bh > 0) dc.DrawRoundedRectangle(BarBrush, null, new Rect(x, plotH - bh, barW, bh), Math.Min(3, barW / 2), Math.Min(3, barW / 2));
            if (i < labels.Count && (i % 5 == 0 || i == values.Count - 1))
            {
                var t = new FormattedText(labels[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9.5, LabelBrush, dpi);
                dc.DrawText(t, new Point(Math.Clamp(x + barW / 2 - t.Width / 2, 0, w - t.Width), plotH + 4));
            }
        }
    }
}
