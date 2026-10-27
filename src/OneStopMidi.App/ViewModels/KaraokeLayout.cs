using OneStopMidi.Core;

namespace OneStopMidi.App.ViewModels;

/// <summary>
/// Splits lyrics into lines and pages for the 8-line karaoke view: each page has up to
/// <see cref="LinesPerPage"/> lines (a paragraph marker starts a new page) and the 8th row previews the
/// line that will start the next page.
/// </summary>
public sealed class KaraokeLayout
{
    public const int DisplayRows = 8;
    public const int LinesPerPage = DisplayRows - 1;

    public sealed record Line(IReadOnlyList<LyricSyllable> Syllables)
    {
        public long StartTick => Syllables.Count > 0 ? Syllables[0].Tick : 0;
        public string Text => string.Concat(Syllables.Select(s => s.Text));
    }

    public IReadOnlyList<IReadOnlyList<Line>> Pages { get; }

    public KaraokeLayout(IReadOnlyList<LyricSyllable> lyrics)
    {
        var lines = new List<(Line Line, bool NewParagraph)>();
        var current = new List<LyricSyllable>();
        var currentPara = true;
        foreach (var s in lyrics.OrderBy(l => l.Tick))
        {
            if ((s.StartsNewLine || s.StartsNewParagraph) && current.Count > 0)
            {
                lines.Add((new Line(current), currentPara));
                current = new List<LyricSyllable>();
                currentPara = false;
            }
            if (s.StartsNewParagraph) currentPara = true;
            current.Add(s);
        }
        if (current.Count > 0) lines.Add((new Line(current), currentPara));

        var pages = new List<IReadOnlyList<Line>>();
        var page = new List<Line>();
        foreach (var (line, para) in lines)
        {
            if (page.Count >= LinesPerPage || (para && page.Count > 0))
            {
                pages.Add(page);
                page = new List<Line>();
            }
            page.Add(line);
        }
        if (page.Count > 0) pages.Add(page);
        Pages = pages;
    }

    public bool IsEmpty => Pages.Count == 0;

    /// <summary>Index of the page showing <paramref name="tick"/>: the page of the latest syllable already reached.</summary>
    public int PageIndexAt(long tick)
    {
        var idx = 0;
        for (var i = 0; i < Pages.Count; i++)
        {
            if (Pages[i].Count > 0 && Pages[i][0].StartTick <= tick) idx = i;
            else break;
        }
        return idx;
    }
}
