using OneStopMidi.App.Common;

namespace OneStopMidi.App.ViewModels;

/// <summary>One row in the song list.</summary>
public sealed class FileItem : ObservableObject
{
    private bool _failed;
    private bool _isCurrent;
    private bool _missing;

    public FileItem(string path)
    {
        Path = path;
        _missing = !File.Exists(path);
    }

    public string Path { get; }
    public string DisplayName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>File does not exist.</summary>
    public bool Missing
    {
        get => _missing;
        set
        {
            if (Set(ref _missing, value)) Raise(nameof(IsBad));
        }
    }

    /// <summary>The engine could not load the file.</summary>
    public bool Failed
    {
        get => _failed;
        set
        {
            if (Set(ref _failed, value)) Raise(nameof(IsBad));
        }
    }

    /// <summary>Shown in red.</summary>
    public bool IsBad => _failed || _missing;

    /// <summary>The song currently loaded in the player.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    public string ToolTip => _missing ? $"{Path}\n(file not found)" : _failed ? $"{Path}\n(could not be loaded)" : Path;

    public void RefreshTooltip() => Raise(nameof(ToolTip));
}

public enum PlaylistKind { Recent, Stored, Temporary }

/// <summary>A node of the playlist tree.</summary>
public sealed class PlaylistRef
{
    public PlaylistRef(PlaylistKind kind, string name, List<string>? entries = null)
    {
        Kind = kind;
        Name = name;
        TempEntries = entries ?? [];
    }

    public PlaylistKind Kind { get; }
    public string Name { get; set; }
    /// <summary>In-memory entries of a temporary list (command line / external file).</summary>
    public List<string> TempEntries { get; }
    public bool IsEditable => Kind != PlaylistKind.Recent;

    public bool SameAs(PlaylistRef? other) =>
        other is not null && other.Kind == Kind && string.Equals(other.Name, Name, StringComparison.OrdinalIgnoreCase);
}
