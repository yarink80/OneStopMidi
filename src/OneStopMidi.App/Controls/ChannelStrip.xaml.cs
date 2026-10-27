using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OneStopMidi.App.ViewModels;

namespace OneStopMidi.App.Controls;

/// <summary>One mixer channel strip; DataContext is a <see cref="ChannelStripModel"/>.</summary>
public partial class ChannelStrip : UserControl
{
    public ChannelStrip()
    {
        InitializeComponent();
    }

    private ChannelStripModel? Model => DataContext as ChannelStripModel;

    private void Instrument_Click(object sender, RoutedEventArgs e) => Model?.PickInstrument();

    private void Pan_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Model is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        Model.Pan = MixerModel.DefaultPan;
        e.Handled = true;
    }

    private void Volume_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Model is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        Model.Volume = MixerModel.DefaultVolume;
        e.Handled = true;
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Alt+drag moves every fader by the same amount (only for user drags, not engine refreshes).
        if (Model is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        if (!VolumeSlider.IsMouseCaptureWithin && Mouse.LeftButton != MouseButtonState.Pressed) return;
        var delta = (int)Math.Round(e.NewValue) - (int)Math.Round(e.OldValue);
        FindMixer()?.ApplyVolumeDeltaToOthers(Model.Channel, delta);
    }

    /// <summary>The mixer is provided by the hosting ItemsControl's DataContext.</summary>
    private MixerModel? FindMixer()
    {
        DependencyObject? d = this;
        while (d is not null)
        {
            if (d is FrameworkElement { DataContext: MixerModel m }) return m;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void Solo_Click(object sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        var wasSoloed = Model.Soloed;
        Model.ToggleSolo(additive: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        // Show the expected state until the engine reports back (SetCurrentValue keeps the binding).
        if (sender is ToggleButton tb) tb.SetCurrentValue(ToggleButton.IsCheckedProperty, !wasSoloed);
    }
}
