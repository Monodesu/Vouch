using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Vouch.App.Localization;

/// <summary>
/// Maps a theme <em>value</em> ("Dark"/"Light"/"System") to its localized label via the
/// <c>Theme_{value}</c> key. Used as a MultiBinding converter whose second input is
/// <see cref="Loc.Language"/>, so the label re-renders when the language changes while the
/// ComboBox keeps binding the stable value.
/// </summary>
public class ThemeLabelConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0 || values[0] is not string value)
            return null;
        return Loc.T($"Theme_{value}");
    }
}
