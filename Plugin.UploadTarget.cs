using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Testing-channel-only "upload target" setting (owner 2026-09-06: "release on the test channel that
// make user i ask can use it and upload to dev" + "make sure that settings only appear when release
// in testing channel"). The plugin registry's CI builds a separate testing-channel target with
// `-p:StellarChannel=testing` (Stellar.CombatMeter.csproj); every other lane (CI's default build,
// local `dotnet build`, the published stable release) leaves it unset, which the csproj defaults to
// "stable". This file reads that build-time stamp back at runtime and, ONLY on a testing-channel
// build, exposes a settings toggle that points THIS install's uploads at the dev/testing server
// instead of production — so the owner can hand a testing build to a tester who then uploads onto
// logs.dev.stellarresonance.app without hand-editing config. A stable build renders NOTHING here;
// the toggle does not exist for normal players.
//
// Scope: this toggle only ever writes the EXISTING "uploadApiBase" pref (Plugin.UploadApiBase.cs) —
// the same one knob that already routes every upload artifact. It never touches LogUploader.ApiBase
// directly: that base is resolved ONCE at construction (InitUploadApiBase), by design (a split
// dataset mid-session is worse than either target), so a toggle flip here takes effect at the NEXT
// launch, not immediately — the settings copy says so.
public sealed partial class Plugin
{
    /// <summary>Dev/testing upload backend a testing-channel build's toggle points at. Distinct from
    /// the staging override (Plugin.UploadApiBase.cs's owner-only "uploadApiBase" free-text pref) —
    /// this is the one sanctioned value the settings TOGGLE ever writes.</summary>
    internal const string TestingUploadApiBase = "https://api.dev.stellarresonance.app";

    private static bool? _isTestingChannelBuildCache;

    /// <summary>Whether THIS assembly was built with <c>-p:StellarChannel=testing</c> (the plugin
    /// registry's testing-channel CI target). Computed once from the assembly's own embedded metadata
    /// and cached — the channel a compiled DLL carries cannot change at runtime.</summary>
    internal static bool IsTestingChannelBuild
        => _isTestingChannelBuildCache ??=
            ChannelOf(typeof(Plugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()) == "testing";

    /// <summary>Pure: the <c>StellarChannel</c> assembly-metadata value carried by <paramref name="attrs"/>,
    /// or <c>"stable"</c> when absent (no <c>StellarChannel</c> entry at all, a plain `dotnet build`, or
    /// an assembly whose metadata never mentions it — e.g. unrelated <see cref="AssemblyMetadataAttribute"/>
    /// entries). Never throws; never returns null.</summary>
    internal static string ChannelOf(IEnumerable<AssemblyMetadataAttribute> attrs)
        => attrs.FirstOrDefault(a => a.Key == "StellarChannel")?.Value ?? "stable";

    /// <summary>Whether <paramref name="prefs"/>' "uploadApiBase" pref currently points at
    /// <see cref="TestingUploadApiBase"/>. Free function over <see cref="IConfigSection"/> (mirrors
    /// <see cref="MeterElementToggles.Save"/>/<c>Load</c>) so the pref round-trip is testable without
    /// constructing a <see cref="Plugin"/>.</summary>
    internal static bool ResolveUploadTargetIsTesting(IConfigSection prefs)
        => prefs.Get(PrefUploadApiBase, "") == TestingUploadApiBase;

    /// <summary>Writes <paramref name="on"/> to <paramref name="prefs"/>' "uploadApiBase" pref —
    /// <see cref="TestingUploadApiBase"/> when true, empty string (production, the default) when
    /// false. Does NOT flush to disk (caller calls <see cref="IConfigSection.Save"/>) and does NOT
    /// touch <see cref="LogUploader.SetApiBase"/> — see the file header for why the change is
    /// next-launch-only by design.</summary>
    internal static void ApplyUploadTargetPreference(IConfigSection prefs, bool on)
        => prefs.Set(PrefUploadApiBase, on ? TestingUploadApiBase : "");

    // Injectable so a test can force the gate without depending on THIS test assembly's own build
    // stamp; defaults to the real build-time gate for production use.
    private Func<bool> _isTestingBuildGate = () => IsTestingChannelBuild;

    // Set once a toggle flip is saved; flips the settings hint copy from "here's what this does" to
    // "saved, relaunch to apply" for the rest of the session (never cleared — a relaunch resets it
    // for free by rebuilding the Plugin).
    private bool _uploadTargetPendingRelaunch;

    private bool UploadTargetIsTesting => ResolveUploadTargetIsTesting(_prefs);

    private void SetUploadTargetTesting(bool on)
    {
        ApplyUploadTargetPreference(_prefs, on);
        _prefs.Save();
        _uploadTargetPendingRelaunch = true;
        _services.Log.Info(on
            ? "[CombatMeter] upload target set to TESTING (dev) — takes effect after relaunch"
            : "[CombatMeter] upload target set to PRODUCTION — takes effect after relaunch");
    }

    // The Settings-window section (Plugin.SettingsArchive.cs's BuildAutoArchiveSettingsRoot places it
    // between the Uploads section and the Discord webhook). Pure builder over explicit Func delegates — no `_loc`/
    // `_prefs`/`_services` reads inside it — so BuildUploadTargetSection is testable standalone with
    // the gate, labels and toggle state all supplied by the caller.
    private HudElement UploadTargetSection()
        => BuildUploadTargetSection(
            _isTestingBuildGate,
            () => _loc.T("settings.section.uploadTarget"),
            () => _loc.T("settings.toggle.uploadTesting"),
            () => UploadTargetIsTesting,
            v => SetUploadTargetTesting(v),
            () => _loc.T(_uploadTargetPendingRelaunch ? "settings.uploadTarget.relaunch" : "settings.uploadTarget.hint"));

    /// <summary>Builds the ConditionalElement gating the whole upload-target section on
    /// <paramref name="isTestingBuild"/>. <c>Then</c> = separator + emphasised section header (the
    /// Settings window's section idiom) + toggle row + hint/relaunch text; <c>Else</c> = a zero-height
    /// spacer, so a stable build renders nothing here (not even the separator).</summary>
    internal static HudElement BuildUploadTargetSection(
        Func<bool> isTestingBuild, Func<string> sectionLabel, Func<string> toggleLabel,
        Func<bool> getToggle, Action<bool> setToggle, Func<string> hintText)
        => new ConditionalElement(
            isTestingBuild,
            new ColumnElement(new HudElement[]
            {
                new SeparatorElement(),
                new TextElement(sectionLabel, Emphasis: true),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(() => "", getToggle, setToggle),
                    new TextElement(toggleLabel),
                }, Gap: 8f),
                new TextElement(hintText, MutedColStatic),
            }, Gap: 3f),
            new SpacerElement(Height: 0f));

    // Static twin of the instance MutedCol() (Plugin.Header.cs) — BuildUploadTargetSection is static
    // so it cannot reference the instance method as a Func group.
    private static ColorRgba? MutedColStatic() => new ColorRgba(0.66f, 0.70f, 0.73f, 1f);
}
