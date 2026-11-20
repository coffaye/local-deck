using System.Windows;
using System.Windows.Media;

namespace LocalDeck.Controls;

/// <summary>
/// Instrument-style segmented meter. Segments make small percentages legible
/// at a glance and give the panel its mechanical feel.
/// </summary>
public sealed class SegmentedBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SegmentedBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SegmentedBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(SegmentedBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentCountProperty = DependencyProperty.Register(
        nameof(SegmentCount), typeof(int), typeof(SegmentedBar),
        new FrameworkPropertyMetadata(24, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public int SegmentCount
    {
        get => (int)GetValue(SegmentCountProperty);
        set => SetValue(SegmentCountProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 4 || height < 2) return;

        var count = Math.Clamp(SegmentCount, 4, 80);
        var gap = Math.Min(2.0, width / count * 0.25);
        var segmentWidth = (width - gap * (count - 1)) / count;
        if (segmentWidth <= 0) return;

        var fill = Fill ?? new SolidColorBrush(Color.FromRgb(0xDB, 0xFF, 0x5A));
        var track = Track ?? new SolidColorBrush(Color.FromRgb(0x25, 0x28, 0x22));
        var value = Math.Clamp(Value, 0, 100) / 100.0 * count;

        for (var i = 0; i < count; i++)
        {
            var x = i * (segmentWidth + gap);
            var rect = new Rect(x, 0, segmentWidth, height);
            drawingContext.DrawRoundedRectangle(track, null, rect, 1, 1);

            var segmentFill = Math.Clamp(value - i, 0, 1);
            if (segmentFill <= 0) continue;

            var fillRect = new Rect(x, 0, segmentWidth * segmentFill, height);
            drawingContext.DrawRoundedRectangle(fill, null, fillRect, 1, 1);
        }
    }
}
