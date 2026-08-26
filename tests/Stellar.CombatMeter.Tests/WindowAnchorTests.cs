using Xunit;

namespace Stellar.CombatMeter.Tests;

// P0 walk-in-anchor fix (2026-08-26, owner ground-truth run qCUzbYtTmI / luid 512621771459919872):
// PrepareReplayDoc used to set the uploaded doc's StartMs/EndMs from encounter.StartMs/EndMs (the
// DPS-log's own combat-only span, i.e. entry.EnteredAtMs/ArchivedAtMs) instead of the replay
// window's TRUE bounds (the watermark and upperMs the position/HP tracks are actually sliced to,
// converted back to the SAME absolute server-clock scale entry.EnteredAtMs/ArchivedAtMs already use
// — NOT the small combat-start-zeroed relative numbers individual track Ms0 values carry, which
// reset to a different reference every window and can't double as a cross-window stitching anchor).
// A site that trusts StartMs/EndMs as the window's valid range — a reasonable reading of those field
// names — would clip/hide any track sample whose reconstructed absolute time falls outside them,
// exactly the walk-in-loss / post-teleport-arrival-loss the owner reported. This bug predates every
// commit on this branch AND the released 2.5.0 build (verified via `git log -L` on the
// StartMs=encounter.StartMs line — byte-identical since the walk-in-capture arc's original commit,
// 44ee9c8, 2026-07-01). Plugin.ResolveWindowBounds (Plugin.ReplayWindow.cs) is the fix; these tests
// pin it against the exact shapes from the owner's real histdocs.
public class WindowAnchorTests
{
    // A round, easy-to-eyeball absolute anchor for _replay.CombatStartMs (the run-constant capture
    // start, stamped once at dungeon entry) — real values are ServerNowMs-scale (huge), but only the
    // DIFFERENCES from this anchor matter for these assertions.
    private const int CaptureStart = 1_000_000;

    [Fact]
    public void Window1_ClaimsEntryToBank_WalkinIncluded()
    {
        // Real shape (owner run qCUzbYtTmI, window 1): capture starts at dungeon entry
        // (capture-relative 0 = CaptureStart absolute); the player walks for 14.6s before the first
        // hit; the archive (bank) fires at capture-relative 15_400ms (a 0.8s fight after the 14.6s
        // walk-in).
        const long watermarkMs = -1;   // ReplayWatermarkUnset — nothing banked yet this run
        const long upperMs = 15_400;   // capture-relative "now" at this archive

        var (startMs, endMs) = Plugin.ResolveWindowBounds(CaptureStart, watermarkMs, upperMs);

        // StartMs must claim the WALK-IN, not combat start: the TRUE absolute dungeon-entry time.
        // The pre-fix code emitted StartMs = encounter.StartMs = CaptureStart + 14_600 (combat
        // start) here — 14.6s too late, exactly the walk-in-loss bug.
        Assert.Equal(CaptureStart, startMs);
        Assert.Equal(CaptureStart + 15_400, endMs);   // this archive's own absolute cut
    }

    [Fact]
    public void WindowN_ClaimsPreviousBankToThisBank_TeleportArrivalIncluded()
    {
        // Real shape: window N's previous archive (bank N-1) fired at capture-relative 100_000.
        // Position sampling resumes at bank+5_000 (post-teleport arrival, heal-up movement) but
        // combat for THIS segment doesn't start until bank+27_500 (the "27.5s teleport gap" the
        // owner measured — most of it is loading + heal-up, not yet combat). This segment's own
        // archive (bank N) fires at capture-relative 140_000.
        const long watermarkMs = 100_000;   // the PREVIOUS archive's own cut — window N's true start
        const long upperMs = 140_000;       // capture-relative "now" at THIS archive

        var (startMs, endMs) = Plugin.ResolveWindowBounds(CaptureStart, watermarkMs, upperMs);

        // StartMs must claim bank(N-1) — CaptureStart + 100_000 — NOT this segment's own combat
        // start (CaptureStart + 127_500). The pre-fix code emitted the latter, losing the
        // post-teleport-arrival heal-up movement the owner reported as "unclaimed".
        Assert.Equal(CaptureStart + 100_000, startMs);
        Assert.Equal(CaptureStart + 140_000, endMs);   // this archive's own absolute cut
    }

    [Fact]
    public void EmptyLoadSegment_StillClaimedByTheSurroundingWindow()
    {
        // A window spanning an actual loading screen has ZERO position samples during the load
        // itself ("fine" per the owner — nothing to capture, the game is frozen) — but the window's
        // DECLARED bounds must still span the FULL watermark-to-cut range, never narrowed to "just
        // where samples happen to exist". ResolveWindowBounds never inspects samples at all — only
        // the boundaries — so this holds by construction; this test pins that contract explicitly.
        const long watermarkMs = 50_000;
        const long upperMs = 90_000;   // a 40s window; suppose only the last 10s has real samples

        var (startMs, endMs) = Plugin.ResolveWindowBounds(CaptureStart, watermarkMs, upperMs);

        Assert.Equal(CaptureStart + watermarkMs, startMs);   // the FULL span, not clipped to sampled data
        Assert.Equal(CaptureStart + upperMs, endMs);
    }

    [Theory]
    [InlineData(-1)]     // ReplayWatermarkUnset
    [InlineData(-100)]   // any negative sentinel-shaped value
    public void UnsetOrNegativeWatermark_TreatedAsCaptureStart_NotAsALiteralNegativeOffset(long watermarkMs)
    {
        // A negative watermark ALWAYS means "nothing banked yet" (capture start itself), never a
        // literal negative offset from it — clamped to 0 before adding to captureStartMs.
        const long upperMs = 10_000;

        var (startMs, _) = Plugin.ResolveWindowBounds(CaptureStart, watermarkMs, upperMs);

        Assert.Equal(CaptureStart, startMs);
    }

    [Fact]
    public void AbsoluteScale_MatchesEnteredAtMsArchivedAtMsConvention()
    {
        // Sanity pin on the DESIGN decision itself: StartMs/EndMs must stay ABSOLUTE (server-clock
        // scale), the SAME convention entry.EnteredAtMs/ArchivedAtMs already use elsewhere (so a
        // site can order/stitch multiple windows on one real-world timeline) — never the small,
        // per-window, combat-start-zeroed relative numbers individual track Ms0 values carry.
        var (startMs, endMs) = Plugin.ResolveWindowBounds(CaptureStart, watermarkMs: -1, upperMs: 500);

        // Both results must be in the SAME huge/absolute magnitude as CaptureStart itself, not a
        // small number close to zero (the tell-tale sign of an accidentally-relative result).
        Assert.True(startMs >= CaptureStart);
        Assert.True(endMs > CaptureStart);
    }

    // ── Differential control: the OLD (pre-fix) formula, byte-identical since 44ee9c8 (2026-07-01) ──
    //
    // PrepareReplayDoc used to assign `StartMs = encounter.StartMs` / `EndMs = encounter.EndMs`
    // directly (Plugin.Replay.cs, verified via `git log -L` to be unchanged in this file's ENTIRE
    // history until this fix). `encounter.StartMs`/`EndMs` are `entry.EnteredAtMs`/`ArchivedAtMs`
    // (CombatLogAssembler.BuildEncounter) — i.e. THIS SEGMENT's own combat start/archive-fire time,
    // with no notion of the watermark at all. OldFormula reproduces that exact computation so these
    // tests can assert, in one place, that the bug is real and that ResolveWindowBounds fixes it —
    // never weaken OldFormula to "look like" the fix; it exists to prove the DELTA.
    private static (long StartMs, long EndMs) OldFormula(long combatStartAbsoluteMs, long archivedAtAbsoluteMs)
        => (combatStartAbsoluteMs, archivedAtAbsoluteMs);

    [Fact]
    public void OldFormula_MissesTheWalkin_NewFormulaDoesNot()
    {
        const long combatStartAbsolute = CaptureStart + 14_600;   // entry.EnteredAtMs for window 1
        const long archivedAtAbsolute  = CaptureStart + 15_400;   // entry.ArchivedAtMs for window 1

        var old = OldFormula(combatStartAbsolute, archivedAtAbsolute);
        var (fixedStart, fixedEnd) = Plugin.ResolveWindowBounds(CaptureStart, watermarkMs: -1, upperMs: 15_400);

        // The bug, reproduced: the old formula's StartMs sits 14.6s LATER than the true entry —
        // exactly the missing walk-in duration the owner measured.
        Assert.Equal(CaptureStart + 14_600, old.StartMs);
        Assert.NotEqual(old.StartMs, fixedStart);
        Assert.Equal(14_600, old.StartMs - fixedStart);
        Assert.Equal(fixedEnd, old.EndMs);   // EndMs happens to coincide here — both represent "now"
    }

    [Fact]
    public void OldFormula_MissesThePostTeleportArrival_NewFormulaDoesNot()
    {
        const long combatStartAbsolute = CaptureStart + 127_500;   // entry.EnteredAtMs for window N
        const long archivedAtAbsolute  = CaptureStart + 140_000;   // entry.ArchivedAtMs for window N

        var old = OldFormula(combatStartAbsolute, archivedAtAbsolute);
        var (fixedStart, fixedEnd) = Plugin.ResolveWindowBounds(CaptureStart, watermarkMs: 100_000, upperMs: 140_000);

        // The bug, reproduced: the old formula's StartMs sits 27.5s LATER than bank(N-1) — exactly
        // the missing post-teleport-arrival + heal-up movement the owner measured.
        Assert.Equal(CaptureStart + 127_500, old.StartMs);
        Assert.NotEqual(old.StartMs, fixedStart);
        Assert.Equal(27_500, old.StartMs - fixedStart);
        Assert.Equal(fixedEnd, old.EndMs);
    }
}
