using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using Avalonia.Platform;

namespace Vouch.App.Localization;

/// <summary>
/// Tiny runtime localizer. String tables live in <c>Assets/i18n/{code}.json</c>; XAML binds
/// through <see cref="TExtension"/> (live-updating on language change), code calls
/// <see cref="T(string)"/>. English is the fallback for missing keys, and a missing key
/// renders as the key itself — a partial translation never breaks the UI.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    private readonly Dictionary<string, string> _en;
    private Dictionary<string, string> _current;

    /// <summary>Raised after the active language changes; drives live UI refresh.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>INPC for plain-property bindings (e.g. a converter keyed off <see cref="Language"/>).
    /// Indexer bindings do not refresh reliably via INPC — those go through <see cref="Observe"/>.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>"en" or "zh-CN".</summary>
    public string Language { get; private set; } = "en";

    private Loc()
    {
        _en = LoadTable("en") ?? new Dictionary<string, string>();
        _current = _en;
    }

    public string this[string key] =>
        _current.TryGetValue(key, out var s) ? s :
        _en.TryGetValue(key, out var e) ? e : key;

    public static string T(string key) => I[key];
    public static string T(string key, params object[] args) => string.Format(I[key], args);

    /// <summary>Switches the language at runtime; unknown codes fall back to English.</summary>
    public void SetLanguage(string code)
    {
        if (code == Language) return;
        Language = code;
        _current = code == "en" ? _en : LoadTable(code) ?? _en;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    /// <summary>
    /// A live value for one key: emits the current translation immediately, then re-emits on every
    /// language change. <see cref="TExtension"/> binds to this so bindings stay correct regardless of
    /// when they were created — sidestepping the unreliable INPC-indexer refresh path.
    /// </summary>
    public IObservable<string> Observe(string key) => new KeyObservable(this, key);

    private static Dictionary<string, string>? LoadTable(string code)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Vouch/Assets/i18n/{code}.json"));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        }
        catch (Exception) { return null; }
    }

    private sealed class KeyObservable : IObservable<string>
    {
        private readonly Loc _loc;
        private readonly string _key;
        public KeyObservable(Loc loc, string key) { _loc = loc; _key = key; }

        public IDisposable Subscribe(IObserver<string> observer)
        {
            observer.OnNext(_loc[_key]);
            EventHandler handler = (_, _) => observer.OnNext(_loc[_key]);
            _loc.LanguageChanged += handler;
            return new Unsubscriber(() => _loc.LanguageChanged -= handler);
        }

        private sealed class Unsubscriber : IDisposable
        {
            private Action? _dispose;
            public Unsubscriber(Action dispose) => _dispose = dispose;
            public void Dispose() { _dispose?.Invoke(); _dispose = null; }
        }
    }
}
