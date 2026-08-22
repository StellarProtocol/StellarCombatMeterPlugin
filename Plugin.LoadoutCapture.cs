using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

// CapturedModule, LoadoutGearSource, CapturedLoadout, and the pure LoadoutCapture accumulator moved
// to LoadoutCapture.cs (2026-08-22 split — this file had crossed the 500-LoC coding-standards
// threshold; see that file's header comment). This file keeps only the Plugin partial class below,
// which does the live-service reads and orchestrates the accumulator.

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
        TickImagineRecapture();
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

    // Owner gap, run B47O8jx6wp retest (2026-08-22): swapping the equipped Battle Imagine (e.g.
    // Predator Spider -> Muku Chief) never fires SelfGearChanged — that event covers IInventory
    // gear/modules only, so without a poll here an Imagine-only swap silently never mints a new
    // fought-with setup. Cheap: two IReadOnlyList<int> reads + an order-sensitive index compare
    // (LoadoutCapture.SameIntSequence), allocation-free on the no-change path — CaptureActiveClassLoadout
    // (and its allocations) only runs on an actual difference, same recapture flow as TickGearRecapture.
    private void TickImagineRecapture()
    {
        var prof = _services.PlayerState.Profession;
        if (prof == 0) return;
        var installed = _services.Resonance.Installed;
        if (LoadoutCapture.SameIntSequence(installed, _loadoutCapture.LastImagines(prof))) return;
        CaptureActiveClassLoadout(prof);
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
            AbilityScore:  _services.CombatLookup.GetFightPoint(self),
            Imagines:      BuildLoadoutImagines()),
            _combatEventMarker);
    }

    // Slot-ordered copy of the live equipped Battle Imagine pair (IResonanceState.Installed) at
    // capture time — owner gap, run B47O8jx6wp retest (2026-08-22). A defensive copy: the
    // implementation publishes an immutable snapshot per its own contract, but this accumulator's
    // entries are meant to be frozen-at-capture like every other component here, so we never hold a
    // reference that could be reinterpreted later. Empty (never null) when unsynced.
    private IReadOnlyList<int> BuildLoadoutImagines()
    {
        var installed = _services.Resonance.Installed;
        if (installed.Count == 0) return System.Array.Empty<int>();
        var copy = new int[installed.Count];
        for (var i = 0; i < installed.Count; i++) copy[i] = installed[i];
        return copy;
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

        // Imagines: an INDEPENDENT live source (IResonanceState.Installed, not the loadout slot), so
        // it refreshes for the active class's newest entry regardless of which way ResolveGearSource
        // falls below — same PreferNonEmpty non-empty-wins rule as gear/modules, so an unsynced (empty)
        // live read never blanks an already-captured pair. Owner gap, run B47O8jx6wp retest (2026-08-22).
        var imagines = isLastOfActiveClass
            ? PreferNonEmpty(_services.Resonance.Installed, l.Imagines ?? System.Array.Empty<int>())
            : l.Imagines;

        if (ResolveGearSource(isLastOfActiveClass, slotHasData, capturedHasData) == LoadoutGearSource.Captured)
            return ReferenceEquals(imagines, l.Imagines) ? l : l with { Imagines = imagines };

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
            Imagines   = imagines,
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
