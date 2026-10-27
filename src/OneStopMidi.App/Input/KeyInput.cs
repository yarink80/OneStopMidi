using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace OneStopMidi.App.Input;

/// <summary>
/// Low-level keyboard helpers:
/// <list type="bullet">
/// <item>Extended-key detection, so NumPad Enter can be told apart from the main Enter key.</item>
/// <item>The policy that decides whether the main window may intercept a key press.</item>
/// </list>
/// </summary>
public static class KeyInput
{
    // WPF's KeyboardDevice copies the extended-key bit of the raw keyboard report into the (internal)
    // KeyEventArgs.IsExtendedKey property. It is not public, so it is read through reflection.
    private static readonly PropertyInfo? IsExtendedKeyProperty =
        typeof(KeyEventArgs).GetProperty("IsExtendedKey", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    /// <summary>True when the reflection path to KeyEventArgs.IsExtendedKey is available.</summary>
    public static bool HasReflectedExtendedKeyFlag => IsExtendedKeyProperty is { CanRead: true };

    /// <summary>Exposed for tests: sets the internal flag on a synthetic KeyEventArgs.</summary>
    internal static bool TrySetExtendedKeyForTest(KeyEventArgs e, bool value)
    {
        if (IsExtendedKeyProperty?.SetMethod is null) return false;
        IsExtendedKeyProperty.SetValue(e, value);
        return true;
    }

    // Fallback path: a thread message filter remembers whether the last WM_KEYDOWN/WM_SYSKEYDOWN for VK_RETURN
    // had the extended bit (bit 24 of lParam). The filter runs before WPF turns the message into input events.
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_RETURN = 0x0D;
    private static bool _hookInstalled;
    private static bool _lastReturnExtended;

    /// <summary>Installs the message filter fallback (call once on the UI thread).</summary>
    public static void InstallMessageHook()
    {
        if (_hookInstalled) return;
        _hookInstalled = true;
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
    }

    private static void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if ((msg.message == WM_KEYDOWN || msg.message == WM_SYSKEYDOWN) && (int)msg.wParam == VK_RETURN)
            _lastReturnExtended = IsExtendedLParam(msg.lParam);
    }

    /// <summary>Bit 24 of a WM_KEYDOWN lParam is the extended-key flag.</summary>
    public static bool IsExtendedLParam(nint lParam) => (((long)lParam >> 24) & 1) != 0;

    /// <summary>True when the key event came from an extended key (for Enter: the numeric keypad Enter).</summary>
    public static bool IsExtendedKey(KeyEventArgs e)
    {
        if (IsExtendedKeyProperty is { CanRead: true })
        {
            try
            {
                if (IsExtendedKeyProperty.GetValue(e) is bool b) return b;
            }
            catch
            {
                // fall through to the message-hook fallback
            }
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key == Key.Enter && _hookInstalled && _lastReturnExtended;
    }

    // ------------------------------------------------------------------ dispatch policy

    /// <summary>
    /// Decides whether the main window's PreviewKeyDown handler may treat this key press as a shortcut.
    /// Text boxes / editable combo boxes, open menus (incl. Alt-menu navigation), open drop-downs and
    /// bare modifier presses are left alone.
    /// </summary>
    public static bool ShouldIntercept(KeyEventArgs e, DependencyObject? focused)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        if (key == Key.None || KeyChord.IsModifierKey(key)) return false;
        // F10 / bare Alt start menu navigation; leave them to WPF unless a chord with modifiers is bound.
        return !IsInTextOrMenuContext(focused);
    }

    /// <summary>True when focus is somewhere a key press belongs to the control (typing, menus, drop-downs).</summary>
    public static bool IsInTextOrMenuContext(DependencyObject? focused)
    {
        for (var d = focused; d is not null; d = GetParent(d))
        {
            switch (d)
            {
                case TextBoxBase:
                case PasswordBox:
                    return true;
                case ComboBox { IsEditable: true }:
                case ComboBox { IsDropDownOpen: true }:
                    return true;
                case MenuItem:
                case MenuBase:
                    return true;
                case Popup:
                    return true;
            }
        }
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject d)
    {
        if (d is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            var p = VisualTreeHelper.GetParent(d);
            if (p is not null) return p;
        }
        return LogicalTreeHelper.GetParent(d);
    }
}
