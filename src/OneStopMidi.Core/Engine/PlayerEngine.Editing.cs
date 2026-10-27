namespace OneStopMidi.Core;

public sealed partial class PlayerEngine
{
    private const int MaxUndo = 100;

    private readonly LinkedList<SongSnapshot> _undo = new();
    private readonly Stack<SongSnapshot> _redo = new();
    private SongClip? _clipboard;

    public bool CanUndo
    {
        get { lock (_sync) return _undo.Count > 0; }
    }

    public bool CanRedo
    {
        get { lock (_sync) return _redo.Count > 0; }
    }

    public bool CanPaste
    {
        get { lock (_sync) return _clipboard is not null && _song is not null; }
    }

    /// <summary>Range used by cut/copy/delete; empty when the right locator is not after the left one.</summary>
    private (long From, long To) Selection() => (_leftLocator, Math.Min(_rightLocator, _song?.LengthTicks ?? 0));

    /// <summary>Runs an edit; <paramref name="op"/> returns false when nothing changed (no undo entry).</summary>
    private void Edit(Func<MidiSong, bool> op) => Run(() =>
    {
        if (_song is null) return;
        var before = _song.CaptureSnapshot();
        if (!op(_song)) return;
        _undo.AddLast(before);
        if (_undo.Count > MaxUndo) _undo.RemoveFirst();
        _redo.Clear();
        AfterSongEdit();
    });

    /// <summary>Re-derives playback data after the song changed, keeping the position.</summary>
    private void AfterSongEdit()
    {
        if (_song is null) return;
        bool playing = _state == PlaybackState.Playing;
        double pos = CurrentTick();
        if (playing) SilenceAll();
        _events = _song.Events.ToArray();
        RebuildTempoMap();
        pos = Math.Clamp(pos, 0, _song.LengthTicks);
        _leftLocator = Math.Clamp(_leftLocator, 0, _song.LengthTicks);
        _rightLocator = Math.Clamp(_rightLocator, 0, _song.LengthTicks);
        if (playing) StartAt(pos, _clock.Seconds, chase: true);
        else
        {
            _position = pos;
            _nextIndex = LowerBound((long)Math.Ceiling(pos));
        }
        MarkAllChannelsChanged();
        QueueSongChanged();
        QueuePositionChanged();
    }

    public void Undo() => Run(() =>
    {
        if (_song is null || _undo.Count == 0) return;
        _redo.Push(_song.CaptureSnapshot());
        var s = _undo.Last!.Value;
        _undo.RemoveLast();
        _song.RestoreSnapshot(s);
        AfterSongEdit();
    });

    public void Redo() => Run(() =>
    {
        if (_song is null || _redo.Count == 0) return;
        _undo.AddLast(_song.CaptureSnapshot());
        _song.RestoreSnapshot(_redo.Pop());
        AfterSongEdit();
    });

    public void CutSelection() => Edit(song =>
    {
        var (from, to) = Selection();
        if (to <= from) return false;
        _clipboard = song.CopyRange(from, to);
        song.DeleteRange(from, to);
        return true;
    });

    public void CopySelection() => Run(() =>
    {
        if (_song is null) return;
        var (from, to) = Selection();
        if (to > from) _clipboard = _song.CopyRange(from, to);
    });

    public void Paste()
    {
        long at = PositionTicks;
        Edit(song =>
        {
            if (_clipboard is null || (_clipboard.Length == 0 && _clipboard.EventCount == 0)) return false;
            song.InsertClip(_clipboard, at);
            return true;
        });
    }

    public void DeleteSelection() => Edit(song =>
    {
        var (from, to) = Selection();
        if (to <= from) return false;
        song.DeleteRange(from, to);
        return true;
    });

    public void EraseMutedChannels() => Edit(song => song.EraseChannels(_muted) > 0);

    public void SetSequenceName(string name) => Edit(song =>
    {
        if (song.SequenceName == name) return false;
        song.SetSequenceName(name);
        return true;
    });

    /// <exception cref="IOException">The file cannot be written.</exception>
    public void Save(string path, int? format = null)
    {
        Exception? error = null;
        Run(() =>
        {
            if (_song is null) return;
            int oldFormat = _song.Format;
            try { _song.Save(path, format, _leftLocator, _rightLocator); }
            catch (Exception ex) { error = ex; return; }
            if (_song.Format != oldFormat) AfterSongEdit();
            else QueueSongChanged();
        });
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    public void RevertToSaved()
    {
        string? path;
        SongLoadOptions? options;
        lock (_sync)
        {
            path = _song?.FilePath;
            options = _loadOptions;
        }
        if (path is null || !File.Exists(path)) return;
        var song = MidiSong.Load(path, options);
        Run(() => LoadCore(song, options));
    }
}
