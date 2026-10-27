using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Windows.Input;

namespace OneStopMidi.App.Input;

/// <summary>
/// One key gesture: a key plus modifiers. NumPad Enter is a distinct chord from the main Enter key
/// (WPF reports both as <see cref="Key.Enter"/>; the numeric-keypad one carries the extended-key flag).
/// Text form examples: "Space", "Ctrl+Shift+Left", "NumPadEnter", "Ctrl+NumPad+", "Alt+F5".
/// </summary>
public readonly record struct KeyChord
{
    public Key Key { get; }
    public ModifierKeys Modifiers { get; }
    /// <summary>True only for the numeric keypad Enter key.</summary>
    public bool IsNumPadEnter { get; }

    public KeyChord(Key key, ModifierKeys modifiers = ModifierKeys.None, bool numPadEnter = false)
    {
        Key = key;
        Modifiers = modifiers;
        IsNumPadEnter = numPadEnter && key == Key.Enter;
    }

    public static KeyChord NumPadEnter => new(Key.Enter, ModifierKeys.None, numPadEnter: true);

    /// <summary>False for "no key" and for chords that consist of a modifier key alone.</summary>
    public bool IsValid => Key != Key.None && !IsModifierKey(Key) && Key is not (Key.System or Key.ImeProcessed or Key.DeadCharProcessed);

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    // ------------------------------------------------------------------ construction from input

    /// <summary>
    /// Builds a chord from the raw parts of a key event. <paramref name="key"/> is <c>KeyEventArgs.Key</c>;
    /// when it is <see cref="Key.System"/> (Alt combos, F10) the real key is <paramref name="systemKey"/>.
    /// <paramref name="isExtendedKey"/> is the hardware extended-key flag (bit 24 of the WM_KEYDOWN lParam).
    /// </summary>
    public static KeyChord FromKeyEvent(Key key, Key systemKey, ModifierKeys modifiers, bool isExtendedKey,
        Key imeProcessedKey = Key.None, Key deadCharProcessedKey = Key.None)
    {
        var real = key switch
        {
            Key.System => systemKey,
            Key.ImeProcessed => imeProcessedKey,
            Key.DeadCharProcessed => deadCharProcessedKey,
            _ => key,
        };
        return new KeyChord(real, modifiers, numPadEnter: real == Key.Enter && isExtendedKey);
    }

    /// <summary>Builds a chord from a live WPF key event (see <see cref="KeyInput"/> for the extended-key detection).</summary>
    public static KeyChord FromKeyEventArgs(KeyEventArgs e)
    {
        var mods = e.KeyboardDevice?.Modifiers ?? Keyboard.Modifiers;
        return FromKeyEvent(e.Key, e.SystemKey, mods, KeyInput.IsExtendedKey(e), e.ImeProcessedKey, e.DeadCharProcessedKey);
    }

    // ------------------------------------------------------------------ text form

    private static readonly Dictionary<Key, string> KeyNames = new()
    {
        [Key.Enter] = "Enter",
        [Key.Escape] = "Esc",
        [Key.Back] = "Backspace",
        [Key.Space] = "Space",
        [Key.Tab] = "Tab",
        [Key.Delete] = "Delete",
        [Key.Insert] = "Insert",
        [Key.PageUp] = "PageUp",
        [Key.PageDown] = "PageDown",
        [Key.Home] = "Home",
        [Key.End] = "End",
        [Key.Left] = "Left",
        [Key.Right] = "Right",
        [Key.Up] = "Up",
        [Key.Down] = "Down",
        [Key.Add] = "NumPad+",
        [Key.Subtract] = "NumPad-",
        [Key.Multiply] = "NumPad*",
        [Key.Divide] = "NumPad/",
        [Key.Decimal] = "NumPad.",
        [Key.D0] = "0", [Key.D1] = "1", [Key.D2] = "2", [Key.D3] = "3", [Key.D4] = "4",
        [Key.D5] = "5", [Key.D6] = "6", [Key.D7] = "7", [Key.D8] = "8", [Key.D9] = "9",
        [Key.OemComma] = ",",
        [Key.OemPeriod] = ".",
        [Key.OemMinus] = "-",
        [Key.OemPlus] = "=",
        [Key.OemQuestion] = "/",
        [Key.OemSemicolon] = ";",
        [Key.OemQuotes] = "'",
        [Key.OemOpenBrackets] = "[",
        [Key.OemCloseBrackets] = "]",
        [Key.OemPipe] = "\\",
        [Key.OemTilde] = "`",
        [Key.CapsLock] = "CapsLock",
        [Key.PrintScreen] = "PrintScreen",
        [Key.Pause] = "Pause",
        [Key.Scroll] = "ScrollLock",
        [Key.NumLock] = "NumLock",
        [Key.Apps] = "Menu",
        [Key.MediaPlayPause] = "MediaPlayPause",
        [Key.MediaStop] = "MediaStop",
        [Key.MediaNextTrack] = "MediaNext",
        [Key.MediaPreviousTrack] = "MediaPrevious",
        [Key.VolumeUp] = "VolumeUp",
        [Key.VolumeDown] = "VolumeDown",
        [Key.VolumeMute] = "VolumeMute",
    };

    private static readonly Dictionary<string, Key> KeyAliases = BuildAliases();

    private static Dictionary<string, Key> BuildAliases()
    {
        var d = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, n) in KeyNames) d[n] = k;
        d["Return"] = Key.Enter;
        d["Escape"] = Key.Escape;
        d["Back"] = Key.Back;
        d["Del"] = Key.Delete;
        d["Ins"] = Key.Insert;
        d["PgUp"] = Key.PageUp;
        d["PgDn"] = Key.PageDown;
        d["Prior"] = Key.PageUp;
        d["Next"] = Key.PageDown;
        d["+"] = Key.Add;
        d["Plus"] = Key.Add;
        d["Add"] = Key.Add;
        d["Subtract"] = Key.Subtract;
        d["Multiply"] = Key.Multiply;
        d["Divide"] = Key.Divide;
        d["Decimal"] = Key.Decimal;
        d["Comma"] = Key.OemComma;
        d["Period"] = Key.OemPeriod;
        d["Minus"] = Key.OemMinus;
        d["Equals"] = Key.OemPlus;
        d["Spacebar"] = Key.Space;
        return d;
    }

    public override string ToString() => Format();

    public string Format()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(FormatKey(Key, IsNumPadEnter));
        return sb.ToString();
    }

    public static string FormatKey(Key key, bool numPadEnter = false)
    {
        if (key == Key.Enter && numPadEnter) return "NumPadEnter";
        if (KeyNames.TryGetValue(key, out var name)) return name;
        return key.ToString(); // A..Z, F1..F24, NumPad0..NumPad9, etc.
    }

    public static KeyChord Parse(string text) =>
        TryParse(text, out var c) ? c : throw new FormatException($"Invalid key chord: '{text}'");

    public static bool TryParse(string? text, [NotNullWhen(true)] out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        // The key part may itself end with '+' ("NumPad+", "Ctrl++"), so split from the right with care.
        string keyPart, modPart;
        if (s.EndsWith('+'))
        {
            var trimmed = s[..^1];
            var idx = trimmed.LastIndexOf('+');
            keyPart = s[(idx + 1)..];
            modPart = idx >= 0 ? s[..idx] : "";
        }
        else
        {
            var idx = s.LastIndexOf('+');
            keyPart = s[(idx + 1)..];
            modPart = idx >= 0 ? s[..idx] : "";
        }

        var mods = ModifierKeys.None;
        if (modPart.Length > 0)
        {
            foreach (var raw in modPart.Split('+'))
            {
                var m = raw.Trim();
                if (m.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || m.Equals("Control", StringComparison.OrdinalIgnoreCase)) mods |= ModifierKeys.Control;
                else if (m.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= ModifierKeys.Shift;
                else if (m.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= ModifierKeys.Alt;
                else if (m.Equals("Win", StringComparison.OrdinalIgnoreCase) || m.Equals("Windows", StringComparison.OrdinalIgnoreCase)) mods |= ModifierKeys.Windows;
                else return false;
            }
        }

        keyPart = keyPart.Trim();
        if (keyPart.Length == 0) return false;

        if (keyPart.Equals("NumPadEnter", StringComparison.OrdinalIgnoreCase) ||
            keyPart.Equals("NumEnter", StringComparison.OrdinalIgnoreCase))
        {
            chord = new KeyChord(Key.Enter, mods, numPadEnter: true);
            return true;
        }

        Key key;
        if (KeyAliases.TryGetValue(keyPart, out var aliased)) key = aliased;
        else if (keyPart.Length == 1 && char.IsDigit(keyPart[0])) key = Key.D0 + (keyPart[0] - '0');
        else if (!Enum.TryParse(keyPart, ignoreCase: true, out key) || int.TryParse(keyPart, out _)) return false;

        chord = new KeyChord(key, mods);
        return chord.IsValid;
    }
}
