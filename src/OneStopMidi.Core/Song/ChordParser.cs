using System.Text.RegularExpressions;

namespace OneStopMidi.Core;

/// <summary>Recognises chord symbols embedded in text/lyric events: "%Gb7", "[F7]", "(G13)" or plain "Cm".</summary>
public static partial class ChordParser
{
    // Root, optional accidental, any sequence of quality/extension tokens, optional slash bass.
    [GeneratedRegex(@"^[A-G](?:#|b)?(?:maj|min|dim|aug|sus|add|alt|m|M|\+|°|ø|o|[b#]?\d{1,2}|-)*(?:/[A-G](?:#|b)?)?$")]
    private static partial Regex StrictChord();

    [GeneratedRegex(@"\[([^\[\]]{1,16})\]|\(([^()]{1,16})\)")]
    private static partial Regex InlineBracket();

    /// <summary>True when the whole string is a chord name (strict grammar).</summary>
    public static bool IsChordName(string s) => s.Length is > 0 and <= 16 && StrictChord().IsMatch(s);

    /// <summary>
    /// Tries to read a chord from a complete text event. Prefixed forms (%X, [X], (X)) are always considered;
    /// plain text only when <paramref name="allowPlain"/> and it strictly matches (a lone "A" is treated as a word).
    /// </summary>
    public static bool TryParseEvent(string text, bool allowPlain, out string chord)
    {
        chord = "";
        string t = text.Trim();
        if (t.Length == 0) return false;
        if (t[0] == '%')
        {
            string rest = t[1..].Trim();
            if (rest.Length > 0 && rest[0] is >= 'A' and <= 'G') { chord = rest; return true; }
            return false;
        }
        if (t.Length > 2 && ((t[0] == '[' && t[^1] == ']') || (t[0] == '(' && t[^1] == ')')))
        {
            string inner = t[1..^1].Trim();
            if (IsChordName(inner)) { chord = inner; return true; }
            return false;
        }
        if (allowPlain && t != "A" && IsChordName(t)) { chord = t; return true; }
        return false;
    }

    /// <summary>Removes inline "[F7]" / "(G13)" chord tokens from lyric text and returns them.</summary>
    public static string StripInlineChords(string text, List<string> chords)
    {
        if (text.IndexOfAny(new[] { '[', '(' }) < 0) return text;
        return InlineBracket().Replace(text, m =>
        {
            string inner = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (!IsChordName(inner)) return m.Value;
            chords.Add(inner);
            return "";
        });
    }

    private static readonly string[] SharpNames = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };

    /// <summary>Display name of a pitch class (0 = C).</summary>
    public static string PitchClassName(int pc) => SharpNames[((pc % 12) + 12) % 12];

    private static readonly string[] ManufacturerChordTypes =
    {
        "", "6", "maj7", "maj7#11", "add9", "maj9", "6/9", "aug", "m", "m6", "m7", "m7b5", "madd9", "m9", "m11",
        "mMaj7", "mMaj9", "dim", "dim7", "7", "7sus4", "7b5", "9", "7#11", "13", "7b9", "7b13", "7#9", "maj7#5",
        "7#5", "", "5", "sus4", "sus2",
    };

    /// <summary>
    /// Decodes the manufacturer-specific chord meta (FF 7F: 43 7B 01 cr ct bn bt). Returns null for anything else.
    /// cr/bn: high nibble accidental (3 = natural), low nibble degree 1..7 = C..B; ct/bt: chord type index.
    /// </summary>
    public static string? DecodeManufacturerChord(byte[]? data)
    {
        if (data is not { Length: >= 5 } || data[0] != 0x43 || data[1] != 0x7B || data[2] != 0x01) return null;
        string? root = DecodeRoot(data[3]);
        if (root is null || data[4] >= ManufacturerChordTypes.Length) return null;
        string name = root + ManufacturerChordTypes[data[4]];
        if (data.Length >= 6 && data[5] != 0x7F)
        {
            string? bass = DecodeRoot(data[5]);
            if (bass is not null && bass != root) name += "/" + bass;
        }
        return name;
    }

    private static string? DecodeRoot(byte b)
    {
        int degree = b & 0x0F, acc = b >> 4;
        if (degree is < 1 or > 7 || acc > 6) return null;
        string letter = "CDEFGAB"[degree - 1].ToString();
        return acc switch
        {
            0 => letter + "bbb", 1 => letter + "bb", 2 => letter + "b", 3 => letter,
            4 => letter + "#", 5 => letter + "##", _ => letter + "###",
        };
    }
}
