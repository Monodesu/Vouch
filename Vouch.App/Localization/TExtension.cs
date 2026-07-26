using System;
using Avalonia;
using Avalonia.Markup.Xaml;

namespace Vouch.App.Localization;

/// <summary>
/// <c>{loc:T Some_Key}</c> — a live-updating binding into the localization table.
/// Bound to <see cref="Loc.Observe"/> so it re-emits on every language change, independent of
/// DataContext or when the binding was created.
/// </summary>
public class TExtension : MarkupExtension
{
    public string Key { get; set; }

    public TExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Loc.I.Observe(Key).ToBinding();
}
