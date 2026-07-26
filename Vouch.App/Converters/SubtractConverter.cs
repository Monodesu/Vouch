using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Vouch.App.Converters;

/// <summary>Returns <c>value - parameter</c> (clamped at 0). Used to cap a dialog's height to the
/// window's, leaving a margin — e.g. MaxHeight = container height − 72.</summary>
public class SubtractConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value is double d ? d : 0;
        double p = parameter switch
        {
            double pd => pd,
            string ps when double.TryParse(ps, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) => r,
            _ => 0,
        };
        return Math.Max(0, v - p);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
