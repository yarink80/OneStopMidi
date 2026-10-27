using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OneStopMidi.App.Input;
using Xunit;

namespace OneStopMidi.App.Tests;

public class KeyChordTests
{
    [Fact]
    public void NumPadEnter_IsDistinctFromMainEnter()
    {
        var main = new KeyChord(Key.Enter);
        var pad = KeyChord.NumPadEnter;
        Assert.NotEqual(main, pad);
        Assert.NotEqual(main.GetHashCode(), pad.GetHashCode());
        Assert.Equal(Key.Enter, pad.Key);
        Assert.True(pad.IsNumPadEnter);
        Assert.False(main.IsNumPadEnter);
    }

    [Fact]
    public void Return_And_Enter_AreTheSameKey()
    {
        Assert.Equal(new KeyChord(Key.Return), new KeyChord(Key.Enter));
    }

    [Fact]
    public void NumPadFlag_IsIgnoredForKeysOtherThanEnter()
    {
        Assert.Equal(new KeyChord(Key.Left), new KeyChord(Key.Left, ModifierKeys.None, numPadEnter: true));
    }

    [Fact]
    public void FromKeyEvent_UsesExtendedFlagOnlyForEnter()
    {
        Assert.Equal(KeyChord.NumPadEnter, KeyChord.FromKeyEvent(Key.Enter, Key.None, ModifierKeys.None, isExtendedKey: true));
        Assert.Equal(new KeyChord(Key.Enter), KeyChord.FromKeyEvent(Key.Enter, Key.None, ModifierKeys.None, isExtendedKey: false));
        // Arrow keys are "extended" keys too, but there is no numpad distinction for them.
        Assert.Equal(new KeyChord(Key.Left), KeyChord.FromKeyEvent(Key.Left, Key.None, ModifierKeys.None, isExtendedKey: true));
    }

    [Fact]
    public void FromKeyEvent_ResolvesSystemKey_ForAltCombos()
    {
        var chord = KeyChord.FromKeyEvent(Key.System, Key.Left, ModifierKeys.Alt, isExtendedKey: true);
        Assert.Equal(new KeyChord(Key.Left, ModifierKeys.Alt), chord);
        Assert.Equal("Alt+Left", chord.Format());

        var altPadEnter = KeyChord.FromKeyEvent(Key.System, Key.Enter, ModifierKeys.Alt, isExtendedKey: true);
        Assert.Equal("Alt+NumPadEnter", altPadEnter.Format());
    }

    [Theory]
    [InlineData("Ctrl+Shift+Left", Key.Left, ModifierKeys.Control | ModifierKeys.Shift, false)]
    [InlineData("NumPadEnter", Key.Enter, ModifierKeys.None, true)]
    [InlineData("Enter", Key.Enter, ModifierKeys.None, false)]
    [InlineData("Return", Key.Enter, ModifierKeys.None, false)]
    [InlineData("Ctrl+NumPadEnter", Key.Enter, ModifierKeys.Control, true)]
    [InlineData("Space", Key.Space, ModifierKeys.None, false)]
    [InlineData("Esc", Key.Escape, ModifierKeys.None, false)]
    [InlineData("Escape", Key.Escape, ModifierKeys.None, false)]
    [InlineData("Tab", Key.Tab, ModifierKeys.None, false)]
    [InlineData("NumPad+", Key.Add, ModifierKeys.None, false)]
    [InlineData("Ctrl+NumPad+", Key.Add, ModifierKeys.Control, false)]
    [InlineData("NumPad-", Key.Subtract, ModifierKeys.None, false)]
    [InlineData("NumPad.", Key.Decimal, ModifierKeys.None, false)]
    [InlineData("NumPad0", Key.NumPad0, ModifierKeys.None, false)]
    [InlineData("Ctrl+,", Key.OemComma, ModifierKeys.Control, false)]
    [InlineData("ctrl + shift + n", Key.N, ModifierKeys.Control | ModifierKeys.Shift, false)]
    [InlineData("Control+Backspace", Key.Back, ModifierKeys.Control, false)]
    [InlineData("Alt+F5", Key.F5, ModifierKeys.Alt, false)]
    [InlineData("PageDown", Key.PageDown, ModifierKeys.None, false)]
    [InlineData("PgUp", Key.PageUp, ModifierKeys.None, false)]
    [InlineData("1", Key.D1, ModifierKeys.None, false)]
    [InlineData("Shift+Z", Key.Z, ModifierKeys.Shift, false)]
    public void Parse_KnownForms(string text, Key key, ModifierKeys mods, bool numPad)
    {
        Assert.True(KeyChord.TryParse(text, out var chord), text);
        Assert.Equal(key, chord.Key);
        Assert.Equal(mods, chord.Modifiers);
        Assert.Equal(numPad, chord.IsNumPadEnter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl")]
    [InlineData("Hyper+A")]
    [InlineData("NotAKey")]
    [InlineData("42")]
    [InlineData("Shift+LeftShift")]
    public void Parse_RejectsInvalid(string text)
    {
        Assert.False(KeyChord.TryParse(text, out _));
    }

    [Theory]
    [InlineData(Key.Left, ModifierKeys.Control | ModifierKeys.Shift, false, "Ctrl+Shift+Left")]
    [InlineData(Key.Enter, ModifierKeys.None, true, "NumPadEnter")]
    [InlineData(Key.Enter, ModifierKeys.None, false, "Enter")]
    [InlineData(Key.Add, ModifierKeys.None, false, "NumPad+")]
    [InlineData(Key.OemComma, ModifierKeys.Control, false, "Ctrl+,")]
    [InlineData(Key.Escape, ModifierKeys.None, false, "Esc")]
    [InlineData(Key.D7, ModifierKeys.Alt, false, "Alt+7")]
    [InlineData(Key.F12, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, false, "Ctrl+Alt+Shift+F12")]
    public void Format_KnownForms(Key key, ModifierKeys mods, bool numPad, string expected)
    {
        Assert.Equal(expected, new KeyChord(key, mods, numPad).Format());
    }

    public static IEnumerable<object[]> RoundTripKeys()
    {
        var mods = new[] { ModifierKeys.None, ModifierKeys.Control, ModifierKeys.Shift | ModifierKeys.Alt, ModifierKeys.Control | ModifierKeys.Shift };
        var keys = new[]
        {
            Key.A, Key.Z, Key.D0, Key.D9, Key.F1, Key.F24, Key.Left, Key.Up, Key.Enter, Key.Space, Key.Escape, Key.Tab,
            Key.Back, Key.Delete, Key.PageUp, Key.PageDown, Key.Home, Key.End, Key.Add, Key.Subtract, Key.Multiply,
            Key.Divide, Key.Decimal, Key.NumPad0, Key.NumPad9, Key.OemComma, Key.OemPeriod, Key.OemMinus, Key.OemPlus,
            Key.OemQuestion, Key.OemOpenBrackets, Key.OemCloseBrackets, Key.OemPipe, Key.OemSemicolon, Key.OemQuotes,
            Key.OemTilde, Key.MediaPlayPause, Key.Insert, Key.Pause,
        };
        foreach (var m in mods)
        foreach (var k in keys)
            yield return [k, m, false];
        foreach (var m in mods) yield return [Key.Enter, m, true];
    }

    [Theory]
    [MemberData(nameof(RoundTripKeys))]
    public void FormatThenParse_RoundTrips(Key key, ModifierKeys mods, bool numPad)
    {
        var chord = new KeyChord(key, mods, numPad);
        var text = chord.Format();
        Assert.True(KeyChord.TryParse(text, out var parsed), text);
        Assert.Equal(chord, parsed);
    }

    [Fact]
    public void ModifierOnly_IsNotValid()
    {
        Assert.False(new KeyChord(Key.LeftCtrl, ModifierKeys.Control).IsValid);
        Assert.False(new KeyChord(Key.None).IsValid);
        Assert.False(default(KeyChord).IsValid);
        Assert.True(new KeyChord(Key.Space).IsValid);
    }

    [Fact]
    public void ExtendedLParamBit_IsBit24()
    {
        Assert.True(KeyInput.IsExtendedLParam(0x011C0001)); // NumPad Enter: scan 0x1C + extended
        Assert.False(KeyInput.IsExtendedLParam(0x001C0001)); // main Enter
    }

    /// <summary>
    /// Verifies the live detection path: WPF's KeyEventArgs carries the hardware extended-key flag in an internal
    /// IsExtendedKey property, which KeyInput reads by reflection; with it set, an Enter event becomes NumPadEnter.
    /// </summary>
    [Fact]
    public void KeyEventArgs_ExtendedFlag_IsReadThroughReflection()
    {
        Sta.Run(() =>
        {
            Assert.True(KeyInput.HasReflectedExtendedKeyFlag, "KeyEventArgs.IsExtendedKey not found - NumPad Enter detection would rely on the message hook only.");
            var source = new TestPresentationSource();
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
            Assert.False(KeyInput.IsExtendedKey(e));
            Assert.Equal("Enter", KeyChord.FromKeyEventArgs(e).Format());

            Assert.True(KeyInput.TrySetExtendedKeyForTest(e, true));
            Assert.True(KeyInput.IsExtendedKey(e));
            Assert.Equal(KeyChord.NumPadEnter, KeyChord.FromKeyEventArgs(e));
        });
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        protected override CompositionTarget? GetCompositionTargetCore() => null;
        public override bool IsDisposed => false;
    }
}
