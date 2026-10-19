// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Input;
using CanX.App;
using Xunit;

namespace CanX.Foundation.Tests;

public sealed class MpvInputKeyMapperTests
{
    [Theory]
    [InlineData(Key.F, "f", KeyModifiers.None, "f")]
    [InlineData(Key.F, "F", KeyModifiers.Shift, "F")]
    [InlineData(Key.Right, null, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+RIGHT")]
    [InlineData(Key.Space, " ", KeyModifiers.None, "SPACE")]
    [InlineData(Key.Oem3, "#", KeyModifiers.Shift, "SHARP")]
    [InlineData(Key.MediaPlayPause, null, KeyModifiers.None, "PLAYPAUSE")]
    [InlineData(Key.NumPad4, null, KeyModifiers.Control, "Ctrl+KP4")]
    [InlineData(Key.PageUp, null, KeyModifiers.None, "PGUP")]
    [InlineData(Key.PageUp, null, KeyModifiers.Shift, "Shift+PGUP")]
    [InlineData(Key.PageDown, null, KeyModifiers.None, "PGDWN")]
    public void Map_UsesMpvInputConfNames(
        Key key,
        string? keySymbol,
        KeyModifiers modifiers,
        string expected)
    {
        Assert.Equal(expected, MpvInputKeyMapper.Map(key, keySymbol, modifiers));
    }

    [Fact]
    public void Map_IgnoresModifierKeysByThemselves()
    {
        Assert.Null(MpvInputKeyMapper.Map(Key.LeftCtrl, null, KeyModifiers.Control));
    }

    [Fact]
    public void AddModifiers_PreservesMouseWheelModifiers()
    {
        Assert.Equal(
            "Ctrl+Alt+WHEEL_UP",
            MpvInputKeyMapper.AddModifiers("WHEEL_UP", KeyModifiers.Control | KeyModifiers.Alt));
    }
}
