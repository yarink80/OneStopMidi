using System.Windows;
using OneStopMidi.App.Playlists;

namespace OneStopMidi.App.Views;

public partial class SearchWindow : Window
{
    public sealed record Row(string Playlist, string Path)
    {
        public string FileName => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    }

    private readonly PlaylistStore _store;

    public SearchWindow(PlaylistStore store, string? initial)
    {
        InitializeComponent();
        _store = store;
        QueryBox.Text = initial ?? "";
        Loaded += (_, _) =>
        {
            QueryBox.Focus();
            QueryBox.SelectAll();
        };
    }

    public PlaylistSearchHit? Selected { get; private set; }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        var hits = _store.Search(QueryBox.Text, IncludeRecentBox.IsChecked == true);
        Results.ItemsSource = hits.Select(h => new Row(h.Playlist, h.Path)).ToList();
        var playlists = hits.Select(h => h.Playlist).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ResultText.Text = hits.Count == 0
            ? "Not found in any playlist."
            : $"Found {hits.Count} match(es) in the following playlists: {string.Join(", ", playlists)}";
        if (hits.Count > 0) Results.SelectedIndex = 0;
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not Row r) return;
        Selected = new PlaylistSearchHit(r.Playlist, r.Path);
        DialogResult = true;
    }

    private void Results_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Go_Click(sender, e);
}
