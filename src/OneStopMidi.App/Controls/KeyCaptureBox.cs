using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OneStopMidi.App.Input;

namespace OneStopMidi.App.Controls;

/// <summary>
/// Read-only box that records the next key press as a <see cref="KeyChord"/>. Every key is swallowed in
/// PreviewKeyDown — including arrows, Enter, NumPad Enter, Space, Esc, Tab and Alt combos — so focus does not
/// move and dialog default/cancel buttons do not fire while it has focus.
/// </summary>
public sealed class KeyCaptureBox : TextBox
{
    public const string Placeholder = "Click here, then press a key…";

    public static readonly DependencyProperty ChordProperty = DependencyProperty.Register(
        nameof(Chord), typeof(KeyChord?), typeof(KeyCaptureBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((KeyCaptureBox)d).UpdateText()));

    public KeyChord? Chord
    {
        get => (KeyChord?)GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    /// <summary>Raised when a complete chord was captured.</summary>
    public event EventHandler<KeyChord>? ChordCaptured;

    public KeyCaptureBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ContextMenu = null;
        Cursor = Cursors.Hand;
        UpdateText();
    }

    private void UpdateText() => Text = Chord is { } c ? c.Format() : Placeholder;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var chord = KeyChord.FromKeyEventArgs(e);
        if (KeyChord.IsModifierKey(chord.Key) || chord.Key is Key.None or Key.System)
        {
            // Show the modifiers being held while waiting for the real key.
            var mods = new KeyChord(Key.A, Keyboard.Modifiers).Format();
            Text = mods.Length > 1 ? mods[..^1] + "…" : Placeholder;
            return;
        }
        Chord = chord;
        ChordCaptured?.Invoke(this, chord);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        if (Chord is null && Keyboard.Modifiers == ModifierKeys.None) UpdateText();
        else if (Chord is not null) UpdateText();
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e) => e.Handled = true;

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (Chord is null) Text = "Press a key…";
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        UpdateText();
    }
}
