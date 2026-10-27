using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OneStopMidi.App.Input;
using Xunit;

namespace OneStopMidi.App.Tests;

/// <summary>
/// End-to-end check of NumPad Enter detection through WPF's real input pipeline: a raw keyboard report (what
/// HwndKeyboardInputProvider produces from WM_KEYDOWN) is pushed through InputManager into a real window, and
/// the window's PreviewKeyDown must see "NumPadEnter" for the extended report and "Enter" for the normal one.
/// Also checks that a focused ListBox does not get to consume arrow keys / Enter before the window handler.
/// </summary>
public class KeyPipelineTests
{
    private static readonly Assembly Core = typeof(Keyboard).Assembly;

    internal static object RawReport(PresentationSource source, string actions, int scanCode, bool extended, int vk)
    {
        var reportType = Core.GetType("System.Windows.Input.RawKeyboardInputReport", throwOnError: true)!;
        var actionsType = Core.GetType("System.Windows.Input.RawKeyboardActions", throwOnError: true)!;
        var ctor = reportType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 9);
        return ctor.Invoke([source, InputMode.Foreground, Environment.TickCount, Enum.Parse(actionsType, actions), scanCode, extended, false, vk, IntPtr.Zero]);
    }

    internal static void Push(PresentationSource source, object report)
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var argsType = Core.GetType("System.Windows.Input.InputReportEventArgs", throwOnError: true)!;
        var args = (InputEventArgs)argsType.GetConstructors(any).Single(c => c.GetParameters().Length == 2)
            .Invoke([Keyboard.PrimaryDevice, report]);
        var previewEvent = (RoutedEvent)(typeof(InputManager).GetField("PreviewInputReportEvent", any)?.GetValue(null)
            ?? typeof(InputManager).GetProperty("PreviewInputReportEvent", any)!.GetValue(null)!);
        args.RoutedEvent = previewEvent;
        InputManager.Current.ProcessInput(args);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    [Fact]
    public void RealPipeline_DistinguishesNumPadEnter_AndWindowSeesKeysBeforeListBox()
    {
        Sta.Run(() =>
        {
            var list = new ListBox { ItemsSource = new[] { "a", "b", "c" } };
            var window = new Window
            {
                Content = list,
                Width = 300,
                Height = 200,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };
            var seen = new List<string>();
            window.PreviewKeyDown += (_, e) =>
            {
                seen.Add(KeyChord.FromKeyEventArgs(e).Format());
                e.Handled = true; // like MainWindow: the list must not get the key
            };
            var listSawKey = false;
            list.KeyDown += (_, _) => listSawKey = true;
            window.Show();
            try
            {
                var source = PresentationSource.FromVisual(window)!;
                Push(source, RawReport(source, "Activate", 0, false, 0));
                list.Focus();
                Keyboard.Focus(list);

                const int vkReturn = 0x0D, scanEnter = 0x1C, vkDown = 0x28, scanDown = 0x50;
                Push(source, RawReport(source, "KeyDown", scanEnter, extended: true, vkReturn));
                Push(source, RawReport(source, "KeyUp", scanEnter, extended: true, vkReturn));
                Push(source, RawReport(source, "KeyDown", scanEnter, extended: false, vkReturn));
                Push(source, RawReport(source, "KeyUp", scanEnter, extended: false, vkReturn));
                Push(source, RawReport(source, "KeyDown", scanDown, extended: true, vkDown));
                Push(source, RawReport(source, "KeyUp", scanDown, extended: true, vkDown));

                Assert.Equal(["NumPadEnter", "Enter", "Down"], seen);
                Assert.False(listSawKey);
            }
            finally
            {
                window.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void DispatchPolicy_SkipsTextBoxes_ButNotLists()
    {
        Sta.Run(() =>
        {
            Assert.True(KeyInput.IsInTextOrMenuContext(new TextBox()));
            Assert.True(KeyInput.IsInTextOrMenuContext(new PasswordBox()));
            Assert.True(KeyInput.IsInTextOrMenuContext(new MenuItem()));
            Assert.True(KeyInput.IsInTextOrMenuContext(new ComboBox { IsEditable = true }));
            Assert.False(KeyInput.IsInTextOrMenuContext(new ListBox()));
            Assert.False(KeyInput.IsInTextOrMenuContext(new TreeView()));
            Assert.False(KeyInput.IsInTextOrMenuContext(new Button()));
            Assert.False(KeyInput.IsInTextOrMenuContext(null));
        });
    }
}
