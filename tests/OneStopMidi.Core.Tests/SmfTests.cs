using OneStopMidi.Core.Midi;
using Xunit;

namespace OneStopMidi.Core.Tests;

public class SmfTests
{
    private static SongBuilder RichSong()
    {
        var b = new SongBuilder(ppq: 96);
        b.Track().Name("My Song").Tempo(0, 100).TimeSig(0, 3, 4).Tempo(288, 140)
            .Add(MidiEvent.SysEx(0, 0xF0, new byte[] { 0x7E, 0x7F, 0x09, 0x01, 0xF7 }))
            .Add(MidiEvent.SysEx(10, 0xF7, new byte[] { 0xF0, 0x41, 0xF7 }))
            .End(400);
        b.Track().Name("Piano").Program(0, 0, 5).CC(0, 0, 7, 90)
            .Note(0, 60, 0, 48).Note(0, 64, 48, 48)
            .Add(MidiEvent.ChannelMessage(60, 0xE0, 0, 0x40))
            .Add(MidiEvent.ChannelMessage(61, 0xD0, 33));
        b.Track().Name("Drums").Note(9, 36, 0, 10).Note(9, 38, 96, 10).End(500);
        return b;
    }

    [Fact]
    public void RoundTrip_PreservesEventsAndStructure()
    {
        var original = RichSong().ToSmf();
        var bytes = SmfWriter.Write(original);
        var read = SmfReader.Read(bytes);

        Assert.Equal(1, read.Format);
        Assert.Equal(96, read.Ppq);
        Assert.Equal(3, read.Tracks.Count);
        Assert.Empty(read.ReadIssues);
        for (int t = 0; t < 3; t++)
        {
            var a = original.Tracks[t].Events;
            var b = read.Tracks[t].Events;
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].Tick, b[i].Tick);
                Assert.Equal(a[i].Status, b[i].Status);
                Assert.Equal(a[i].Data1, b[i].Data1);
                Assert.Equal(a[i].Data2, b[i].Data2);
                Assert.Equal(a[i].MetaType, b[i].MetaType);
                Assert.Equal(a[i].Data ?? Array.Empty<byte>(), b[i].Data ?? Array.Empty<byte>());
            }
            Assert.Equal(original.Tracks[t].EndTick, read.Tracks[t].EndTick);
        }
        // Writing again yields identical bytes.
        Assert.Equal(bytes, SmfWriter.Write(read));
    }

    [Fact]
    public void Reader_HandlesRunningStatus()
    {
        byte[] data =
        {
            (byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, 0, 0, 1, 0, 96,
            (byte)'M', (byte)'T', (byte)'r', (byte)'k', 0, 0, 0, 18,
            0x00, 0x90, 0x3C, 0x64,   // note on
            0x60, 0x3C, 0x00,         // running status: note on vel 0 (= off)
            0x00, 0x40, 0x50,         // running status: note on 64
            0x81, 0x00, 0x40, 0x00,   // delta 128 (VLQ), running status off
            0x00, 0xFF, 0x2F, 0x00,
        };
        var file = SmfReader.Read(data);
        var ev = file.Tracks[0].Events;
        Assert.Equal(4, ev.Count);
        Assert.Equal((0L, 60, true), (ev[0].Tick, (int)ev[0].Data1, ev[0].IsNoteOn));
        Assert.Equal((96L, 60, true), (ev[1].Tick, (int)ev[1].Data1, ev[1].IsNoteOff));
        Assert.Equal((96L, 64, true), (ev[2].Tick, (int)ev[2].Data1, ev[2].IsNoteOn));
        Assert.Equal((224L, 64, true), (ev[3].Tick, (int)ev[3].Data1, ev[3].IsNoteOff));
        Assert.Empty(file.ReadIssues);
    }

    [Fact]
    public void Writer_UsesRunningStatus()
    {
        var b = new SongBuilder(format: 0);
        var t = b.Track();
        for (int i = 0; i < 10; i++)
            t.Add(MidiEvent.NoteOn(i * 10, 0, 60 + i, 100)).Add(MidiEvent.NoteOn(i * 10 + 5, 0, 60 + i, 0));
        var bytes = b.Build();
        // header 14 + chunk header 8 + 20 events: first 4 bytes, rest 3 bytes (delta + 2 data), + EOT 4
        Assert.Equal(14 + 8 + 4 + 19 * 3 + 4, bytes.Length);
    }

    [Fact]
    public void Reader_ToleratesTruncatedFile()
    {
        var bytes = RichSong().Build();
        var cut = bytes.AsSpan(0, bytes.Length - 20).ToArray();
        var file = SmfReader.Read(cut);
        Assert.Equal(3, file.Tracks.Count);
        Assert.NotEmpty(file.ReadIssues);
        Assert.True(file.Tracks[2].Events.Count >= 1);

        // Missing whole tracks too.
        var file2 = SmfReader.Read(bytes.AsSpan(0, 60).ToArray());
        Assert.True(file2.Tracks.Count >= 1);
        // And the song model still loads.
        var song = MidiSong.FromBytes(cut);
        Assert.True(song.LengthTicks > 0);
    }

    [Fact]
    public void Reader_AcceptsRiffRmidWrapper()
    {
        var smf = RichSong().Build();
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(System.Text.Encoding.ASCII.GetBytes(s));
        W("RIFF");
        ms.Write(BitConverter.GetBytes(4 + 8 + smf.Length));
        W("RMID");
        W("data");
        ms.Write(BitConverter.GetBytes(smf.Length));
        ms.Write(smf);
        var file = SmfReader.Read(ms.ToArray());
        Assert.Equal(3, file.Tracks.Count);
        Assert.Equal(96, file.Ppq);
    }

    [Fact]
    public void Reader_RejectsNonMidi()
    {
        Assert.Throws<InvalidDataException>(() => SmfReader.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Fact]
    public void Save_Format0_And_Format1_Conversion()
    {
        var song = RichSong().Song();
        var dir = Directory.CreateTempSubdirectory("onestopmidi");
        try
        {
            string p0 = Path.Combine(dir.FullName, "f0.mid");
            int eventCount = song.Events.Count;
            song.Save(p0, format: 0);
            var f0 = SmfReader.ReadFile(p0);
            Assert.Equal(0, f0.Format);
            Assert.Single(f0.Tracks);
            var s0 = MidiSong.Load(p0);
            Assert.Equal(song.Events.Count(e => e.IsChannelMessage), s0.Events.Count(e => e.IsChannelMessage));
            Assert.Equal("My Song", s0.SequenceName);
            Assert.Equal("Piano", s0.ChannelTrackNames[0]);
            Assert.Equal("Drums", s0.ChannelTrackNames[9]);
            Assert.True(eventCount <= s0.Events.Count); // channel prefix metas may be added

            string p1 = Path.Combine(dir.FullName, "f1.mid");
            s0.Save(p1, format: 1);
            var s1 = MidiSong.Load(p1);
            Assert.Equal(1, s1.Format);
            Assert.Equal(3, s1.TrackCount); // conductor + channel 0 + channel 9
            Assert.Equal("Piano", s1.ChannelTrackNames[0]);
            Assert.Equal("Drums", s1.ChannelTrackNames[9]);
            Assert.Equal(song.Events.Count(e => e.IsChannelMessage), s1.Events.Count(e => e.IsChannelMessage));
            Assert.Equal(song.LengthTicks, s1.LengthTicks);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Save_StoresLocatorsInSequencerSpecificMeta()
    {
        var song = RichSong().Song();
        var path = Path.Combine(Path.GetTempPath(), $"onestopmidi-{Guid.NewGuid():N}.mid");
        try
        {
            song.Save(path, null, 96, 288);
            Assert.False(song.IsModified);
            var raw = SmfReader.ReadFile(path);
            Assert.Contains(raw.Tracks[0].Events, e => e.IsMeta && e.MetaType == 0x7F);
            var reloaded = MidiSong.Load(path);
            Assert.Equal(96, reloaded.StoredLeftLocator);
            Assert.Equal(288, reloaded.StoredRightLocator);
            // The locator meta is not kept as a song event (no duplication on re-save).
            Assert.DoesNotContain(reloaded.Events, e => e.IsMeta && e.MetaType == 0x7F);
        }
        finally { File.Delete(path); }
    }
}
