using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// The difficulty gate as the AUTO path actually applies it — <see cref="Plugin.TierAllows"/>, the same
/// static the plugin's instance overload binds, so these exercise shipped code rather than a restatement
/// of the rule.
///
/// <para>Why this file exists: the tier model landed in <c>d24aeb7</c> with 29 pins and was still wired to
/// NOTHING — <c>TierAllowsUpload</c> was called from nowhere, so the filter did nothing at all. Every pin
/// passed. Coverage of a rule is not coverage of its use.</para>
///
/// <para>PINNED, do not weaken: the gate is UPLOAD-ONLY and AUTO-ONLY. <c>d24aeb7</c>: "a disabled tier
/// never blocks a local archive, so nothing is destroyed and a hand push still works." A tier block must
/// therefore retain the record and leave the manual path open.</para>
/// </summary>
public class TierGateTests
{
    // Verbatim shape of GET /api/site/content-kinds (payload v2) with the real live ids and tags:
    // 1151 hard dungeon, 1153 master dungeon, 13002 "Brutal!" raid served as hard, 13021 "Clash!" as
    // normal, 7152 world boss (untiered), 9999 classified but absent from `tiers`.
    private const string Payload =
        "{\"version\":2,\"kinds\":{\"dungeon\":[1151,1153,9999],\"raid\":[13002,13021],"
        + "\"worldboss\":[7152],\"vault\":[],\"other\":[]},"
        + "\"tiers\":{\"1151\":\"hard\",\"1153\":\"master\",\"13002\":\"hard\",\"13021\":\"normal\"}}";

    private static ContentKindMap Kinds()
    {
        Assert.True(ContentKindMap.TryParse(Payload, out var m));
        return m;
    }

    private static ContentTierMap Tiers()
    {
        Assert.True(ContentTierMap.TryParse(Payload, out var m));
        return m;
    }

    private static Plugin.EncounterHistoryEntry Entry(int mapId, int masterLevel = 0)
        => new() { SceneName = mapId.ToString(), DifficultyLevel = masterLevel };

    [Fact]
    public void DefaultFilterAllowsEveryRun()
    {
        // An upgrade must not change what uploads — asserted through the GATE, not just the filter.
        var f = new UploadTierFilter();
        foreach (var id in new[] { 1151, 1153, 13002, 13021, 7152, 9999 })
            Assert.True(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(id)), $"mapId {id}");
    }

    [Fact]
    public void ADisabledTierBlocksTheAutoSendForThatRun()
    {
        var f = new UploadTierFilter();
        f.SetTierEnabled(ContentKind.Dungeon, ContentTier.Hard, false);

        Assert.False(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(1151)));   // hard dungeon
        Assert.True(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(1153)));    // master dungeon untouched
    }

    // THE BUG THIS GATE EXPOSED (2026-07-30). Raid 13002 is "Brutal! Floating Island" and the site tags
    // it "hard" — the same tag a hard dungeon carries. With a tier-only key, turning off the DUNGEON hard
    // chip silently stopped this raid auto-uploading. Verify through the gate, with the real payload.
    [Fact]
    public void ADisabledDungeonTierDoesNotBlockABrutalRaid()
    {
        var f = new UploadTierFilter();
        f.SetTierEnabled(ContentKind.Dungeon, ContentTier.Hard, false);

        Assert.False(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(1151)));    // dungeon hard: blocked
        Assert.True(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(13002)));    // Brutal! raid: allowed
    }

    [Fact]
    public void DisablingARaidChipBlocksThatRaidOnly()
    {
        var f = new UploadTierFilter();
        f.SetTierEnabled(ContentKind.Raid, ContentTier.Hard, false);          // "Brutal!"

        Assert.False(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(13002)));   // Brutal!
        Assert.True(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(13021)));    // Clash! untouched
        Assert.True(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(1151)));     // hard dungeon untouched
    }

    // FAIL OPEN. A classified map id the site serves no tier for, and an untiered kind, must both upload
    // even with every chip off — withholding a run because a classifier lacked an entry loses data.
    [Fact]
    public void UntieredAndUnclassifiedRunsAlwaysUpload()
    {
        var f = new UploadTierFilter();
        foreach (var kind in UploadTierFilter.TiersFor.Keys)
        foreach (var tier in UploadTierFilter.TiersFor[kind])
            f.SetTierEnabled(kind, tier, false);

        Assert.True(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(9999)));   // dungeon, no tier served
        Assert.True(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(7152)));   // world boss: no axis
    }

    [Fact]
    public void AnEmptyTierMapUploadsEverything()
    {
        // The plugin has never reached the endpoint. Nothing may be withheld.
        var f = new UploadTierFilter();
        f.SetTierEnabled(ContentKind.Dungeon, ContentTier.Hard, false);
        Assert.True(Plugin.TierAllows(Kinds(), ContentTierMap.Empty, f, Entry(1151)));
    }

    [Fact]
    public void TheMasterFloorBlocksOnlyMasterRunsBelowIt()
    {
        var f = new UploadTierFilter { MinMasterLevel = 10 };

        Assert.False(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(1153, masterLevel: 6)));
        Assert.True(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(1153, masterLevel: 18)));
        Assert.True(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(1153, masterLevel: 0)));  // unknown: open
        Assert.True(Plugin.TierAllows(Kinds(), Tiers(), f, Entry(1151, masterLevel: 0)));  // hard: unaffected
    }

    // UPLOAD-ONLY / AUTO-ONLY. The per-kind cell governs the trigger; the tier gate is a separate
    // predicate the auto path adds. A hand push must stay possible for a tier-blocked run, so the manual
    // decision — UploadPolicy.Allows(state, Manual) — must not consult the tier filter at all.
    [Fact]
    public void ATierBlockedRunCanStillBePushedByHand()
    {
        var f = new UploadTierFilter();
        f.SetTierEnabled(ContentKind.Dungeon, ContentTier.Hard, false);
        var entry = Entry(1151);

        // auto: refused by the tier gate
        Assert.False(Plugin.TierAllows(Kinds(), Tiers(), f, entry));

        // manual: the cell still permits it, and the tier gate is not part of that decision
        var policy = new UploadPolicyTable();   // defaults: auto
        var state = Plugin.EffectivePolicy(Kinds(), policy, entry, UploadArtifact.Stats);
        Assert.True(UploadPolicy.Allows(state, UploadTrigger.Manual));
    }

    // Composition: an auto send needs BOTH. Neither predicate alone is the gate.
    [Fact]
    public void AnAutoSendRequiresTheCellAndTheTier()
    {
        var entry = Entry(1151);
        var kinds = Kinds();
        var tiers = Tiers();

        var cellOnly = new UploadPolicyTable();                       // auto
        var tierOff = new UploadTierFilter();
        tierOff.SetTierEnabled(ContentKind.Dungeon, ContentTier.Hard, false);

        var cellState = Plugin.EffectivePolicy(kinds, cellOnly, entry, UploadArtifact.Stats);
        Assert.True(UploadPolicy.Allows(cellState, UploadTrigger.Auto));            // cell says yes
        Assert.False(Plugin.TierAllows(kinds, tiers, tierOff, entry));              // tier says no
        // => the auto path must not send. Both halves are required; see Plugin.MaybeUploadLog.

        var cellOff = new UploadPolicyTable();
        cellOff[ContentKind.Dungeon, UploadArtifact.Stats] = UploadPolicyState.Off;
        var tierOn = new UploadTierFilter();
        Assert.False(UploadPolicy.Allows(
            Plugin.EffectivePolicy(kinds, cellOff, entry, UploadArtifact.Stats), UploadTrigger.Auto));
        Assert.True(Plugin.TierAllows(kinds, tiers, tierOn, entry));
    }
}
