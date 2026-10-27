namespace OneStopMidi.Core.Midi;

/// <summary>Standard MIDI File writer (uses running status for channel messages).</summary>
public static class SmfWriter
{
    public static void WriteFile(string path, SmfFile file)
    {
        var bytes = Write(file);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    public static byte[] Write(SmfFile file)
    {
        using var ms = new MemoryStream();
        ms.Write("MThd"u8);
        WriteUInt32(ms, 6);
        WriteUInt16(ms, file.Format);
        WriteUInt16(ms, file.Tracks.Count);
        WriteUInt16(ms, Math.Clamp(file.Ppq, 1, 0x7FFF));
        foreach (var track in file.Tracks)
        {
            var body = WriteTrackBody(track);
            ms.Write("MTrk"u8);
            WriteUInt32(ms, (uint)body.Length);
            ms.Write(body);
        }
        return ms.ToArray();
    }

    private static byte[] WriteTrackBody(MidiTrack track)
    {
        using var ms = new MemoryStream();
        long last = 0;
        int running = 0;
        foreach (var e in track.Events.OrderBy(e => e.Tick)) // OrderBy is stable
        {
            if (e.IsMeta && e.MetaType == MidiEvent.MetaEndOfTrack) continue;
            WriteVlq(ms, (int)Math.Max(0, e.Tick - last));
            last = Math.Max(last, e.Tick);
            if (e.IsChannelMessage)
            {
                if (e.Status != running) { ms.WriteByte(e.Status); running = e.Status; }
                ms.WriteByte((byte)(e.Data1 & 0x7F));
                if (e.Command is not (0xC0 or 0xD0)) ms.WriteByte((byte)(e.Data2 & 0x7F));
            }
            else if (e.IsMeta)
            {
                running = 0;
                var data = e.Data ?? Array.Empty<byte>();
                ms.WriteByte(0xFF);
                ms.WriteByte(e.MetaType);
                WriteVlq(ms, data.Length);
                ms.Write(data);
            }
            else if (e.IsSysEx)
            {
                running = 0;
                var data = e.Data ?? Array.Empty<byte>();
                ms.WriteByte(e.Status);
                WriteVlq(ms, data.Length);
                ms.Write(data);
            }
        }
        WriteVlq(ms, (int)Math.Max(0, track.EndTick - last));
        ms.Write(new byte[] { 0xFF, 0x2F, 0x00 });
        return ms.ToArray();
    }

    internal static void WriteVlq(Stream s, int value)
    {
        value &= 0x0FFFFFFF;
        Span<byte> buf = stackalloc byte[4];
        int n = 0;
        buf[n++] = (byte)(value & 0x7F);
        while ((value >>= 7) != 0) buf[n++] = (byte)((value & 0x7F) | 0x80);
        for (int i = n - 1; i >= 0; i--) s.WriteByte(buf[i]);
    }

    private static void WriteUInt32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static void WriteUInt16(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }
}
