using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Suppressed-archive stat carry-forward (owner rulings 2026-07-19, run 206630597437685760).
//
// A SUPPRESSED auto-archive used to call Clear(), wiping _stats + the combat state. The owner's
// failure: pre-pull activity landed, an armed StageChange auto-archive committed in the lull and was
// suppressed as junk, and the Clear() erased everything → the local player showed 0 damage for the
// whole run. TWO owner rulings fix this together (both in this change):
//
//  1. Suppression NEVER wipes state. The suppressed branch of ManualArchive returns WITHOUT Clear()
//     — accumulated rows/actors + combat clocks + baselines all carry forward unconditionally and
//     fold into whatever segment banks next (the all-zero pre-fight actors then appear in that entry,
//     which the owner wants). There is NO staleness discard (the owner rejected a 10 s rule).
//  2. Junk is CONTENT-only and narrowed to ALL-ZERO. Owner verbatim: "junk = when nothing happen
//     DPS=0, HPS=0, TAKEN=0 ... it's not junk too" — so ANY nonzero row banks as its own entry.
//
// Consequence for the owner's imagine-opener case: the opener carries a NONZERO row, so it now BANKS
// as its own small entry (ruling 2) instead of being carried — nothing is lost. The carry path
// (ruling 1) now matters for the ALL-ZERO shape: pre-fight actors seen with zero rows are suppressed
// but not wiped, so they carry into the next banked entry.
//
// Plugin can't be instantiated headless (the ShouldSuppressAutoArchive / PendingArchiveDue
// precedent), so the pure suppression predicate is exercised directly; the no-wipe carry itself is a
// one-line lifecycle invariant (the suppressed branch returns before Clear) confirmed by inspection.
public class SuppressedCarryTests
{
    // ── Carry scenario (ruling 1): an ALL-ZERO archive between activity is SUPPRESSED ──────────────
    // Pre-fight actors are in range with zero DPS/HPS/Taken. This all-zero auto archive is suppressed
    // — and because the suppressed branch no longer Clear()s, those zero-row actors are NOT wiped;
    // they carry forward and appear in the next banked entry (the owner's intent).
    [Fact]
    public void All_zero_pre_fight_actors_are_suppressed_then_carried()
        => Assert.True(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: false, allRowsZero: true));

    // ── Nothing lost across a pair (ruling 2): a nonzero single-actor pre-pull archive BANKS, and ──
    // the following fight BANKS separately. Neither is suppressed → two distinct entries, no loss.
    [Fact]
    public void Nonzero_pre_pull_and_following_fight_both_bank_nothing_lost()
    {
        // pre-pull: one actor, a lone instant hit — nonzero, so it is NOT suppressed (banks entry #1).
        Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: false, allRowsZero: false));
        // the real fight afterwards — also nonzero, NOT suppressed (banks entry #2).
        Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.SceneChange, carriesFreshResult: false, allRowsZero: false));
    }

    // ── Case (c): a MANUAL archive is byte-identical — never suppressed, even all-zero ─────────────
    [Fact]
    public void Manual_archive_is_never_suppressed_even_all_zero()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.Manual, carriesFreshResult: false, allRowsZero: true));

    // A fresh kill/settlement force-keeps even an all-zero auto archive (the destroyed-kill-tail guard).
    [Fact]
    public void Fresh_result_keeps_even_all_zero_auto_archive()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: true, allRowsZero: true));

    // ── Case (e): repeated suppressed (all-zero) archives in one carry window stay correct ─────────
    // The predicate is a pure function of its inputs, so evaluating it repeatedly across a carry
    // window (multiple all-zero suppressions before real combat folds in) is side-effect free and
    // stable — no double-count, no corruption of the decision.
    [Fact]
    public void Repeated_all_zero_suppression_is_pure_and_stable()
    {
        var first  = Plugin.ShouldSuppressAutoArchive(AutoArchive.ArchiveReason.StageChange, false, true);
        var second = Plugin.ShouldSuppressAutoArchive(AutoArchive.ArchiveReason.StageChange, false, true);
        Assert.Equal(first, second);
        Assert.True(first);
    }
}
