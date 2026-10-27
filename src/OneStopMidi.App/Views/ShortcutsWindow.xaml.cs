using System.Windows;
using System.Windows.Data;
using OneStopMidi.App.Input;

namespace OneStopMidi.App.Views;

public partial class ShortcutsWindow : Window
{
    public sealed record Row(string Category, string Name, string Keys);

    public ShortcutsWindow(KeyMap map)
    {
        InitializeComponent();
        var rows = AppActionInfo.All
            .Select(a => new Row(AppActionInfo.GetCategory(a), AppActionInfo.GetName(a), map.FormatChords(a)))
            .Where(r => r.Keys.Length > 0)
            .ToList();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Category)));
        ShortcutList.ItemsSource = view;
    }
}
