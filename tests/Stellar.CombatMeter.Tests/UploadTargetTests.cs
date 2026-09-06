// Tests for the testing-channel-only "upload target" setting (owner 2026-09-06, rDPS P2 follow-up):
// "release on the test channel that make user i ask can use it and upload to dev" + "make sure that
// settings only appear when release in testing channel". Three independent pieces, all pure /
// services-free so none of them need a full Plugin instance (mirrors LogUploadTests/
// MeterElementTogglesTests' own FakeConfigSection double):
//   1. Plugin.ChannelOf — the build-time StellarChannel assembly-metadata reader.
//   2. Plugin.ResolveUploadTargetIsTesting / ApplyUploadTargetPreference — the "uploadApiBase" pref
//      round-trip the settings toggle drives.
//   3. Plugin.BuildUploadTargetSection — the settings-column section builder, gated on an injectable
//      Func<bool> (pinned directly here rather than via a live Plugin instance — no test in this
//      suite constructs one; see Plugin.UploadTarget.cs's own doc comment).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class UploadTargetTests
{
    // Mirrors LogUploadTests/MeterElementTogglesTests' own FakeConfigSection double.
    private sealed class FakeConfigSection : IConfigSection
    {
        private readonly Dictionary<string, object?> _store = new();
        public T? Get<T>(string key, T? defaultValue)
            => _store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => _store[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
        public void RemoveByPrefix(string prefix)
        {
            foreach (var key in _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _store.Remove(key);
        }
    }

    // -------------------------------------------------------------------------
    // 1. ChannelOf — pure assembly-metadata reader.
    // -------------------------------------------------------------------------

    [Fact]
    public void ChannelOf_defaults_to_stable_with_no_attributes()
        => Assert.Equal("stable", Plugin.ChannelOf(Array.Empty<AssemblyMetadataAttribute>()));

    [Fact]
    public void ChannelOf_defaults_to_stable_when_only_unrelated_metadata_present()
        => Assert.Equal("stable", Plugin.ChannelOf(new[]
        {
            new AssemblyMetadataAttribute("RepositoryUrl", "https://example.com"),
            new AssemblyMetadataAttribute("SomethingElse", "value"),
        }));

    [Fact]
    public void ChannelOf_reads_testing_when_stamped()
        => Assert.Equal("testing", Plugin.ChannelOf(new[]
        {
            new AssemblyMetadataAttribute("StellarChannel", "testing"),
        }));

    [Fact]
    public void ChannelOf_reads_stable_when_explicitly_stamped_stable()
        => Assert.Equal("stable", Plugin.ChannelOf(new[]
        {
            new AssemblyMetadataAttribute("StellarChannel", "stable"),
        }));

    [Fact]
    public void ChannelOf_falls_back_to_stable_when_value_is_null()
        => Assert.Equal("stable", Plugin.ChannelOf(new[]
        {
            new AssemblyMetadataAttribute("StellarChannel", null),
        }));

    // The referenced Stellar.CombatMeter.dll under THIS build (a plain `dotnet test`, no
    // `-p:StellarChannel=testing`) must itself resolve to the stable channel — proves the csproj's
    // conditional default actually reaches the compiled assembly's own metadata, not just the pure
    // helper in isolation.
    [Fact]
    public void IsTestingChannelBuild_is_false_under_a_plain_test_build()
        => Assert.False(Plugin.IsTestingChannelBuild);

    // -------------------------------------------------------------------------
    // 2. The "uploadApiBase" pref round-trip the toggle drives.
    // -------------------------------------------------------------------------

    [Fact]
    public void UploadTargetIsTesting_defaults_false_when_pref_unset()
        => Assert.False(Plugin.ResolveUploadTargetIsTesting(new FakeConfigSection()));

    [Fact]
    public void ApplyUploadTargetPreference_true_writes_exactly_the_testing_base()
    {
        var prefs = new FakeConfigSection();
        Plugin.ApplyUploadTargetPreference(prefs, true);
        Assert.Equal(Plugin.TestingUploadApiBase, prefs.Get("uploadApiBase", ""));
        Assert.True(Plugin.ResolveUploadTargetIsTesting(prefs));
    }

    [Fact]
    public void ApplyUploadTargetPreference_false_writes_empty_string()
    {
        var prefs = new FakeConfigSection();
        Plugin.ApplyUploadTargetPreference(prefs, true);   // start ON …
        Plugin.ApplyUploadTargetPreference(prefs, false);  // … then turn OFF
        Assert.Equal("", prefs.Get("uploadApiBase", "unset-sentinel"));
        Assert.False(Plugin.ResolveUploadTargetIsTesting(prefs));
    }

    [Fact]
    public void TestingUploadApiBase_is_the_dev_host()
        => Assert.Equal("https://api.dev.stellarresonance.app", Plugin.TestingUploadApiBase);

    // -------------------------------------------------------------------------
    // 3. BuildUploadTargetSection — the settings-column section, gated on an injectable Func<bool>.
    // -------------------------------------------------------------------------

    [Fact]
    public void Section_When_reflects_the_injected_gate_true()
    {
        var el = (ConditionalElement)Plugin.BuildUploadTargetSection(
            () => true, () => "section", () => "toggle", () => false, _ => { }, () => "hint");
        Assert.True(el.When());
    }

    [Fact]
    public void Section_When_reflects_the_injected_gate_false()
    {
        var el = (ConditionalElement)Plugin.BuildUploadTargetSection(
            () => false, () => "section", () => "toggle", () => false, _ => { }, () => "hint");
        Assert.False(el.When());
    }

    // A stable build's gate returns false — the settings column must render NOTHING for this block.
    // The Else branch is pinned as a zero-height spacer regardless of the gate value (both subtrees
    // are always built; only When() decides which one is shown — see HudElement.cs's own doc comment
    // on ConditionalElement).
    [Fact]
    public void Section_Else_branch_is_a_zero_height_spacer()
    {
        var el = (ConditionalElement)Plugin.BuildUploadTargetSection(
            () => false, () => "section", () => "toggle", () => false, _ => { }, () => "hint");
        var spacer = Assert.IsType<SpacerElement>(el.Else);
        Assert.Equal(0f, spacer.Height);
        Assert.Equal(0f, spacer.Width);
    }

    // The Then branch carries the section label, the toggle row (bound to the supplied get/set), and
    // the hint text — built regardless of the gate (the gate only controls visibility via When()).
    [Fact]
    public void Section_Then_branch_contains_the_toggle_bound_to_the_supplied_state()
    {
        var toggleState = false;
        var el = (ConditionalElement)Plugin.BuildUploadTargetSection(
            () => true, () => "Upload target", () => "Upload to testing",
            () => toggleState, v => toggleState = v, () => "hint text");

        var column = Assert.IsType<ColumnElement>(el.Then);
        Assert.Equal(4, column.Children.Count);
        Assert.IsType<SeparatorElement>(column.Children[0]);

        var label = Assert.IsType<TextElement>(column.Children[1]);
        Assert.True(label.Emphasis);
        Assert.Equal("Upload target", label.Text());

        var row = Assert.IsType<RowElement>(column.Children[2]);
        var toggle = Assert.IsType<ToggleElement>(row.Children[0]);
        var toggleLabel = Assert.IsType<TextElement>(row.Children[1]);
        Assert.Equal("Upload to testing", toggleLabel.Text());

        Assert.False(toggle.Get());
        toggle.Set(true);
        Assert.True(toggleState);   // Set delegate reached the caller-supplied state
        Assert.True(toggle.Get());  // Get delegate reflects it back live

        var hint = Assert.IsType<TextElement>(column.Children[3]);
        Assert.Equal("hint text", hint.Text());
    }

    // Distinguishes the hint copy from the post-save relaunch copy by whichever Func<string> the
    // caller supplies for the hint slot — Plugin.UploadTargetSection() (the instance wrapper) is what
    // actually switches between "settings.uploadTarget.hint" and "…relaunch" on
    // _uploadTargetPendingRelaunch; this pins that the builder just renders whatever it is given.
    [Fact]
    public void Section_Then_hint_text_reflects_whatever_the_caller_supplies()
    {
        var pending = false;
        var el = (ConditionalElement)Plugin.BuildUploadTargetSection(
            () => true, () => "s", () => "t", () => false, _ => { },
            () => pending ? "relaunch copy" : "hint copy");

        var column = Assert.IsType<ColumnElement>(el.Then);
        var hint = Assert.IsType<TextElement>(column.Children[3]);
        Assert.Equal("hint copy", hint.Text());

        pending = true;
        Assert.Equal("relaunch copy", hint.Text());   // Func re-pulled live, not baked at build time
    }
}
