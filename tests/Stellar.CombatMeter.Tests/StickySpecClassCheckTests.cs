using Xunit;

namespace Stellar.CombatMeter.Tests;

// Spec upload design 2026-09-26 item 2: the sticky last-known spec cache (Plugin.EntitySnapshotSticky.cs) must never
// label a player with a spec of a class they are no longer playing. Origin: the owner-confirmed Miyuki swap
// (docs/recon/spec-from-public-wire-data.md § Live test) — Marksman/Falconry → Beat Performer. The framework's
// live spec is 0 for a moment after the class swap (no root buff of the new class yet), and the cache used to
// answer "Falconry" for a Beat Performer. A cached spec is served only when spec / 10000 == the entity's current
// PLAYABLE class; otherwise 0 and the row falls back to the class name.
public class StickySpecClassCheckTests
{
    const int Falconry = 110002, Concerto = 130002;

    [Fact]
    public void Falconry_cached_but_now_Beat_Performer_serves_no_spec()
        => Assert.Equal(0, Plugin.ResolveStickySpec(liveSpec: 0, cached: Falconry, currentClass: 13));

    [Fact]
    public void Cached_spec_of_the_current_class_is_served()
        => Assert.Equal(Falconry, Plugin.ResolveStickySpec(liveSpec: 0, cached: Falconry, currentClass: 11));

    [Fact]
    public void Unknown_current_class_serves_no_cached_spec()
        => Assert.Equal(0, Plugin.ResolveStickySpec(liveSpec: 0, cached: Falconry, currentClass: 0));

    // A Battle Imagine transform id (14 Lucy, 15 Natsu, 8 …) is not a class (owner ruling 2026-09-25) and never
    // validates a cached spec — the caller resolves the real class before asking.
    [Fact]
    public void A_transform_id_is_not_a_class_and_serves_no_cached_spec()
        => Assert.Equal(0, Plugin.ResolveStickySpec(liveSpec: 0, cached: Falconry, currentClass: 14));

    // The live framework value is unchanged by this rule: it is served as-is (and refreshes the cache).
    [Fact]
    public void A_live_spec_is_always_served()
        => Assert.Equal(Concerto, Plugin.ResolveStickySpec(liveSpec: Concerto, cached: Falconry, currentClass: 11));

    [Fact]
    public void Nothing_cached_serves_zero()
        => Assert.Equal(0, Plugin.ResolveStickySpec(liveSpec: 0, cached: 0, currentClass: 11));

    // Archive freeze: the snapshot's attr 220 is frozen when the snapshot first populates, so a player who swapped
    // Marksman → Beat Performer later in the encounter still carries attr 220 = 11. The class timeline's LAST
    // playable class is the one they were playing at the archive (a trailing transform span is skipped).
    [Fact]
    public void FrozenCurrentClass_prefers_the_class_timelines_last_playable_class()
    {
        var swapped = new EntitySnapshot
        {
            AttrIds = new[] { 220 }, AttrValues = new long[] { 11 },
            ClassSpanProf = new long[] { 11, 13, 14 }, ClassSpanStart = new long[] { 0, 10, 20 }, ClassSpanEnd = new long[] { 10, 20, 30 },
        };
        Assert.Equal(13, Plugin.FrozenCurrentClass(swapped));
        Assert.Equal(0, Plugin.ResolveStickySpec(0, Falconry, Plugin.FrozenCurrentClass(swapped)));

        var single = new EntitySnapshot { AttrIds = new[] { 220 }, AttrValues = new long[] { 11 } };
        Assert.Equal(11, Plugin.FrozenCurrentClass(single));
    }

    // QA fix round finding 2: a party member whose snapshot carries NO playable class (never an attr 220, and a class
    // timeline holding only a Battle Imagine transform) must not lose a valid cached spec at archive — the archive
    // falls back to the LIVE class resolution (roster / attr / last shown class), resolved lazily.
    [Fact]
    public void Archive_class_falls_back_to_live_resolution_when_the_snapshot_has_no_playable_class()
    {
        var transformedOnly = new EntitySnapshot
        {
            ClassSpanProf = new long[] { 14 }, ClassSpanStart = new long[] { 0 }, ClassSpanEnd = new long[] { 10 },
        };
        Assert.Equal(0, Plugin.FrozenCurrentClass(transformedOnly));
        var liveCalls = 0;
        var cls = Plugin.ArchiveClass(Plugin.FrozenCurrentClass(transformedOnly), () => { liveCalls++; return 11; });
        Assert.Equal(11, cls);
        Assert.Equal(1, liveCalls);
        Assert.Equal(Falconry, Plugin.ResolveStickySpec(0, Falconry, cls));

        // A frozen playable class wins and the live read is skipped entirely.
        Assert.Equal(13, Plugin.ArchiveClass(13, () => { liveCalls++; return 11; }));
        Assert.Equal(1, liveCalls);
    }
}
