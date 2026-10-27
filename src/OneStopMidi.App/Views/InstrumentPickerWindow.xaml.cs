using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using OneStopMidi.App.Engine;

namespace OneStopMidi.App.Views;

public partial class InstrumentPickerWindow : Window
{
    public sealed record ProgramEntry(int Program, string Name, string Family)
    {
        public string Number => (Program + 1).ToString("000");
    }

    private readonly ListCollectionView _view;

    public InstrumentPickerWindow(int channel, int program, int bankMsb, int bankLsb, bool isDrum)
    {
        InitializeComponent();
        HeaderText.Text = $"Channel {channel + 1}{(isDrum ? " (drums)" : "")}";
        var items = Enumerable.Range(0, 128)
            .Select(p => isDrum
                ? new ProgramEntry(p, GeneralMidi.DrumKitName(p), GeneralMidi.DrumKits.ContainsKey(p) ? "GM drum kits" : "Other kits")
                : new ProgramEntry(p, GeneralMidi.ProgramName(p), GeneralMidi.FamilyOf(p)))
            .ToList();
        _view = new ListCollectionView(items);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProgramEntry.Family)));
        ProgramList.ItemsSource = _view;
        ProgramList.SelectedItem = items[Math.Clamp(program, 0, 127)];
        MsbBox.Text = bankMsb.ToString();
        LsbBox.Text = bankLsb.ToString();
        Loaded += (_, _) =>
        {
            ProgramList.ScrollIntoView(ProgramList.SelectedItem);
            FilterBox.Focus();
        };
        PreviewKeyDown += (_, e) =>
        {
            // Down from the filter jumps into the list.
            if (e.Key == Key.Down && FilterBox.IsKeyboardFocused && ProgramList.Items.Count > 0)
            {
                if (ProgramList.SelectedIndex < 0) ProgramList.SelectedIndex = 0;
                (ProgramList.ItemContainerGenerator.ContainerFromItem(ProgramList.SelectedItem) as UIElement)?.Focus();
                e.Handled = true;
            }
        };
    }

    public int SelectedProgram { get; private set; }
    public int? BankMsb { get; private set; }
    public int? BankLsb { get; private set; }

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var f = FilterBox.Text.Trim();
        _view.Filter = f.Length == 0
            ? null
            : o => o is ProgramEntry p && (p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                         p.Family.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                         p.Number.TrimStart('0') == f.TrimStart('0'));
        if (ProgramList.SelectedItem is null && ProgramList.Items.Count > 0) ProgramList.SelectedIndex = 0;
    }

    private void ProgramList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Ok_Click(sender, e);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ProgramList.SelectedItem is not ProgramEntry p) return;
        SelectedProgram = p.Program;
        BankMsb = int.TryParse(MsbBox.Text, out var m) ? Math.Clamp(m, 0, 127) : null;
        BankLsb = int.TryParse(LsbBox.Text, out var l) ? Math.Clamp(l, 0, 127) : null;
        DialogResult = true;
    }
}
