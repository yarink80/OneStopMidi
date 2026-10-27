using System.Windows.Input;
using OneStopMidi.App.Input;
using Xunit;

namespace OneStopMidi.App.Tests;

public class KeyMapTests
{
    private static AppAction ActionFor(KeyMap map, string chord)
    {
        Assert.True(map.TryGetAction(KeyChord.Parse(chord), out var a), $"{chord} is not bound");
        return a;
    }

    [Theory]
    [InlineData("Enter", AppAction.LoadAndPlaySelectedSong)]
    [InlineData("NumPadEnter", AppAction.Stop)]
    [InlineData("Space", AppAction.StartStop)]
    [InlineData("Up", AppAction.SelectPreviousSong)]
    [InlineData("Down", AppAction.SelectNextSong)]
    [InlineData("Left", AppAction.PreviousBar)]
    [InlineData("Right", AppAction.NextBar)]
    [InlineData("Ctrl+Left", AppAction.PreviousSong)]
    [InlineData("Ctrl+Right", AppAction.NextSong)]
    [InlineData("PageUp", AppAction.PreviousPlaylist)]
    [InlineData("PageDown", AppAction.NextPlaylist)]
    [InlineData("A", AppAction.ToggleAutoplay)]
    [InlineData("C", AppAction.ToggleLoop)]
    [InlineData("Esc", AppAction.ToggleLoop)]
    [InlineData("J", AppAction.TogglePlaylistMode)]
    [InlineData("K", AppAction.ToggleLyrics)]
    [InlineData("Ctrl+M", AppAction.ToggleClick)]
    [InlineData("Ctrl+Shift+N", AppAction.NewPlaylist)]
    [InlineData("Ctrl+O", AppAction.OpenFile)]
    [InlineData("Ctrl+R", AppAction.RevertToSaved)]
    [InlineData("Ctrl+S", AppAction.SaveFile)]
    [InlineData("Ctrl+Shift+S", AppAction.SaveFileAs)]
    [InlineData("NumPad+", AppAction.TempoUp)]
    [InlineData("NumPad-", AppAction.TempoDown)]
    [InlineData("NumPad.", AppAction.PauseContinue)]
    [InlineData("NumPad0", AppAction.Play)]
    [InlineData("M", AppAction.Play)]
    [InlineData("Z", AppAction.SetLeftLocator)]
    [InlineData("Shift+Z", AppAction.ResetLeftLocator)]
    [InlineData("X", AppAction.SetRightLocator)]
    [InlineData("Shift+X", AppAction.ResetRightLocator)]
    [InlineData("Ctrl+Z", AppAction.Undo)]
    [InlineData("Ctrl+Y", AppAction.Redo)]
    [InlineData("Ctrl+Backspace", AppAction.DeleteRange)]
    [InlineData("Ctrl+E", AppAction.EraseMutedChannels)]
    [InlineData("Delete", AppAction.DeleteListItem)]
    [InlineData("Backspace", AppAction.DeleteListItem)]
    [InlineData("Ctrl+,", AppAction.OpenSettings)]
    [InlineData("Ctrl+F", AppAction.SearchPlaylists)]
    public void Defaults_MatchSpec(string chord, AppAction expected)
    {
        Assert.Equal(expected, ActionFor(KeyMap.CreateDefault(), chord));
    }

    [Fact]
    public void Defaults_HaveNoDuplicateChords_AndSeveralChordsPerAction()
    {
        var map = KeyMap.CreateDefault();
        var all = map.AllBindings().Select(b => b.Chord).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(2, map.GetChords(AppAction.ToggleLoop).Count);
        Assert.Equal(2, map.GetChords(AppAction.Play).Count);
    }

    [Fact]
    public void Add_ReportsConflict_AndLeavesOwnerUntouched()
    {
        var map = KeyMap.CreateDefault();
        var space = KeyChord.Parse("Space");
        Assert.Equal(KeyBindResult.Conflict, map.Add(AppAction.Stop, space));
        Assert.Equal(AppAction.StartStop, map.FindConflict(space, AppAction.Stop));
        Assert.Equal(AppAction.StartStop, ActionFor(map, "Space"));
        Assert.Null(map.FindConflict(space, AppAction.StartStop));
    }

    [Fact]
    public void Add_SameChordTwice_IsAlreadyBound_AndInvalidIsRejected()
    {
        var map = KeyMap.CreateDefault();
        Assert.Equal(KeyBindResult.AlreadyBound, map.Add(AppAction.StartStop, KeyChord.Parse("Space")));
        Assert.Equal(KeyBindResult.Invalid, map.Add(AppAction.Stop, new KeyChord(Key.LeftShift, ModifierKeys.Shift)));
    }

    [Fact]
    public void Add_FreeChord_AddsSecondGesture()
    {
        var map = KeyMap.CreateDefault();
        Assert.Equal(KeyBindResult.Added, map.Add(AppAction.Stop, KeyChord.Parse("Ctrl+Shift+F9")));
        Assert.Equal(2, map.GetChords(AppAction.Stop).Count);
        Assert.Equal(AppAction.Stop, ActionFor(map, "Ctrl+Shift+F9"));
    }

    [Fact]
    public void Reassign_StealsChordFromOtherAction()
    {
        var map = KeyMap.CreateDefault();
        map.Reassign(AppAction.Stop, KeyChord.Parse("Space"));
        Assert.Equal(AppAction.Stop, ActionFor(map, "Space"));
        Assert.Empty(map.GetChords(AppAction.StartStop));
        Assert.Contains(KeyChord.NumPadEnter, map.GetChords(AppAction.Stop));
    }

    [Fact]
    public void MainEnter_And_NumPadEnter_CanBeBoundSeparately()
    {
        var map = KeyMap.CreateDefault();
        map.Reassign(AppAction.PauseContinue, new KeyChord(Key.Enter));
        Assert.Equal(AppAction.PauseContinue, ActionFor(map, "Enter"));
        Assert.Equal(AppAction.Stop, ActionFor(map, "NumPadEnter"));
    }

    [Fact]
    public void Remove_And_ResetToDefaults()
    {
        var map = KeyMap.CreateDefault();
        Assert.True(map.Remove(AppAction.ToggleLoop, KeyChord.Parse("Esc")));
        Assert.False(map.TryGetAction(KeyChord.Parse("Esc"), out _));
        Assert.False(map.Remove(AppAction.ToggleLoop, KeyChord.Parse("Esc")));
        map.ClearAction(AppAction.StartStop);
        map.ResetToDefaults();
        Assert.Equal(AppAction.ToggleLoop, ActionFor(map, "Esc"));
        Assert.Equal(AppAction.StartStop, ActionFor(map, "Space"));
    }

    [Fact]
    public void ResetAction_RestoresItsDefaults_StealingIfNeeded()
    {
        var map = KeyMap.CreateDefault();
        map.Reassign(AppAction.Stop, KeyChord.Parse("Space"));
        map.ResetAction(AppAction.StartStop);
        Assert.Equal(AppAction.StartStop, ActionFor(map, "Space"));
    }

    [Fact]
    public void FootPedalPreset_RebindsArrowsEnterSpace()
    {
        var map = KeyMap.CreatePreset(KeyMap.FootPedalPresetName);
        Assert.Equal(AppAction.PreviousSong, ActionFor(map, "Left"));
        Assert.Equal(AppAction.NextSong, ActionFor(map, "Right"));
        Assert.Equal(AppAction.TempoUp, ActionFor(map, "Up"));
        Assert.Equal(AppAction.TempoDown, ActionFor(map, "Down"));
        Assert.Equal(AppAction.PauseContinue, ActionFor(map, "Enter"));
        Assert.Equal(AppAction.Stop, ActionFor(map, "Space"));
        // Untouched defaults survive.
        Assert.Equal(AppAction.Stop, ActionFor(map, "NumPadEnter"));
        Assert.Equal(AppAction.TempoUp, ActionFor(map, "NumPad+"));
        Assert.Equal(AppAction.PreviousSong, ActionFor(map, "Ctrl+Left"));
        // Each chord still has exactly one owner.
        var all = map.AllBindings().Select(b => b.Chord).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void ApplyPreset_ReplacesBindings_AndRaisesChanged()
    {
        var map = KeyMap.CreateDefault();
        var raised = 0;
        map.Changed += (_, _) => raised++;
        map.ApplyPreset(KeyMap.FootPedalPresetName);
        Assert.True(raised > 0);
        Assert.Equal(AppAction.PreviousSong, ActionFor(map, "Left"));
        map.ApplyPreset(KeyMap.DefaultPresetName);
        Assert.Equal(AppAction.PreviousBar, ActionFor(map, "Left"));
        map.ApplyPreset("no such preset");
        Assert.Equal(AppAction.PreviousBar, ActionFor(map, "Left"));
    }

    [Fact]
    public void Json_RoundTrip_PreservesEveryBinding()
    {
        var map = KeyMap.CreatePreset(KeyMap.FootPedalPresetName);
        map.Add(AppAction.About, KeyChord.Parse("Ctrl+Alt+NumPadEnter"));
        map.ClearAction(AppAction.ToggleLyrics);
        var json = map.ToJson();
        var back = KeyMap.FromJson(json);
        Assert.Equal(
            map.AllBindings().OrderBy(b => b.Action).ThenBy(b => b.Chord.Format()).ToList(),
            back.AllBindings().OrderBy(b => b.Action).ThenBy(b => b.Chord.Format()).ToList());
        Assert.Empty(back.GetChords(AppAction.ToggleLyrics)); // an explicit empty list stays unbound
        Assert.Equal(AppAction.About, ActionFor(back, "Ctrl+Alt+NumPadEnter"));
    }

    [Fact]
    public void Dictionary_RoundTrip_ViaSettingsShape()
    {
        var map = KeyMap.CreateDefault();
        map.Reassign(AppAction.Play, KeyChord.Parse("Enter"));
        var back = KeyMap.FromDictionary(map.ToDictionary());
        Assert.Equal(AppAction.Play, ActionFor(back, "Enter"));
        Assert.Empty(back.GetChords(AppAction.LoadAndPlaySelectedSong));
    }

    [Fact]
    public void FromDictionary_IgnoresUnknownActionsAndBadChords_AndFillsMissingActionsWithDefaults()
    {
        var data = new Dictionary<string, List<string>>
        {
            ["Stop"] = ["Space", "not a key", "NumPadEnter"],
            ["NoSuchAction"] = ["F9"],
            ["StartStop"] = ["Space"], // duplicate chord: first owner wins
        };
        var map = KeyMap.FromDictionary(data);
        Assert.Equal(AppAction.Stop, ActionFor(map, "Space"));
        Assert.Equal(AppAction.Stop, ActionFor(map, "NumPadEnter"));
        Assert.Empty(map.GetChords(AppAction.StartStop));
        Assert.False(map.TryGetAction(KeyChord.Parse("F9"), out _));
        // Missing actions get their defaults.
        Assert.Equal(AppAction.LoadAndPlaySelectedSong, ActionFor(map, "Enter"));
        Assert.Equal(AppAction.SelectPreviousSong, ActionFor(map, "Up"));
    }

    [Fact]
    public void FromDictionary_Null_GivesDefaults()
    {
        var map = KeyMap.FromDictionary(null);
        Assert.Equal(AppAction.StartStop, ActionFor(map, "Space"));
    }

    [Fact]
    public void FromJson_AcceptsBareDictionary_AndRejectsGarbage()
    {
        var map = KeyMap.FromJson("""{ "Stop": ["Space"], }""");
        Assert.Equal(AppAction.Stop, ActionFor(map, "Space"));
        Assert.ThrowsAny<Exception>(() => KeyMap.FromJson("not json"));
        Assert.ThrowsAny<Exception>(() => KeyMap.FromJson("[1,2,3]"));
        Assert.ThrowsAny<Exception>(() => KeyMap.FromJson("{}"));
    }

    [Fact]
    public void ExportImport_File()
    {
        using var dir = new TempDir();
        var map = KeyMap.CreatePreset(KeyMap.FootPedalPresetName);
        var file = dir.File("keys.json");
        map.Export(file);
        var back = KeyMap.Import(file);
        Assert.Equal(AppAction.PreviousSong, ActionFor(back, "Left"));
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var map = KeyMap.CreateDefault();
        var clone = map.Clone();
        clone.Reassign(AppAction.Stop, KeyChord.Parse("Space"));
        Assert.Equal(AppAction.StartStop, ActionFor(map, "Space"));
        Assert.Equal(AppAction.Stop, ActionFor(clone, "Space"));
    }

    [Fact]
    public void EveryAction_HasDisplayMetadata()
    {
        foreach (var a in AppActionInfo.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(AppActionInfo.GetName(a)));
            Assert.NotEqual("Other", AppActionInfo.GetCategory(a)); // "Other" means the action has no metadata entry
        }
    }
}
