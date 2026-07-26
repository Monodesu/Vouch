using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Vouch.App.Converters;

/// <summary>
/// Resolves an icon resource-key string (e.g. "Ico_Key") to its StreamGeometry
/// from the application resources, so a data-bound key can drive a Path's Data.
/// </summary>
public class IconGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current is { } app
            && app.TryFindResource(key, out var geometry))
            return geometry;
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
