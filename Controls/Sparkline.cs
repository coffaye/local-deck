using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace LocalDeck.Controls;

/// <summary>
/// A very small, quiet sparkline. No axes, no chrome, just the shape of the last
/// few samples and a single live endpoint.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2) return;

        var values = new List<double>();
        if (Values is not null)
        {
            foreach (var item in Values)
            {
                if (item is double d && !double.IsNaN(d)) values.Add(d);
            }
        }

        if (values.Count == 0)
        {
            values.Add(0);
        }

        var stroke = Stroke ?? new SolidColorBrush(Color.FromRgb(0xDB, 0xFF, 0x5A));
        var fill = Fill ?? CreateFade(stroke);
        var top = 1.5;
        var bottom = height - 1.5;
        var usable = Math.Max(1, bottom - top);
        var step = values.Count <= 1 ? width : width / (values.Count - 1);
        var points = new Point[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var y = bottom - Math.Clamp(values[i], 0, 100) / 100.0 * usable;
            points[i] = new Point(i * step, y);
        }

        if (points.Length > 1)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(0, bottom + 1), isFilled: true, isClosed: true);
                ctx.LineTo(points[0], isStroked: false, isSmoothJoin: false);
                for (var i = 1; i < points.Length; i++)
                {
                    ctx.LineTo(points[i], isStroked: false, isSmoothJoin: false);
                }

                ctx.LineTo(new Point(width, bottom + 1), isStroked: false, isSmoothJoin: false);
            }

            area.Freeze();
            drawingContext.DrawGeometry(fill, null, area);
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
            for (var i = 1; i < points.Length; i++)
            {
                ctx.LineTo(points[i], isStroked: true, isSmoothJoin: true);
            }
        }

        line.Freeze();
        var pen = new Pen(stroke, 1.25) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, line);
        var last = points[^1];
        drawingContext.DrawEllipse(stroke, null, last, 1.8, 1.8);
    }

    private static Brush CreateFade(Brush source)
    {
        var color = source is SolidColorBrush solid ? solid.Color : Color.FromRgb(0xDB, 0xFF, 0x5A);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(45, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
            }
        };
        brush.Freeze();
        return brush;
    }
}
