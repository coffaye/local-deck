using System.Windows;
using System.Windows.Media;

namespace LocalDeck.Controls;

public partial class QuotaMeter
{
    public QuotaMeter()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(QuotaMeter), new PropertyMetadata("额度窗口"));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(QuotaMeter), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty RemainingTextProperty = DependencyProperty.Register(
        nameof(RemainingText), typeof(string), typeof(QuotaMeter), new PropertyMetadata("—"));

    public static readonly DependencyProperty RemainingProperty = DependencyProperty.Register(
        nameof(Remaining), typeof(double), typeof(QuotaMeter), new PropertyMetadata(0d));

    public static readonly DependencyProperty ResetTextProperty = DependencyProperty.Register(
        nameof(ResetText), typeof(string), typeof(QuotaMeter), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(string), typeof(QuotaMeter), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(QuotaMeter),
        new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0xDB, 0xFF, 0x5A))));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string RemainingText
    {
        get => (string)GetValue(RemainingTextProperty);
        set => SetValue(RemainingTextProperty, value);
    }

    public double Remaining
    {
        get => (double)GetValue(RemainingProperty);
        set => SetValue(RemainingProperty, value);
    }

    public string ResetText
    {
        get => (string)GetValue(ResetTextProperty);
        set => SetValue(ResetTextProperty, value);
    }

    public string Note
    {
        get => (string)GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }
}
