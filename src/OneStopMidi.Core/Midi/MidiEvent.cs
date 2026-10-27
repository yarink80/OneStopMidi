namespace OneStopMidi.Core.Midi;

/// <summary>
/// One immutable MIDI file event at an absolute tick.
/// Channel messages use <see cref="Status"/> 0x80-0xEF with <see cref="Data1"/>/<see cref="Data2"/>;
/// sysex uses status 0xF0 (payload after F0, normally ending in F7) or 0xF7 (escaped raw bytes);
/// meta events use status 0xFF with <see cref="MetaType"/> and <see cref="Data"/>.
/// </summary>
public sealed record MidiEvent(long Tick, byte Status, byte Data1 = 0, byte Data2 = 0, byte MetaType = 0, byte[]? Data = null)
{
    public const byte MetaSequenceNumber = 0x00;
    public const byte MetaText = 0x01;
    public const byte MetaCopyright = 0x02;
    public const byte MetaTrackName = 0x03;
    public const byte MetaInstrumentName = 0x04;
    public const byte MetaLyric = 0x05;
    public const byte MetaMarker = 0x06;
    public const byte MetaCuePoint = 0x07;
    public const byte MetaChannelPrefix = 0x20;
    public const byte MetaPortPrefix = 0x21;
    public const byte MetaEndOfTrack = 0x2F;
    public const byte MetaTempo = 0x51;
    public const byte MetaSmpteOffset = 0x54;
    public const byte MetaTimeSignature = 0x58;
    public const byte MetaKeySignature = 0x59;
    public const byte MetaSequencerSpecific = 0x7F;

    public static MidiEvent ChannelMessage(long tick, int status, int data1, int data2 = 0) =>
        new(tick, (byte)status, (byte)(data1 & 0x7F), (byte)(data2 & 0x7F));

    public static MidiEvent NoteOn(long tick, int channel, int note, int velocity) =>
        ChannelMessage(tick, 0x90 | (channel & 0x0F), note, velocity);

    public static MidiEvent NoteOff(long tick, int channel, int note, int velocity = 0) =>
        ChannelMessage(tick, 0x80 | (channel & 0x0F), note, velocity);

    public static MidiEvent ControlChange(long tick, int channel, int controller, int value) =>
        ChannelMessage(tick, 0xB0 | (channel & 0x0F), controller, value);

    public static MidiEvent ProgramChange(long tick, int channel, int program) =>
        ChannelMessage(tick, 0xC0 | (channel & 0x0F), program);

    public static MidiEvent Meta(long tick, byte type, byte[] data) => new(tick, 0xFF, MetaType: type, Data: data);

    public static MidiEvent SysEx(long tick, byte status, byte[] data) => new(tick, status, Data: data);

    /// <summary>Tempo meta event from microseconds per quarter note.</summary>
    public static MidiEvent Tempo(long tick, int microsecondsPerQuarter) =>
        Meta(tick, MetaTempo, new[] { (byte)(microsecondsPerQuarter >> 16), (byte)(microsecondsPerQuarter >> 8), (byte)microsecondsPerQuarter });

    public static MidiEvent TempoBpm(long tick, double bpm) => Tempo(tick, (int)Math.Round(60_000_000.0 / bpm));

    /// <summary>Time signature meta event; denominator must be a power of two.</summary>
    public static MidiEvent TimeSignature(long tick, int numerator, int denominator)
    {
        int pow = 0;
        while ((1 << pow) < denominator && pow < 7) pow++;
        return Meta(tick, MetaTimeSignature, new[] { (byte)numerator, (byte)pow, (byte)24, (byte)8 });
    }

    public bool IsChannelMessage => Status is >= 0x80 and < 0xF0;
    public bool IsMeta => Status == 0xFF;
    public bool IsSysEx => Status is 0xF0 or 0xF7;
    public int Channel => Status & 0x0F;
    public int Command => Status & 0xF0;
    public bool IsNoteOn => Command == 0x90 && Data2 > 0 && IsChannelMessage;
    public bool IsNoteOff => IsChannelMessage && (Command == 0x80 || (Command == 0x90 && Data2 == 0));
    public bool IsTextMeta => IsMeta && MetaType is >= 0x01 and <= 0x0F;

    /// <summary>Tempo in microseconds per quarter for a tempo meta event, else null.</summary>
    public int? TempoMicroseconds =>
        IsMeta && MetaType == MetaTempo && Data is { Length: >= 3 } d ? (d[0] << 16) | (d[1] << 8) | d[2] : null;

    public override string ToString() =>
        IsMeta ? $"{Tick}: Meta {MetaType:X2} [{Data?.Length ?? 0}]"
        : IsSysEx ? $"{Tick}: SysEx {Status:X2} [{Data?.Length ?? 0}]"
        : $"{Tick}: {Status:X2} {Data1} {Data2}";
}
