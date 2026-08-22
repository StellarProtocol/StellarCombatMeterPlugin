using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Domain.Loadout;
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
    long AbilityScore = 0);                       // this class's combat power (FightPoint) read while it was ACTIVE;
                                                  // gear-dependent, so per-class. 0 when unread. (self-only)

/// <summary>
/// Pure per-class loadout accumulator — an ORDERED LIST of captures per professionId (oldest first),
/// no longer a strict latest-wins upsert. Holds NO service dependency (plain data in, plain data out),
/// so it is unit-testable without an IL2CPP/IPluginServices fake (see <c>LoadoutCaptureTests</c>).
/// <see cref="Plugin"/>'s <c>PollLocalProfession</c>/<c>CaptureActiveClassLoadout</c> below do the
/// live-service reads and hand the finished <see cref="CapturedLoadout"/> to <see cref="Capture"/> —
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

    /// <summary>Pure content-identity check for "is this the same setup" — gear [slot,itemId] pairs,
    /// modules (slot/configId/quality/parts), TalentStageId, and TalentNodes, each compared as a
    /// CANONICALLY SORTED set so capture-order jitter alone can never split one setup into two.
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
        && SameModules(a.Modules, b.Modules);

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

/// <summary>
/// Per-class loadout capture orchestration (owner design 2026-08-02): a class the player PLAYED was
/// ACTIVE at some point, and <c>IInventory</c> gives the ACTIVE class rich data (rolls via
/// <c>GetSelfGear</c>, modules via <c>GetEquipped</c>/<c>GetModules</c>) that the broadcast per-entity
/// APIs never carry once the player has swapped away from that class — even for self. So this polls
/// the local player's live profession (<c>IPlayerState.Profession</c>, attr 220) and, whenever it
/// changes to a new class, freezes THAT class's current loadout into <see cref="_loadoutCapture"/>,
/// keyed by professionId (latest-wins). Self-only; never touches teammates. The accumulator resets
/// only at RUN START (see <see cref="IsNewLoadoutRun"/>) — NOT by <see cref="Plugin.Clear"/>, which
/// fires on every archive within a run and must not lose classes captured earlier in it. This task
/// (per-class-loadout Task 2) only builds + wires the accumulator; nothing here touches the upload —
/// that is Task 3.
/// </summary>
public sealed partial class Plugin
{
    private readonly LoadoutCapture _loadoutCapture = new();

    // Last profession value POLLED this run (0 = none seen yet / just reset). Distinct from the
    // accumulator's map key set — this only gates re-capture on an unchanged live value.
    private int _lastPolledProfession;

    // Dungeon run-id last observed by the loadout run-boundary check — separate from Plugin.Replay.cs's
    // _replayRunId (different reset semantics; see IsNewLoadoutRun's doc for why).
    private long _loadoutRunId;

    /// <summary>Read-only view of every class captured so far this run, for the upload assembler.</summary>
    internal IReadOnlyList<CapturedLoadout> LoadoutSnapshot() => _loadoutCapture.Snapshot();

    // Throttled tick — called from OnUpdate at the existing ~10 Hz snapshot cadence (Plugin.cs's
    // _snapshotAccum block), not every frame: a profession swap is a rare, deliberate player action.
    private void TickLoadoutCapture()
    {
        TickLoadoutRunBoundary();
        PollLocalProfession();
        TickGearRecapture();
        TickAttrRangeSample();
        TickClassTimeline();   // per-entity professionId timeline (self + party) — Plugin.ClassTimeline.cs
    }

    // Set on IInventory.SelfGearChanged, which fires on the network/sync thread (see that event's
    // threading contract) — so the handler ONLY flips this flag and NEVER touches game state (IL2CPP
    // reads off the tick thread are a native-crash class). The tick below consumes it. Event-driven:
    // no polling — the game pushes a full gear sync only on login / map change / class swap / gear edit.
    private volatile bool _gearDirty;

    private void OnSelfGearChanged() => _gearDirty = true;

    // A class swap re-syncs the new class's gear a MOMENT AFTER the profession attr flips, so
    // PollLocalProfession's switch-instant capture froze the OLD class's stale gear (owner-reported:
    // gear identical across classes; root cause docs/recon/combatmeter-data-facts.md). When the fresh
    // sync lands we re-capture the active class — latest-wins overwrites the stale gear (and re-reads
    // modules/fashion/etc. too). Runs at most once per gear sync, on the game tick.
    private void TickGearRecapture()
    {
        if (!_gearDirty) return;
        _gearDirty = false;
        var prof = _services.PlayerState.Profession;
        LogGearSyncDiag(prof);   // Part B gear investigation — no-op unless STELLAR_DIAGNOSTICS
        if (prof != 0) CaptureActiveClassLoadout(prof);
    }

    /// <summary>True when <paramref name="newRunId"/> marks the START of a run the accumulator hasn't
    /// captured for yet: a non-zero id different from the one last observed. 0→A (entering a run,
    /// including straight from boot) resets a fresh accumulator so any pre-run town-swap captures
    /// don't leak into this run's data; A→B (different non-zero run, e.g. crash/re-enter) resets too.
    /// A→0 (leaving to town) deliberately does NOT reset — a dungeon→town archive still needs to read
    /// this run's captured classes. A→A (repeated poll, same run) is a no-op.</summary>
    internal static bool IsNewLoadoutRun(long previousRunId, long newRunId)
        => newRunId != 0 && newRunId != previousRunId;

    private void TickLoadoutRunBoundary()
    {
        var runId = _services.Dungeon.CurrentRunId;
        if (IsNewLoadoutRun(_loadoutRunId, runId))
        {
            _loadoutCapture.ResetForRun();
            _attrRange.ResetForRun();
            _classSpans.ResetForRun();
            _lastPolledProfession = 0;
        }
        _loadoutRunId = runId;
    }

    /// <summary>Reads the local player's live profession; on a new non-zero value (including the first
    /// one seen since a run-start reset) captures that class's active loadout (self-only).</summary>
    private void PollLocalProfession()
    {
        var current = _services.PlayerState.Profession;
        if (current == 0 || current == _lastPolledProfession) return;
        var prev = _lastPolledProfession;
        _lastPolledProfession = current;
        LogProfChangeDiag(prev, current);   // Part B gear investigation — no-op unless STELLAR_DIAGNOSTICS
        CaptureActiveClassLoadout(current);
    }

    // Purely additive and self-only: a no-op when the loadout API isn't up yet or we're not in world.
    private void CaptureActiveClassLoadout(int professionId)
    {
        if (!_services.Loadout.IsAvailable) return;
        var self = _services.CombatSnapshot.LocalEntityId;
        if (!self.IsPlayer) return;

        var (projectName, talentStageId, talentNodes) = ResolveActiveProject(professionId);
        // Gear/modules come from the PICKED loadout entry (PickSlot: the live-synthesized "Current"
        // entry, else the plan the player is ON) — the framework overlays the CURRENT plan with the
        // LIVE equipped set read via the Lua bridge, the only in-game-verified live source.
        // IInventory.GetLiveEquipped is NOT live in-game (stale method-21 latch; owner-verified
        // 2026-08-19: frozen login modules + empty gear) — never source equipment from it. The owner's
        // "never a saved loadout" rule (2026-08-05, re-escalated 2026-08-19: "I don't want any loadout,
        // I want what user currently is using") still holds here: the picked entry IS live data, not a
        // saved plan, because PickSlot prefers the live-synthesized "Current" (-1) entry over any saved
        // plan. Captured here while the class is active; the ACTIVE class re-reads the picked slot once
        // more at archive (ApplyLiveEquipment) so the snapshot is the setup the combat actually used.
        // AbilityScore (FightPoint) is gear-dependent, so we read it HERE while this class is the active
        // one — the broadcast per-entity value reflects only whatever class the player is on now. A class
        // swap re-syncs gear a moment later (TickGearRecapture re-fires this), and latest-wins overwrites
        // the stale read with the settled per-class value (same lifecycle as the gear re-capture).
        var slot = FindLoadoutSlot(professionId);
        var slotGear = slot?.Gear;
        // combatMarker: sampled NOW (Plugin.Capture.cs's _combatEventMarker) so LoadoutCapture.Capture
        // can tell "this class was fought with since its last capture" from "this is just another
        // unfought gear-browsing draft" — see that method's doc for the full decision table.
        _loadoutCapture.Capture(new CapturedLoadout(
            ProfessionId:  professionId,
            ProjectName:   projectName,
            TalentStageId: talentStageId,
            Gear:          slotGear is { Count: > 0 } ? BuildGearPairs(slotGear) : System.Array.Empty<int[]>(),
            GearDetail:    slotGear is { Count: > 0 } ? BuildLoadoutGearDetail(slotGear) : System.Array.Empty<GearDetail>(),
            Skills:        BuildLoadoutSkills(self),
            Fashion:       BuildLoadoutFashion(self),
            Modules:       BuildLoadoutModulesFromSlot(slot?.Modules),
            TalentNodes:   talentNodes,
            Attributes:    BuildLoadoutAttributes(self),
            AbilityScore:  _services.CombatLookup.GetFightPoint(self)),
            _combatEventMarker);
    }

    // Snapshot the local player's non-zero attribute sheet ([attrId, value]) at capture time so the
    // site can show THIS class's stats (self-only; each class captured while it was active). Same
    // source the whole-player upload uses (IEntityDetail.GetAttributes).
    private List<long[]> BuildLoadoutAttributes(EntityId self)
    {
        var attrs = _services.EntityDetail.GetAttributes(self);
        var list = new List<long[]>(attrs.Count);
        foreach (var (attrId, value) in attrs)
        {
            if (value != 0) list.Add(new long[] { attrId, value });
        }
        return list;
    }

    // The active class's project name + talent stage + allocated talent nodes, from the framework's
    // loadout entries. FindLoadoutSlot prefers the live-synthesized "Current" (-1) entry — the only
    // entry whose talents are live via ILoadout.LiveState since Phase 2 — then the plan the player is ON;
    // a saved plan's entry carries its own parsed talents, so a respec without a plan save can be stale
    // until the next refresh. Never an arbitrary same-class sibling. Absent (0/null) when nothing
    // currently describes this class.
    private (string? ProjectName, int TalentStageId, IReadOnlyList<int>? TalentNodes) ResolveActiveProject(int professionId)
    {
        var slot = FindLoadoutSlot(professionId);
        var (stage, nodes) = ResolveTalents(_services.Loadout.LiveState, slot, professionId);
        return (slot?.Name, stage, nodes);
    }

    /// <summary>Talent source for the ACTIVE class: the framework's LIVE state when it describes
    /// this class (never a saved plan — owner rule), else the picked slot's parsed talents, else
    /// empty. All-or-nothing per source: live talents are never spliced with a plan's.</summary>
    internal static (int TalentStageId, IReadOnlyList<int>? TalentNodes) ResolveTalents(
        LiveLoadoutState? live, LoadoutSlot? slot, int professionId)
        => live is not null && live.ProfessionId == professionId
            ? (live.TalentStageId, live.TalentNodes)
            : (slot?.TalentStageId ?? 0, slot?.TalentNodes);

    // Best LoadoutSlot describing this class, or null — see PickSlot for the preference order.
    private LoadoutSlot? FindLoadoutSlot(int professionId) => PickSlot(_services.Loadout.GetSlots(), professionId);

    /// <summary>Preference order for the entry describing a class: the framework's live-synthesized
    /// "Current" entry (Index -1 — IS the live state) beats the plan the player is ON (IsCurrent) beats
    /// any other same-class plan. The old first-match-by-profession pick is what shipped a sibling plan's
    /// saved modules as "the" build (owner report 2026-08-19, run sea/YcVuYojHoD: tank archive carried
    /// the Frostbeam plan's modules).</summary>
    internal static LoadoutSlot? PickSlot(IReadOnlyList<LoadoutSlot> slots, int professionId)
    {
        LoadoutSlot? current = null, first = null;
        foreach (var slot in slots)
        {
            if (slot.ProfessionId != professionId) continue;
            if (slot.Index == -1) return slot;
            if (slot.IsCurrent) current ??= slot;
            first ??= slot;
        }
        return current ?? first;
    }

    /// <summary>Which source fills a captured class's gear/modules at archive. LIVE for the class the
    /// player is on (the setup this combat actually used); otherwise the values frozen while the class
    /// was active; a saved LoadoutSlot only as last resort when live never resolved for it. Pure — the
    /// regression tests pin that an available live/captured set is NEVER passed over for a saved plan.</summary>
    internal static LoadoutGearSource ResolveGearSource(bool isActiveClass, bool liveHasData, bool capturedHasData)
        => isActiveClass && liveHasData ? LoadoutGearSource.Live
         : capturedHasData ? LoadoutGearSource.Captured
         : LoadoutGearSource.SavedSlot;

    // At archive (Plugin.AttrRange.cs ApplyAttrRanges): re-read the ACTIVE class's LAST entry from the
    // picked loadout slot (live-overlaid — see CaptureActiveClassLoadout) so the archive carries
    // exactly the setup this combat used (module/gear edits mid-run included, class change or not).
    // Earlier-played classes, AND any earlier fought-with entry preserved for the same class (owner
    // run B47O8jx6wp — see LoadoutCapture.Capture), keep the values frozen at their capture moment. A
    // saved plan is read only as a never-saw-live-or-capture last resort (ResolveGearSource). Both the
    // Live and SavedSlot outcomes now read from the SAME picked slot (the slot IS the live overlay for
    // the active class, and IS the saved plan otherwise) — the only difference is which entry gets
    // read at all.
    //
    // isLastOfActiveClass: true only for the newest entry of the class currently active (computed by
    // the caller, which knows every entry's position — see ApplyAttrRanges). A class can now carry
    // MULTIPLE entries; re-overlaying an EARLIER, already-fought-with entry with the CURRENT live gear
    // would silently erase the very setup this fix exists to preserve, so only the last one is
    // eligible for the live read.
    private CapturedLoadout ApplyLiveEquipment(CapturedLoadout l, bool isLastOfActiveClass)
    {
        var slot = FindLoadoutSlot(l.ProfessionId);
        var slotHasData = slot?.Gear is { Count: > 0 } || slot?.Modules is { Count: > 0 };
        var capturedHasData = l.Gear.Count > 0 || l.Modules.Count > 0;
        if (ResolveGearSource(isLastOfActiveClass, slotHasData, capturedHasData) == LoadoutGearSource.Captured)
            return l;

        // Fill each component ONLY when the slot actually has it — a slot with data in one component
        // but not another (e.g. modules resolved, gear not yet) must never overwrite an already-captured
        // component with an empty one (owner-verified 2026-08-19: this exact OR-gated overwrite emptied
        // a run's gear while re-freezing stale modules). PreferNonEmpty is the pinned pure seam for that.
        var gear = slot?.Gear;
        var freshGear    = gear is { Count: > 0 } ? BuildGearPairs(gear) : (IReadOnlyList<int[]>)System.Array.Empty<int[]>();
        var freshDetail  = gear is { Count: > 0 } ? BuildLoadoutGearDetail(gear) : (IReadOnlyList<GearDetail>)System.Array.Empty<GearDetail>();
        var freshModules = slot?.Modules is { Count: > 0 } m ? BuildLoadoutModulesFromSlot(m) : (IReadOnlyList<CapturedModule>)System.Array.Empty<CapturedModule>();
        return l with
        {
            Gear       = PreferNonEmpty(freshGear, l.Gear),
            GearDetail = PreferNonEmpty(freshDetail, l.GearDetail),
            Modules    = PreferNonEmpty(freshModules, l.Modules),
        };
    }

    /// <summary>Pure component-wise fill rule: a freshly-read component (slot/live read) replaces the
    /// captured one only when the fresh read actually has data — an empty fresh read NEVER overwrites a
    /// non-empty captured component. Pinned regression seam for the empty-overwrite bug
    /// (LiveFirstLoadoutSourceTests.ActiveClass_ComponentNeverOverwrittenByEmptySource).</summary>
    internal static IReadOnlyList<T> PreferNonEmpty<T>(IReadOnlyList<T> fresh, IReadOnlyList<T> kept)
        => fresh.Count > 0 ? fresh : kept;

    // Maps a LoadoutSlot's per-class module set (slot → ModuleInfo, framework-resolved with rolled parts)
    // to the plugin's CapturedModule upload shape.
    private static List<CapturedModule> BuildLoadoutModulesFromSlot(IReadOnlyDictionary<int, ModuleInfo>? modules)
    {
        if (modules is null || modules.Count == 0) return new List<CapturedModule>();
        var list = new List<CapturedModule>(modules.Count);
        foreach (var (slot, info) in modules)
        {
            var parts = new int[info.Parts.Count][];
            for (var i = 0; i < info.Parts.Count; i++)
                parts[i] = new[] { info.Parts[i].AttrId, info.Parts[i].Value };
            list.Add(new CapturedModule(slot, info.ConfigId, info.Quality, parts));
        }
        return list;
    }

    private static List<int[]> BuildGearPairs(IReadOnlyList<GearInstance> gear)
    {
        var list = new List<int[]>(gear.Count);
        foreach (var g in gear) list.Add(new[] { g.Slot, g.ConfigId });
        return list;
    }

    // Reuses AppendRolls (EntitySnapshot.cs) — the SAME roll-resolution formula CaptureSelfGearDetail
    // uses for the live encounter snapshot — so a loadout's gear detail is flattened identically. Writes
    // straight into GearDetail records instead of EntitySnapshot's parallel Gd* arrays: there is no wire
    // format to match here, this feeds the accumulator directly.
    private List<GearDetail> BuildLoadoutGearDetail(IReadOnlyList<GearInstance> gear)
    {
        var list = new List<GearDetail>(gear.Count);
        foreach (var g in gear)
        {
            var rolls = new List<int>();
            AppendRolls(rolls, 0, g.Attrs.Basic);
            AppendRolls(rolls, 1, g.Attrs.Advanced);
            AppendRolls(rolls, 2, g.Attrs.Recast);
            AppendRolls(rolls, 3, g.Attrs.Rare);

            var enchantId = 0;
            var enchantLv = 0;
            if (g.Enchant is { } en)
            {
                enchantLv = en.Level;
                if (_services.GameData.Equip.GetEnchantItem(en.ItemTypeId, en.Level) is { } gem)
                {
                    enchantId = gem.GemItemId;
                    foreach (var eff in gem.Effects) { rolls.Add(4); rolls.Add(eff.AttrId); rolls.Add(eff.Value); rolls.Add(0); }
                }
                else enchantId = en.ItemTypeId;
            }

            var rollPairs = new int[rolls.Count / 4][];
            for (var i = 0; i * 4 < rolls.Count; i++)
                rollPairs[i] = new[] { rolls[i * 4], rolls[i * 4 + 1], rolls[i * 4 + 2], rolls[i * 4 + 3] };

            list.Add(new GearDetail(
                g.Slot, g.Quality, g.RefineLevel,
                g.Perfection.Value, g.Perfection.Max,
                enchantId, enchantLv, rollPairs,
                g.Perfection.Level, g.BreakThroughTime));
        }
        return list;
    }

    private List<int[]> BuildLoadoutSkills(EntityId self)
    {
        var skills = _services.CombatLookup.GetSkillLevels(self);
        var list = new List<int[]>(skills.Count);
        foreach (var s in skills) list.Add(new[] { s.SkillId, s.Level, s.Tier });
        return list;
    }

    private List<Fashion> BuildLoadoutFashion(EntityId self)
    {
        var fashion = _services.EntityDetail.GetFashion(self);
        var list = new List<Fashion>(fashion.Count);
        foreach (var f in fashion)
        {
            var dyes = f.Dyes ?? FashionEntry.NoDyes;
            var flat = new float[dyes.Length * 4];
            for (var i = 0; i < dyes.Length; i++)
            {
                flat[i * 4]     = dyes[i].R;
                flat[i * 4 + 1] = dyes[i].G;
                flat[i * 4 + 2] = dyes[i].B;
                flat[i * 4 + 3] = dyes[i].A;
            }
            list.Add(new Fashion(f.Slot, f.FashionId, flat));
        }
        return list;
    }

}
