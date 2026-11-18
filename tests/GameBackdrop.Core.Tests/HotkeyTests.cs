using GameBackdrop.Core.Settings;

namespace GameBackdrop.Core.Tests;

public sealed class HotkeyTests
{
    [Fact]
    public void The_default_is_ctrl_alt_b()
    {
        Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x42), Hotkey.Default);
        Assert.Equal("Ctrl+Alt+B", Hotkey.Default.ToString());
    }

    [Theory]
    [InlineData("Ctrl+Alt+B", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x42u)]
    [InlineData("ctrl + alt + b", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x42u)]
    [InlineData("Control+Shift+F9", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x78u)]
    [InlineData("Win+Alt+7", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x37u)]
    [InlineData("Alt+F24", HotkeyModifiers.Alt, 0x87u)]
    public void A_hotkey_is_read_from_its_written_form(string text, HotkeyModifiers modifiers, uint key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("B")]            // no modifier: it would steal a plain key from every app
    [InlineData("Shift+B")]      // Shift alone only makes a capital letter
    [InlineData("Ctrl+Alt")]     // no key
    [InlineData("Ctrl+Alt+B+C")] // two keys
    [InlineData("Ctrl+Ctrl+B")]
    [InlineData("Ctrl+Hyper+B")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+")]
    public void A_hotkey_that_isnt_a_modifier_and_one_key_is_refused(string text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Fact]
    public void A_hotkey_writes_back_in_the_form_it_reads()
    {
        Assert.True(Hotkey.TryParse("shift+ctrl+f9", out var hotkey));
        Assert.Equal("Ctrl+Shift+F9", hotkey.ToString());
    }

    [Fact]
    public void Settings_that_name_no_hotkey_get_the_default()
    {
        Assert.Equal(Hotkey.Default, Hotkey.FromSettings(null, out var understood));
        Assert.True(understood);
    }

    [Fact]
    public void Settings_that_name_a_hotkey_get_it()
    {
        Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x78), Hotkey.FromSettings("Ctrl+Shift+F9", out var understood));
        Assert.True(understood);
    }

    [Fact]
    public void Settings_whose_hotkey_isnt_understood_get_the_default_and_say_so()
    {
        Assert.Equal(Hotkey.Default, Hotkey.FromSettings("Ctrl+Hyper+B", out var understood));
        Assert.False(understood);
    }
}
