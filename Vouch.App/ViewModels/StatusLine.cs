using System;

namespace Vouch.App.ViewModels;

/// <summary>
/// Formats the little status-line glyphs consistently across the VMs. The glyph is a
/// language-neutral decoration; the message text comes localized (or from an exception),
/// so keeping the two separate avoids baking ✓/✗/⚠ into every translation.
/// </summary>
internal static class StatusLine
{
    public static string Ok(string message) => "✓ " + message;
    public static string Error(string message) => "✗ " + message;
    public static string Error(Exception ex) => "✗ " + ex.Message;
    public static string Warn(string message) => "⚠ " + message;
}
