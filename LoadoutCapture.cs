// Split out of Plugin.LoadoutCapture.cs (2026-08-22, imagine-identity gap fix) once that file passed
// the 500-LoC coding-standards threshold: these types hold NO service dependency (plain data in,
// plain data out) and are a natural "separate type" split from the Plugin partial class orchestration
// that builds/consumes them (which stays in Plugin.LoadoutCapture.cs). See docs/coding-standards.md
// § SOLID — "stop and split before adding more."

using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

/// <summary>One equipped module captured from the local <c>IInventory</c> (self-only): slot, the
/// module's config id + quality, and its rolled parts (attrId, value). Plugin-internal capture shape —
/// distinct from any future upload-wire <c>ModuleEntry</c> DTO, which the assembler (Task 3 of the
/// per-class-loadout plan) maps this onto.</summary>
internal sealed record CapturedModule(int Slot, int ConfigId, int Quality, IReadOnlyList<int[]> Parts);

/// <summary>Archive-time source for a captured class's gear/modules — see
/// <see cref="Plugin.ResolveGearSource"/>. Live-first, loadout-never (owner rulings 2026-08-05 +
/// 2026-08-19): a saved plan is reached only when no live data ever resolved for the class.</summary>
internal enum LoadoutGearSource { Live, Captured, SavedSlot }

/// <summary>
/// A full snapshot of ONE played class's loadout — gear (ids + self-only rolled detail), modules,
/// skills, fashion, its project name, and active talent stage. Captured self-only, latest-wins per
/// <see cref="ProfessionId"/> (see <see cref="LoadoutCapture"/>). <see cref="GearDetail"/> and
/// <see cref="Fashion"/> reuse the existing upload-wire record shapes (<c>LogUpload/CombatLog.cs</c>)
/// so the future upload assembler can attach these with no conversion.
/// </summary>
internal sealed record CapturedLoadout(
    int ProfessionId,
    string? ProjectName,
    int TalentStageId,
    IReadOnlyList<int[]> Gear,               // [slot, itemId(=ConfigId)]
    IReadOnlyList<GearDetail> GearDetail,     // self-only rolled detail — same shape as CaptureSelfGearDetail
    IReadOnlyList<int[]> Skills,              // [skillId, level, tier]
    IReadOnlyList<Fashion> Fashion,
    IReadOnlyList<CapturedModule> Modules,
    IReadOnlyList<int>? TalentNodes = null,   // actual allocated talent-tree node ids (self-only)
    IReadOnlyList<long[]>? Attributes = null,    // [attrId, value] self attribute sheet (BASE) at capture (self-only)
    IReadOnlyList<long[]>? AttrPeaks  = null,    // [attrId, peakValue] sparse combat peaks (self-only)
    long AbilityScore = 0,                       // this class's combat power (FightPoint) read while it was ACTIVE;
                                                  // gear-dependent, so per-class. 0 when unread. (self-only)
    IReadOnlyList<long>? Activations = null,     // per-setup ACTIVATION TIMELINE (owner-approved feature,
                                                  // 2026-08-23): ServerNowMs stamps appended by LoadoutCapture
                                                  // each time this setup BECOMES the equipped identity — at
                                                  // mint and on a swap-back re-match. The SWAP moment, never
                                                  // first-fought (owner ruling: players swap pre-run / between
                                                  // clear and boss; the span starts at the swap). SAME timebase
                                                  // as the uploaded classSpans (ICombatSnapshot.ServerNowMs,
                                                  // TickClassTimeline) so the site intersects them directly.
                                                  // Managed by LoadoutCapture.Capture — callers pass null.
    IReadOnlyList<int>? Imagines = null);        // equipped Battle Imagine ids, slot-ordered [X, Z] (self-only),
                                                  // a copy of IResonanceState.Installed at capture. Empty (never
                                                  // null from a live capture — BuildLoadoutImagines always
                                                  // returns a non-null array) when unsynced; the nullable
                                                  // annotation only covers test/fake fixtures that omit it.
                                                  // Owner gap, run B47O8jx6wp retest (2026-08-22): an Imagine
                                                  // swap alone must mint a new fought-with setup — see
                                                  // LoadoutCapture.SameSetup (ORDER-SENSITIVE: slots X/Z are
                                                  // distinct — but empty is no-signal, see ImaginesDiffer).

/// <summary>
/// Pure per-class loadout accumulator — an ORDERED LIST of captures per professionId (oldest first),
/// no longer a strict latest-wins upsert. Holds NO service dependency (plain data in, plain data out),
/// so it is unit-testable without an IL2CPP/IPluginServices fake (see <c>LoadoutCaptureTests</c>).
/// <see cref="Plugin"/>'s <c>PollLocalProfession</c>/<c>CaptureActiveClassLoadout</c> (Plugin.LoadoutCapture.cs)
/// do the live-service reads and hand the finished <see cref="CapturedLoadout"/> to <see cref="Capture"/> —
/// that seam is deliberately thin and untested; only in-game verification exercises IInventory/ILoadout.
///
/// Spec evolved 2026-08-22 (owner run <c>B47O8jx6wp</c>): a player fought a 5-module setup, idled,
/// removed one module, fought again, archived ONCE — the upload carried only the 4-module setup
/// because the plain latest-wins upsert let the post-fight recapture silently overwrite the fought-with
/// gear before any archive banked it. Owner, verbatim: <em>"when any equipment change such as
/// module,talents,equipments... and use have a combat with that setup it require plugin to take
/// snapshot of it even class has no change."</em> So a class's list now keeps a fought-with setup as
/// its OWN entry; only an UNFOUGHT draft (browsing gear with no combat since it was captured) gets
/// overwritten. See <see cref="Capture"/> for the exact decision.
/// </summary>
internal sealed class LoadoutCapture
{
    // One captured setup + the combat-activity marker value SAMPLED AT THE MOMENT this entry was
    // FIRST captured. A same-content refresh (see Capture) keeps this original value rather than
    // bumping it forward, so a later, genuinely different capture always compares against "was there
    // ANY combat while this content was equipped" — never against a refresh's own marker.
    private sealed record Entry(CapturedLoadout Capture, long MarkerAtCapture);

    private readonly Dictionary<int, List<Entry>> _byProfession = new();

    /// <summary>
    /// Stores <paramref name="capture"/> under its <see cref="CapturedLoadout.ProfessionId"/>.
    /// <paramref name="combatMarker"/> is a monotonically increasing combat-activity marker sampled by
    /// the caller (<see cref="Plugin"/>'s <c>_combatEventMarker</c>, Plugin.Capture.cs) — comparing it
    /// against the value recorded when the class's LAST entry was captured is how a fought-with setup
    /// is told apart from an unfought gear-browsing draft:
    ///   - no entry yet for this class          → add the first entry.
    ///   - same content as the last entry       → REPLACE it in place; the marker reference point is
    ///                                             KEPT, never bumped, so a later genuinely different
    ///                                             setup still sees every fight that happened while
    ///                                             this content was equipped. An UNFOUGHT entry
    ///                                             refreshes wholesale (the pre-existing behavior); a
    ///                                             FOUGHT-WITH entry keeps its captured
    ///                                             Skills/AbilityScore/Attributes frozen — see
    ///                                             <see cref="RefreshFought"/> (owner staging run
    ///                                             sea/ZdTH3UwZQ6, the chimera setup).
    ///   - same content as an EARLIER entry     → swap-back: RE-ACTIVATE that entry — new activation
    ///                                             stamp, moved to the end (the class's active slot);
    ///                                             a trailing unfought draft dies. See
    ///                                             <see cref="TryRematchEarlier"/>.
    ///   - different content, marker UNCHANGED  → REPLACE the last entry (no combat occurred since it
    ///                                             was captured — an unfought draft, e.g. someone
    ///                                             flicking through gear before pulling).
    ///   - different content, marker ADVANCED   → APPEND a new entry (the last entry WAS fought with —
    ///                                             preserve it; owner run B47O8jx6wp).
    ///
    /// <paramref name="nowMs"/> (ICombatSnapshot.ServerNowMs — the classSpans timebase) is stamped
    /// onto a setup's <see cref="CapturedLoadout.Activations"/> whenever it BECOMES the equipped
    /// identity: at mint, on a draft replacement (the survivor), and on a swap-back re-match. A
    /// same-identity refresh of the already-active (last) entry stamps nothing.
    /// </summary>
    public void Capture(CapturedLoadout capture, long combatMarker, long nowMs = 0)
    {
        if (!_byProfession.TryGetValue(capture.ProfessionId, out var entries))
        {
            entries = new List<Entry>();
            _byProfession[capture.ProfessionId] = entries;
        }

        if (entries.Count == 0)
        {
            entries.Add(new Entry(Activate(capture, nowMs), combatMarker));   // mint — first activation
            return;
        }

        var last = entries[^1];
        if (SameSetup(last.Capture, capture))
        {
            var refreshed = combatMarker != last.MarkerAtCapture
                ? RefreshFought(last.Capture, capture)   // fought-with — freeze the class-switch-racy fields
                : capture;                                // unfought draft — wholesale refresh, as before
            // The last entry IS this class's active setup — a same-identity refresh appends NO
            // activation; the entry's existing timeline is carried forward untouched.
            entries[^1] = new Entry(refreshed with { Activations = last.Capture.Activations }, last.MarkerAtCapture);
            return;
        }

        if (TryRematchEarlier(entries, capture, combatMarker, nowMs)) return;   // swap-back → re-activate

        if (combatMarker == last.MarkerAtCapture)
        {
            // Unfought draft — overwrite in place; the dead draft's activation stamps die with it.
            entries[^1] = new Entry(Activate(capture, nowMs), combatMarker);
            return;
        }

        entries.Add(new Entry(Activate(capture, nowMs), combatMarker));   // fought-with — preserve it, new entry activates
    }

    // Appends nowMs to the capture's activation timeline — the moment this setup BECAME the equipped
    // identity (owner ruling 2026-08-23: the span starts at the SWAP, never the first hit — players
    // swap pre-run and between clear and boss phases). Timebase = ICombatSnapshot.ServerNowMs, the
    // SAME clock the uploaded classSpans stamp (TickClassTimeline), so the site intersects the two.
    private static CapturedLoadout Activate(CapturedLoadout capture, long nowMs)
    {
        var prior = capture.Activations;
        var stamps = new List<long>((prior?.Count ?? 0) + 1);
        if (prior is not null) stamps.AddRange(prior);
        stamps.Add(nowMs);
        return capture with { Activations = stamps };
    }

    // Swap-back (owner-approved activation-timeline design, 2026-08-23): re-equipping a setup
    // identical to an EARLIER entry of this class RE-ACTIVATES that entry — a new activation stamp,
    // and the entry moves to the end (the class's active slot, which ResolveLoadoutFields /
    // ResolveSelfEquipment read as "currently equipped") — instead of minting a duplicate. Content
    // refreshes under the same fought-freeze rules as a last-entry match (a non-last entry is fought
    // by construction: an unfought last entry is always replaced, never appended past). An unfought
    // draft sitting at the end dies, its activation stamps with it (the draft-replacement rule).
    private static bool TryRematchEarlier(List<Entry> entries, CapturedLoadout capture, long combatMarker, long nowMs)
    {
        for (var i = entries.Count - 2; i >= 0; i--)
        {
            if (!SameSetup(entries[i].Capture, capture)) continue;
            var matched = entries[i];
            var refreshed = combatMarker != matched.MarkerAtCapture
                ? RefreshFought(matched.Capture, capture)
                : capture;
            if (combatMarker == entries[^1].MarkerAtCapture)
            {
                entries.RemoveAt(entries.Count - 1);   // the trailing unfought draft dies
            }
            entries.RemoveAt(i);
            entries.Add(new Entry(
                Activate(refreshed with { Activations = matched.Capture.Activations }, nowMs),
                matched.MarkerAtCapture));
            return true;
        }
        return false;
    }

    /// <summary>Same-identity refresh of a FOUGHT-WITH entry: keep the fought capture's
    /// Skills/AbilityScore/Attributes frozen, take everything else from the refresh. During a
    /// class-switch's SelfGearChanged burst the recapture runs while attr 220 still reads the OLD
    /// profession, so the slot-keyed reads (gear/talents/modules) still describe the old class —
    /// SameSetup true — while the LIVE self reads have already flipped to the NEW class
    /// (GetSkillLevels served the tank 49-skill list, GetFightPoint its 34840 score), and the
    /// wholesale in-place refresh poisoned the fought frost entry with them (owner staging run
    /// sea/ZdTH3UwZQ6 — the stored half of the chimera setup). Only those three live-read fields
    /// are frozen: ProjectName/Fashion/GearDetail/AttrPeaks keep refreshing exactly as before
    /// (pinned: SameContent_CombatAdvancedSince_StillRefreshesInPlace_NeverAppends), and Imagines
    /// follows empty-is-no-signal in BOTH directions — the refresh's pair wins (the []→populated
    /// heal, pinned by the ImagineSentinel tests) unless the refresh side is the empty one, which
    /// must never wipe a fought pair.</summary>
    private static CapturedLoadout RefreshFought(CapturedLoadout fought, CapturedLoadout refresh)
    {
        var merged = refresh with
        {
            Skills = fought.Skills,
            AbilityScore = fought.AbilityScore,
            Attributes = fought.Attributes,
        };
        if ((fought.Imagines?.Count ?? 0) > 0 && (refresh.Imagines?.Count ?? 0) == 0)
        {
            merged = merged with { Imagines = fought.Imagines };
        }
        return merged;
    }

    /// <summary>Clears every captured class. Called at RUN START only — NOT on every archive
    /// (<see cref="Plugin.Clear"/> fires per encounter within the same run and must not drop classes
    /// captured earlier in that same run).</summary>
    public void ResetForRun() => _byProfession.Clear();

    /// <summary>Every entry captured so far this run, for the upload assembler. A class played once
    /// with no fought-with equipment change carries exactly one entry (unchanged from before this
    /// spec evolved); a class fought with, then changed, carries one entry PER distinct fought-with
    /// setup. Per-class order is LAST-ACTIVATION order (a swap-back moves the re-activated entry to
    /// the end), so each class's LAST entry is its currently-active setup — the invariant the
    /// top-level mirrors (ResolveLoadoutFields / ResolveSelfEquipment) read. Classes never played
    /// this run are absent.</summary>
    public IReadOnlyList<CapturedLoadout> Snapshot()
    {
        var result = new List<CapturedLoadout>();
        foreach (var entries in _byProfession.Values)
            foreach (var e in entries)
                result.Add(e.Capture);
        return result;
    }

    /// <summary>The talent identity (stage + nodes) recorded on <paramref name="professionId"/>'s LAST
    /// entry ((0, null) when no entry exists yet). Was the comparison seam the retired
    /// <c>TickTalentRecapture</c> compare-poll read (the talent-edit race, owner staging run
    /// sea/CdPgKYHQ6e); the framework now reports a real change itself (ILoadout.LiveStateChanged,
    /// owner ruling 2026-08-23) and the poll is gone. Kept as a tested accessor.</summary>
    internal (int TalentStageId, IReadOnlyList<int>? TalentNodes) LastTalents(int professionId)
        => _byProfession.TryGetValue(professionId, out var entries) && entries.Count > 0
            ? (entries[^1].Capture.TalentStageId, entries[^1].Capture.TalentNodes)
            : (0, null);

    /// <summary>The Imagine pair recorded on <paramref name="professionId"/>'s LAST entry (empty when
    /// no entry exists yet). Was the comparison seam the retired <c>TickImagineRecapture</c>
    /// compare-poll read to catch an imagine swap no per-field event covered (owner gap, run
    /// B47O8jx6wp retest); the container-merge event now covers it. Kept as a tested accessor.</summary>
    internal IReadOnlyList<int> LastImagines(int professionId)
        => _byProfession.TryGetValue(professionId, out var entries) && entries.Count > 0
            ? entries[^1].Capture.Imagines ?? System.Array.Empty<int>()
            : System.Array.Empty<int>();

    /// <summary>Pure content-identity check for "is this the same setup" — gear [slot,itemId] pairs,
    /// modules (slot/configId/quality/parts), TalentStageId, TalentNodes (canonically sorted so
    /// capture-order jitter alone can never split one setup into two), and Imagines (owner gap, run
    /// B47O8jx6wp retest, 2026-08-22 — equipped Battle Imagines join the identity). Imagines is
    /// compared ORDER-SENSITIVE, unlike TalentNodes/gear/modules: slot X and slot Z are distinct, so
    /// [Predator Spider, Muku Chief] is a different setup from [Muku Chief, Predator Spider] — but only
    /// when BOTH sides are non-empty (<see cref="ImaginesDiffer"/>): a login-order race lets the first
    /// capture of a run land before the 1 Hz <c>IResonanceState.Installed</c> poll ever populates, so an
    /// empty side is "not yet known", not "no Imagines equipped" — treating it as a real difference let
    /// the []→populated transition APPEND a phantom second setup with no actual swap (review finding,
    /// 2026-08-22; mirrors the <see cref="Plugin.PreferNonEmpty{T}"/> empty-is-no-signal rule already
    /// used for gear/modules at archive time). Deliberately EXCLUDES GearDetail (self-only enrichment
    /// jitter — refine/enchant/roll detail can read differently capture-to-capture for the identical
    /// physical gear) and Attributes/AttrPeaks/Skills/Fashion/ProjectName/AbilityScore, which drift
    /// without the player changing anything. Mirrors the worker's setup-identity rationale
    /// (<c>loadoutVariantKey</c>, services/stellar-logs/src/do/mergeActors.ts) so the plugin and the
    /// server agree on what counts as "a different build" — including Quality in the module identity,
    /// matching that key exactly.</summary>
    internal static bool SameSetup(CapturedLoadout a, CapturedLoadout b)
        => a.TalentStageId == b.TalentStageId
        && SameGear(a.Gear, b.Gear)
        && SameIntSet(a.TalentNodes, b.TalentNodes)
        && SameModules(a.Modules, b.Modules)
        && !ImaginesDiffer(a.Imagines, b.Imagines);

    /// <summary>Whether Imagines contributes a genuine "different setup" signal to <see cref="SameSetup"/>
    /// — true only when BOTH sides are non-empty and the order-sensitive pair differs. An empty side
    /// (either) means "not yet known" (the 1 Hz resonance poll hasn't landed since login/class-swap),
    /// never "no Imagines equipped" (a live capture's <see cref="BuildLoadoutImagines"/>-style read is
    /// empty only while unsynced), so it can never itself mint a difference. This is what lets the
    /// []→populated transition route through <see cref="Capture"/>'s same-setup REPLACE branch — which
    /// carries the NEW capture's (non-empty) Imagines forward — instead of APPEND, healing the sentinel
    /// in place rather than minting a phantom second entry for a swap that never happened.</summary>
    internal static bool ImaginesDiffer(IReadOnlyList<int>? a, IReadOnlyList<int>? b)
    {
        if ((a?.Count ?? 0) == 0 || (b?.Count ?? 0) == 0) return false;
        return !SameIntSequence(a, b);
    }

    private static bool SameGear(IReadOnlyList<int[]> a, IReadOnlyList<int[]> b)
    {
        if (a.Count != b.Count) return false;
        var sa = SortedPairs(a);
        var sb = SortedPairs(b);
        for (var i = 0; i < sa.Count; i++)
            if (sa[i][0] != sb[i][0] || sa[i][1] != sb[i][1]) return false;
        return true;
    }

    private static List<int[]> SortedPairs(IReadOnlyList<int[]> pairs)
    {
        var list = new List<int[]>(pairs);
        list.Sort((x, y) => x[0] != y[0] ? x[0].CompareTo(y[0]) : x[1].CompareTo(y[1]));
        return list;
    }

    private static bool SameIntSet(IReadOnlyList<int>? a, IReadOnlyList<int>? b)
    {
        var sa = a is null ? new List<int>() : new List<int>(a);
        var sb = b is null ? new List<int>() : new List<int>(b);
        if (sa.Count != sb.Count) return false;
        sa.Sort(); sb.Sort();
        for (var i = 0; i < sa.Count; i++)
            if (sa[i] != sb[i]) return false;
        return true;
    }

    /// <summary>ORDER-SENSITIVE sequence equality, null treated as empty — unlike <see cref="SameIntSet"/>
    /// (which sorts), this is the identity compare for Imagines (slot X/Z are distinct positions, so
    /// permuting them is a genuinely different setup). Allocation-free: a plain indexed walk, no
    /// sort/copy.</summary>
    internal static bool SameIntSequence(IReadOnlyList<int>? a, IReadOnlyList<int>? b)
    {
        var la = a?.Count ?? 0;
        var lb = b?.Count ?? 0;
        if (la != lb) return false;
        for (var i = 0; i < la; i++)
            if (a![i] != b![i]) return false;
        return true;
    }

    private static bool SameModules(IReadOnlyList<CapturedModule> a, IReadOnlyList<CapturedModule> b)
    {
        if (a.Count != b.Count) return false;
        var sa = new List<CapturedModule>(a); sa.Sort((x, y) => x.Slot.CompareTo(y.Slot));
        var sb = new List<CapturedModule>(b); sb.Sort((x, y) => x.Slot.CompareTo(y.Slot));
        for (var i = 0; i < sa.Count; i++)
            if (!SameModule(sa[i], sb[i])) return false;
        return true;
    }

    private static bool SameModule(CapturedModule a, CapturedModule b)
    {
        if (a.Slot != b.Slot || a.ConfigId != b.ConfigId || a.Quality != b.Quality) return false;
        if (a.Parts.Count != b.Parts.Count) return false;
        var pa = SortedPairs(a.Parts);
        var pb = SortedPairs(b.Parts);
        for (var i = 0; i < pa.Count; i++)
            if (pa[i][0] != pb[i][0] || pa[i][1] != pb[i][1]) return false;
        return true;
    }
}
