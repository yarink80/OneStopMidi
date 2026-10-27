namespace OneStopMidi.Core.Midi;

/// <summary>
/// Standard MIDI File reader. Supports formats 0/1/2, running status, sysex (F0/F7), meta events,
/// the RIFF "RMID" wrapper, and tolerates truncated or slightly corrupt files.
/// </summary>
public static class SmfReader
{
    public static SmfFile ReadFile(string path) => Read(File.ReadAllBytes(path));

    public static SmfFile Read(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Read(ms.ToArray());
    }

    /// <exception cref="InvalidDataException">No MIDI header found.</exception>
    public static SmfFile Read(byte[] data)
    {
        int pos = LocateHeader(data);
        if (pos < 0) throw new InvalidDataException("Not a Standard MIDI File (no MThd header).");

        var file = new SmfFile();
        uint headerLen = ReadUInt32(data, pos + 4);
        if (pos + 8 + 6 > data.Length) throw new InvalidDataException("MIDI header is truncated.");
        int format = ReadUInt16(data, pos + 8);
        int ntrks = ReadUInt16(data, pos + 10);
        int division = ReadUInt16(data, pos + 12);
        file.Format = format is >= 0 and <= 2 ? format : 1;
        if ((division & 0x8000) != 0)
        {
            int fps = -(sbyte)(division >> 8);
            int tpf = division & 0xFF;
            if (fps == 29) fps = 30;
            // Treat SMPTE time as a fixed 120 BPM grid: one quarter = 0.5 s.
            file.Ppq = Math.Max(1, fps * tpf / 2);
            file.ReadIssues.Add("SMPTE time division converted to PPQ at 120 BPM.");
        }
        else
        {
            file.Ppq = division == 0 ? 480 : division;
        }

        pos += 8 + (int)Math.Min(headerLen, (uint)(data.Length - pos - 8));
        while (pos + 8 <= data.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            long len = ReadUInt32(data, pos + 4);
            int start = pos + 8;
            long available = data.Length - start;
            if (len > available)
            {
                file.ReadIssues.Add($"Chunk {id} is truncated ({len} bytes declared, {available} available).");
                len = available;
            }
            if (id == "MTrk")
            {
                file.Tracks.Add(ReadTrack(data, start, (int)len, file.ReadIssues, file.Tracks.Count));
            }
            else if (!IsPrintable(data, pos, 4))
            {
                // Garbage instead of a chunk header: stop rather than misinterpret the rest.
                file.ReadIssues.Add("Unexpected data after the last track.");
                break;
            }
            pos = start + (int)len;
        }

        if (file.Tracks.Count != ntrks)
            file.ReadIssues.Add($"Header declares {ntrks} tracks, found {file.Tracks.Count}.");
        if (file.Tracks.Count == 0) file.Tracks.Add(new MidiTrack());
        if (file.Format == 0 && file.Tracks.Count > 1) file.Format = 1;
        return file;
    }

    private static int LocateHeader(byte[] data)
    {
        // RIFF RMID: "RIFF" size "RMID" then chunks; the "data" chunk holds the SMF.
        if (data.Length >= 12 && Match(data, 0, "RIFF") && Match(data, 8, "RMID"))
        {
            int p = 12;
            while (p + 8 <= data.Length)
            {
                uint size = BitConverter.ToUInt32(data, p + 4); // RIFF sizes are little-endian
                if (Match(data, p, "data") && p + 8 + 4 <= data.Length && Match(data, p + 8, "MThd")) return p + 8;
                p += 8 + (int)Math.Min(size + (size & 1), (uint)(data.Length - p - 8));
            }
        }
        // Tolerate a little junk before the header (some files have leading bytes).
        int limit = Math.Min(data.Length - 4, 4096);
        for (int i = 0; i <= limit; i++)
            if (Match(data, i, "MThd")) return i;
        return -1;
    }

    private static MidiTrack ReadTrack(byte[] d, int start, int length, List<string> issues, int index)
    {
        var track = new MidiTrack();
        int p = start, end = start + length;
        long tick = 0;
        int running = 0;
        bool sawEnd = false;
        try
        {
            while (p < end)
            {
                if (!TryReadVlq(d, ref p, end, out int delta)) break;
                tick += delta;
                if (p >= end) break;
                int b = d[p];
                int status;
                if (b >= 0x80) { status = b; p++; }
                else if (running != 0) status = running;
                else { p++; issues.Add($"Track {index}: data byte without status at offset {p - 1}."); continue; }

                if (status < 0xF0)
                {
                    running = status;
                    int cmd = status & 0xF0;
                    int n = cmd is 0xC0 or 0xD0 ? 1 : 2;
                    if (p + n > end) break;
                    byte d1 = (byte)(d[p] & 0x7F);
                    byte d2 = n == 2 ? (byte)(d[p + 1] & 0x7F) : (byte)0;
                    p += n;
                    track.Events.Add(new MidiEvent(tick, (byte)status, d1, d2));
                }
                else if (status == 0xFF)
                {
                    if (p >= end) break;
                    byte type = d[p++];
                    if (!TryReadVlq(d, ref p, end, out int len)) break;
                    len = Math.Min(len, end - p);
                    var payload = d.AsSpan(p, len).ToArray();
                    p += len;
                    if (type == MidiEvent.MetaEndOfTrack) { sawEnd = true; track.EndTick = tick; break; }
                    track.Events.Add(MidiEvent.Meta(tick, type, payload));
                }
                else if (status is 0xF0 or 0xF7)
                {
                    if (!TryReadVlq(d, ref p, end, out int len)) break;
                    len = Math.Min(len, end - p);
                    var payload = d.AsSpan(p, len).ToArray();
                    p += len;
                    track.Events.Add(MidiEvent.SysEx(tick, (byte)status, payload));
                }
                else
                {
                    // System common / realtime bytes are not valid in files; skip their data bytes.
                    int skip = status switch { 0xF1 or 0xF3 => 1, 0xF2 => 2, _ => 0 };
                    p += skip;
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            // Defensive: treat as truncation.
        }
        if (!sawEnd)
        {
            issues.Add($"Track {index}: missing end-of-track (file may be truncated).");
            track.EndTick = tick;
        }
        track.EndTick = Math.Max(track.EndTick, track.LastEventTick);
        return track;
    }

    private static bool TryReadVlq(byte[] d, ref int p, int end, out int value)
    {
        value = 0;
        for (int i = 0; i < 4; i++)
        {
            if (p >= end) return false;
            byte b = d[p++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) return true;
        }
        return true; // over-long quantity: accept what we have
    }

    private static bool Match(byte[] d, int p, string s)
    {
        if (p < 0 || p + s.Length > d.Length) return false;
        for (int i = 0; i < s.Length; i++) if (d[p + i] != s[i]) return false;
        return true;
    }

    private static bool IsPrintable(byte[] d, int p, int n)
    {
        for (int i = 0; i < n; i++) if (d[p + i] < 0x20 || d[p + i] > 0x7E) return false;
        return true;
    }

    private static uint ReadUInt32(byte[] d, int p) =>
        p + 4 > d.Length ? 0 : (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]);

    private static int ReadUInt16(byte[] d, int p) => p + 2 > d.Length ? 0 : d[p] << 8 | d[p + 1];
}
