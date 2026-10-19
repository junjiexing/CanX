// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Avalonia.Input;

namespace CanX.App;

public static class MpvInputKeyMapper
{
    public static string? Map(Key key, string? keySymbol, KeyModifiers modifiers)
    {
        var specialName = GetSpecialName(key);
        if (specialName is not null)
        {
            return AddModifiers(specialName, modifiers, includeShift: true);
        }

        var symbol = GetSingleSymbol(keySymbol) ?? GetFallbackSymbol(key, modifiers);
        if (symbol is null)
        {
            return null;
        }

        var name = symbol switch
        {
            " " => "SPACE",
            "#" => "SHARP",
            _ => symbol,
        };
        return AddModifiers(name, modifiers, includeShift: false);
    }

    public static string AddModifiers(string key, KeyModifiers modifiers, bool includeShift = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var parts = new List<string>(5);
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (includeShift && modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Meta");
        }

        parts.Add(key);
        return string.Join('+', parts);
    }

    private static string? GetSpecialName(Key key)
    {
        if (key == Key.Return || key == Key.Enter)
        {
            return "ENTER";
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return $"F{(int)key - (int)Key.F1 + 1}";
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return $"KP{(int)key - (int)Key.NumPad0}";
        }

        return key switch
        {
            Key.Space => "SPACE",
            Key.Tab => "TAB",
            Key.Back => "BS",
            Key.Delete => "DEL",
            Key.Insert => "INS",
            Key.Home => "HOME",
            Key.End => "END",
            Key.PageUp => "PGUP",
            Key.PageDown => "PGDWN",
            Key.Escape => "ESC",
            Key.Print or Key.PrintScreen => "PRINT",
            Key.Right => "RIGHT",
            Key.Left => "LEFT",
            Key.Down => "DOWN",
            Key.Up => "UP",
            Key.Decimal => "KP_DEC",
            Key.Add => "KP_ADD",
            Key.Subtract => "KP_SUBTRACT",
            Key.Multiply => "KP_MULTIPLY",
            Key.Divide => "KP_DIVIDE",
            Key.VolumeUp => "VOLUME_UP",
            Key.VolumeDown => "VOLUME_DOWN",
            Key.VolumeMute => "MUTE",
            Key.MediaPlayPause => "PLAYPAUSE",
            Key.MediaStop => "STOP",
            Key.MediaNextTrack => "NEXT",
            Key.MediaPreviousTrack => "PREV",
            Key.BrowserBack => "GO_BACK",
            Key.BrowserForward => "GO_FORWARD",
            Key.BrowserHome => "HOMEPAGE",
            Key.BrowserSearch => "SEARCH",
            Key.LaunchMail => "MAIL",
            Key.Sleep => "SLEEP",
            Key.Pause => "PAUSE",
            Key.Cancel => "CANCEL",
            Key.Apps => "MENU",
            _ => null,
        };
    }

    private static string? GetSingleSymbol(string? keySymbol)
    {
        if (string.IsNullOrEmpty(keySymbol))
        {
            return null;
        }

        var runes = keySymbol.EnumerateRunes();
        if (!runes.MoveNext())
        {
            return null;
        }

        var rune = runes.Current;
        return runes.MoveNext() || Rune.IsControl(rune) ? null : rune.ToString();
    }

    private static string? GetFallbackSymbol(Key key, KeyModifiers modifiers)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            var character = (char)('a' + (int)key - (int)Key.A);
            return modifiers.HasFlag(KeyModifiers.Shift)
                ? char.ToUpperInvariant(character).ToString()
                : character.ToString();
        }

        return key is >= Key.D0 and <= Key.D9
            ? ((char)('0' + (int)key - (int)Key.D0)).ToString()
            : null;
    }
}
