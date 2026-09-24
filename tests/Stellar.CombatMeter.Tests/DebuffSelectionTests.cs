using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class DebuffSelectionTests
{
    private sealed class FakeConfigSection : IConfigSection
    {
        private readonly Dictionary<string, object?> _store = new();
        public T? Get<T>(string key, T? defaultValue)
            => _store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => _store[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
        public void RemoveByPrefix(string prefix) { }
    }

    [Fact]
    public void ShowAllExceptSelected_empty_shows_named_iconed_debuff()
    {
        var sel = new DebuffSelection();
        Assert.Equal(DebuffTrackMode.ShowAllExceptSelected, sel.Mode);
        Assert.True(sel.ShouldShow(10, hasName: true, hasIcon: true));
    }

    [Fact]
    public void ShowAllExceptSelected_hides_the_selected_id_shows_others()
    {
        var sel = new DebuffSelection();
        sel.SetSelected(10, true);
        Assert.False(sel.ShouldShow(10, hasName: true, hasIcon: true));
        Assert.True(sel.ShouldShow(11, hasName: true, hasIcon: true));
    }

    [Fact]
    public void ShowOnlySelected_shows_only_the_selected_id()
    {
        var sel = new DebuffSelection { Mode = DebuffTrackMode.ShowOnlySelected };
        sel.SetSelected(10, true);
        Assert.True(sel.ShouldShow(10, hasName: true, hasIcon: true));
        Assert.False(sel.ShouldShow(11, hasName: true, hasIcon: true));
    }

    [Fact]
    public void ShowOnlySelected_empty_shows_nothing()
    {
        var sel = new DebuffSelection { Mode = DebuffTrackMode.ShowOnlySelected };
        Assert.False(sel.ShouldShow(10, hasName: true, hasIcon: true));
    }

    [Fact]
    public void No_icon_is_always_hidden()
    {
        var sel = new DebuffSelection();
        Assert.False(sel.ShouldShow(10, hasName: true, hasIcon: false));

        sel.ShowHidden = true;
        Assert.False(sel.ShouldShow(10, hasName: false, hasIcon: false));
    }

    [Fact]
    public void No_name_is_hidden_unless_show_hidden()
    {
        var sel = new DebuffSelection();
        Assert.False(sel.ShouldShow(10, hasName: false, hasIcon: true));

        sel.ShowHidden = true;
        Assert.True(sel.ShouldShow(10, hasName: false, hasIcon: true));
    }

    [Fact]
    public void SetSelected_false_removes_a_previously_selected_id()
    {
        var sel = new DebuffSelection();
        sel.SetSelected(10, true);
        Assert.True(sel.IsSelected(10));
        sel.SetSelected(10, false);
        Assert.False(sel.IsSelected(10));
    }

    [Fact]
    public void Load_and_save_round_trip_through_a_config_section()
    {
        var cfg = new FakeConfigSection();
        var sel = new DebuffSelection { Mode = DebuffTrackMode.ShowOnlySelected, ShowHidden = true };
        sel.SetSelected(10, true);
        sel.SetSelected(20, true);
        sel.Save(cfg);

        var loaded = DebuffSelection.Load(cfg);
        Assert.Equal(DebuffTrackMode.ShowOnlySelected, loaded.Mode);
        Assert.True(loaded.ShowHidden);
        Assert.True(loaded.IsSelected(10));
        Assert.True(loaded.IsSelected(20));
        Assert.Equal(2, loaded.Selected.Count);
    }

    [Fact]
    public void Load_from_an_empty_config_section_yields_defaults()
    {
        var loaded = DebuffSelection.Load(new FakeConfigSection());
        Assert.Equal(DebuffTrackMode.ShowAllExceptSelected, loaded.Mode);
        Assert.False(loaded.ShowHidden);
        Assert.Empty(loaded.Selected);
    }
}
