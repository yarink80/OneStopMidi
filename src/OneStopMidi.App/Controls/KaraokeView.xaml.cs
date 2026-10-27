using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using OneStopMidi.App.ViewModels;
using OneStopMidi.Core;

namespace OneStopMidi.App.Controls;

/// <summary>
/// 8-line lyrics display: sung text red, upcoming text yellow, the line that will start the next page white;
/// chord line at the bottom.
/// </summary>
public partial class KaraokeView : UserControl
{
    private static readonly Brush SungBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x45)));
    private static readonly Brush UpcomingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x4A)));
    private static readonly Brush NextPageBrush = Brushes.White;
    private static readonly Brush CurrentChordBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0xD1, 0xFF)));
    private static readonly Brush OtherChordBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0x66, 0x7A)));

    private readonly TextBlock[] _rows = new TextBlock[KaraokeLayout.DisplayRows];
    private IMidiSong? _song;
    private KaraokeLayout? _layout;
    private int _shownPage = -1;
    private long _lastTick = -1;
    private int _lastChordIndex = int.MinValue;

    public KaraokeView()
    {
        InitializeComponent();
        for (var i = 0; i < _rows.Length; i++)
        {
            _rows[i] = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 2),
            };
            LinesPanel.Children.Add(_rows[i]);
        }
        ApplyFontSize();
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyFontSize();

    private void ApplyFontSize()
    {
        var h = ActualHeight > 0 ? ActualHeight : 400;
        var size = Math.Clamp(h / 14.0, 12, 48);
        foreach (var r in _rows) r.FontSize = size;
        ChordLine.FontSize = Math.Clamp(size * 0.8, 11, 36);
    }

    /// <summary>Called from the UI timer.</summary>
    public void Update(IMidiSong? song, long tick)
    {
        if (!ReferenceEquals(song, _song))
        {
            _song = song;
            _layout = song is null ? null : new KaraokeLayout(song.Lyrics);
            _shownPage = -1;
            _lastTick = -1;
            _lastChordIndex = int.MinValue;
            NoLyricsText.Visibility = _layout is { IsEmpty: false } ? Visibility.Collapsed : Visibility.Visible;
            NoLyricsText.Text = song is null ? "No song loaded" : "No lyrics in this song";
        }
        if (tick == _lastTick) return;
        _lastTick = tick;
        RenderLyrics(tick);
        RenderChords(tick);
    }

    private void RenderLyrics(long tick)
    {
        if (_layout is null || _layout.IsEmpty)
        {
            foreach (var r in _rows) r.Inlines.Clear();
            return;
        }
        var pageIndex = _layout.PageIndexAt(tick);
        var page = _layout.Pages[pageIndex];
        var rebuild = pageIndex != _shownPage;
        _shownPage = pageIndex;

        for (var i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            if (i < page.Count)
            {
                var line = page[i];
                if (rebuild || row.Inlines.Count != line.Syllables.Count)
                {
                    row.Inlines.Clear();
                    foreach (var s in line.Syllables) row.Inlines.Add(new Run(s.Text));
                }
                var j = 0;
                foreach (var inline in row.Inlines)
                {
                    var brush = line.Syllables[j++].Tick <= tick ? SungBrush : UpcomingBrush;
                    if (!ReferenceEquals(inline.Foreground, brush)) inline.Foreground = brush;
                }
            }
            else if (i == page.Count && pageIndex + 1 < _layout.Pages.Count)
            {
                if (rebuild)
                {
                    row.Inlines.Clear();
                    row.Inlines.Add(new Run(_layout.Pages[pageIndex + 1][0].Text) { Foreground = NextPageBrush });
                }
            }
            else if (rebuild)
            {
                row.Inlines.Clear();
            }
        }
    }

    private void RenderChords(long tick)
    {
        var chords = _song?.Chords;
        if (chords is null || chords.Count == 0)
        {
            if (_lastChordIndex != -2) ChordLine.Inlines.Clear();
            _lastChordIndex = -2;
            return;
        }
        var current = -1;
        for (var i = 0; i < chords.Count; i++)
        {
            if (chords[i].Tick <= tick) current = i;
            else break;
        }
        if (current == _lastChordIndex) return;
        _lastChordIndex = current;
        ChordLine.Inlines.Clear();
        var start = Math.Max(0, current);
        for (var i = start; i < Math.Min(chords.Count, start + 6); i++)
        {
            if (ChordLine.Inlines.Count > 0) ChordLine.Inlines.Add(new Run("   "));
            ChordLine.Inlines.Add(new Run(chords[i].Name)
            {
                Foreground = i == current ? CurrentChordBrush : OtherChordBrush,
                FontWeight = i == current ? FontWeights.Bold : FontWeights.Normal,
            });
        }
    }
}
