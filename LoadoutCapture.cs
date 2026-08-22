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
    IReadOnlyList<int>? Imagines = null);        // equipped Battle Imagine ids, slot-ordered [X, Z] (self-only),
                                                  // a copy of IResonanceState.Installed at capture. Empty/null
                                                  // when unsynced. Owner gap, run B47O8jx6wp retest (2026-08-22):
                                                  // an Imagine swap alone must mint a new fought-with setup —
                                                  // see LoadoutCapture.SameSetup (ORDER-SENSITIVE: slots X/Z
                                                  // are distinct).

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
    ///   - same content as the last entry       → REPLACE it in place (refreshed attrs/abilityScore —
    ///                                             the existing refresh behavior; the marker reference
    ///                                             point is KEPT, never bumped, so a later genuinely
    ///                                             different setup still sees every fight that happened
    ///                                             while this content was equipped).
    ///   - different content, marker UNCHANGED  → REPLACE the last entry (no combat occurred since it
    ///                                             was captured — an unfought draft, e.g. someone
    ///                                             flicking through gear before pulling).
    ///   - different content, marker ADVANCED   → APPEND a new entry (the last entry WAS fought with —
    ///                                             preserve it; owner run B47O8jx6wp).
    /// </summary>
    public void Capture(CapturedLoadout capture, long combatMarker)
    {
        if (!_byProfession.TryGetValue(capture.ProfessionId, out var entries))
        {
            entries = new List<Entry>();
            _byProfession[capture.ProfessionId] = entries;
        }

        if (entries.Count == 0)
        {
            entries.Add(new Entry(capture, combatMarker));
            return;
        }

        var last = entries[^1];
        if (SameSetup(last.Capture, capture))
        {
            entries[^1] = new Entry(capture, last.MarkerAtCapture);
            return;
        }

        if (combatMarker == last.MarkerAtCapture)
        {
            entries[^1] = new Entry(capture, combatMarker);   // unfought draft — overwrite in place
            return;
        }

        entries.Add(new Entry(capture, combatMarker));   // fought-with — preserve it, start a new entry
    }

    /// <summary>Clears every captured class. Called at RUN START only — NOT on every archive
    /// (<see cref="Plugin.Clear"/> fires per encounter within the same run and must not drop classes
    /// captured earlier in that same run).</summary>
    public void ResetForRun() => _byProfession.Clear();

    /// <summary>Every entry captured so far this run, for the upload assembler. A class played once
    /// with no fought-with equipment change carries exactly one entry (unchanged from before this
    /// spec evolved); a class fought with, then changed, carries one entry PER distinct fought-with
    /// setup, oldest first. Classes never played this run are absent.</summary>
    public IReadOnlyList<CapturedLoadout> Snapshot()
    {
        var result = new List<CapturedLoadout>();
        foreach (var entries in _byProfession.Values)
            foreach (var e in entries)
                result.Add(e.Capture);
        return result;
    }

    /// <summary>The Imagine pair recorded on <paramref name="professionId"/>'s LAST entry (empty when
    /// no entry exists yet). The cheap comparison seam <see cref="Plugin.TickImagineRecapture"/> polls
    /// live IResonanceState.Installed against to detect a swap that no other event notices — owner gap,
    /// run B47O8jx6wp retest (2026-08-22).</summary>
    internal IReadOnlyList<int> LastImagines(int professionId)
        => _byProfession.TryGetValue(professionId, out var entries) && entries.Count > 0
            ? entries[^1].Capture.Imagines ?? System.Array.Empty<int>()
            : System.Array.Empty<int>();

    /// <summary>Pure content-identity check for "is this the same setup" — gear [slot,itemId] pairs,
    /// modules (slot/configId/quality/parts), TalentStageId, TalentNodes (canonically sorted so
    /// capture-order jitter alone can never split one setup into two), and Imagines (owner gap, run
    /// B47O8jx6wp retest, 2026-08-22 — equipped Battle Imagines join the identity). Imagines is
    /// compared ORDER-SENSITIVE, unlike TalentNodes/gear/modules: slot X and slot Z are distinct, so
    /// [Predator Spider, Muku Chief] is a different setup from [Muku Chief, Predator Spider].
    /// Deliberately EXCLUDES GearDetail (self-only enrichment jitter — refine/enchant/roll detail can
    /// read differently capture-to-capture for the identical physical gear) and Attributes/AttrPeaks/
    /// Skills/Fashion/ProjectName/AbilityScore, which drift without the player changing anything.
    /// Mirrors the worker's setup-identity rationale (<c>loadoutVariantKey</c>,
    /// services/stellar-logs/src/do/mergeActors.ts) so the plugin and the server agree on what counts
    /// as "a different build" — including Quality in the module identity, matching that key exactly.</summary>
    internal static bool SameSetup(CapturedLoadout a, CapturedLoadout b)
        => a.TalentStageId == b.TalentStageId
        && SameGear(a.Gear, b.Gear)
        && SameIntSet(a.TalentNodes, b.TalentNodes)
        && SameModules(a.Modules, b.Modules)
        && SameIntSequence(a.Imagines, b.Imagines);

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
    /// permuting them is a genuinely different setup) and doubles as the cheap tick-time comparison
    /// seam <see cref="Plugin.TickImagineRecapture"/> polls IResonanceState.Installed against.
    /// Allocation-free: a plain indexed walk, no sort/copy.</summary>
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
