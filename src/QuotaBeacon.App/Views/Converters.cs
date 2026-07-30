using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QuotaBeacon.App.Views;

/// <summary>Collapses an element when a flag is <c>true</c>.</summary>
public sealed class NegatedBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Collapses an element when its text is empty.
/// </summary>
/// <remarks>
/// Lets the view models express "nothing to say here" as an empty string instead of carrying a
/// parallel visibility flag for every optional line on the card.
/// </remarks>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
