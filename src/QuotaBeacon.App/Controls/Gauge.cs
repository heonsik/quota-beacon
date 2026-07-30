using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaBrush = System.Windows.Media.Brush;

namespace QuotaBeacon.App.Controls;

/// <summary>
/// A horizontal quota bar that animates to its value.
/// </summary>
/// <remarks>
/// <para>
/// Drawn rather than composed from panels so the fill can be a rounded capsule that stays correct at
/// any width, and so a value change is one eased animation instead of a layout pass.
/// </para>
/// <para>
/// <see cref="Value"/> is <em>remaining</em> fraction, matching what the user reads off the card. A
/// <c>null</c> value means there is no denominator, and the control then draws nothing at all —
/// not an empty track — because an empty track reads as either zero remaining or as still loading.
/// </para>
/// </remarks>
public sealed class Gauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double?),
        typeof(Gauge),
        new PropertyMetadata(null, OnValueChanged));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(MediaBrush),
        typeof(Gauge),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track),
        typeof(MediaBrush),
        typeof(Gauge),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The animated stand-in for <see cref="Value"/>; this is what gets painted.</summary>
    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue),
        typeof(double),
        typeof(Gauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public Gauge() => Height = 8;

    /// <summary>Remaining fraction in <c>[0,1]</c>, or <c>null</c> when there is no denominator.</summary>
    public double? Value
    {
        get => (double?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public MediaBrush Fill
    {
        get => (MediaBrush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public MediaBrush Track
    {
        get => (MediaBrush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    private double DisplayValue => (double)GetValue(DisplayValueProperty);

    protected override void OnRender(DrawingContext drawingContext)
    {
        // No denominator: draw nothing, so the absence reads as information rather than as zero.
        if (Value is null)
        {
            return;
        }

        var height = RenderSize.Height;
        var width = RenderSize.Width;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = height / 2;

        drawingContext.DrawRoundedRectangle(
            Track,
            null,
            new Rect(0, 0, width, height),
            radius,
            radius);

        var fillWidth = width * Math.Clamp(DisplayValue, 0d, 1d);

        // Below one full cap the capsule geometry degenerates into a sliver that reads as a glitch,
        // so nothing is drawn until there is room for a real shape.
        if (fillWidth < height)
        {
            return;
        }

        drawingContext.DrawRoundedRectangle(
            Fill,
            null,
            new Rect(0, 0, fillWidth, height),
            radius,
            radius);
    }

    private static void OnValueChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        var gauge = (Gauge)element;
        var target = (double?)args.NewValue ?? 0d;

        if (!Motion.ShouldAnimate || args.OldValue is null)
        {
            // First paint must not animate from zero: opening the popup would look like it is loading.
            gauge.BeginAnimation(DisplayValueProperty, null);
            gauge.SetValue(DisplayValueProperty, target);
            return;
        }

        gauge.BeginAnimation(DisplayValueProperty, Motion.To(target, Motion.Value));
    }
}
