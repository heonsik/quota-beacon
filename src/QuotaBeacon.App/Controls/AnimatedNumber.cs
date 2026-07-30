using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace QuotaBeacon.App.Controls;

/// <summary>
/// A number that counts to its new value instead of snapping.
/// </summary>
/// <remarks>
/// The hero figure is the one thing the user actually reads, so it moves on the same curve as its
/// gauge; a snapping number beside a sliding bar looks like a bug. When <see cref="Value"/> is
/// <c>null</c> the control shows <see cref="EmptyText"/>, which is how a spend meter with no
/// denominator avoids displaying a percentage it does not have.
/// </remarks>
public sealed class AnimatedNumber : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double?),
        typeof(AnimatedNumber),
        new PropertyMetadata(null, OnValueChanged));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format),
        typeof(string),
        typeof(AnimatedNumber),
        new PropertyMetadata("0", OnFormatChanged));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText),
        typeof(string),
        typeof(AnimatedNumber),
        new PropertyMetadata("—", OnFormatChanged));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue),
        typeof(double),
        typeof(AnimatedNumber),
        new PropertyMetadata(0d, OnDisplayValueChanged));

    /// <summary>The target value, or <c>null</c> to show <see cref="EmptyText"/>.</summary>
    public double? Value
    {
        get => (double?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>A standard or custom numeric format string, applied with the invariant culture.</summary>
    public string Format
    {
        get => (string)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    private double DisplayValue => (double)GetValue(DisplayValueProperty);

    private static void OnValueChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        var number = (AnimatedNumber)element;
        var target = (double?)args.NewValue;

        if (target is null)
        {
            number.BeginAnimation(DisplayValueProperty, null);
            number.Text = number.EmptyText;
            return;
        }

        if (!Motion.ShouldAnimate || args.OldValue is null)
        {
            number.BeginAnimation(DisplayValueProperty, null);
            number.SetValue(DisplayValueProperty, target.Value);
            return;
        }

        number.BeginAnimation(DisplayValueProperty, Motion.To(target.Value, Motion.Value));
    }

    private static void OnDisplayValueChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        var number = (AnimatedNumber)element;

        if (number.Value is not null)
        {
            number.Text = ((double)args.NewValue).ToString(number.Format, CultureInfo.InvariantCulture);
        }
    }

    private static void OnFormatChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        var number = (AnimatedNumber)element;

        number.Text = number.Value is null
            ? number.EmptyText
            : ((double)number.GetValue(DisplayValueProperty)).ToString(
                number.Format,
                CultureInfo.InvariantCulture);
    }
}
