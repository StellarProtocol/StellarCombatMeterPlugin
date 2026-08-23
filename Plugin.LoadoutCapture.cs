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
/// APIs never carry once the player has swapped away from that class — even for self. So whenever the
/// framework reports that the live build state changed (<c>ILoadout.LiveStateChanged</c> — which a
/// class switch always raises, since the switch rewrites <c>curProfessionId</c> and the equipped set in
/// the container the event watches), this freezes the ACTIVE class's current loadout into
/// <see cref="_loadoutCapture"/>, keyed by professionId (latest-wins). Nothing here polls: as of
/// 2026-08-23 the last compare-poll (<c>PollLocalProfession</c>, a per-tick re-read of
/// <c>IPlayerState.Profession</c>) is deleted, and the only non-event capture is the run-start ARM in
/// <see cref="TickLoadoutRunBoundary"/>. Self-only; never touches teammates. The accumulator resets
/// only at RUN START (see <see cref="IsNewLoadoutRun"/>) — NOT by <see cref="Plugin.Clear"/>, which
/// fires on every archive within a run and must not lose classes captured earlier in it. This task
/// (per-class-loadout Task 2) only builds + wires the accumulator; nothing here touches the upload —
/// that is Task 3.
/// </summary>
public sealed partial class Plugin
{
    private readonly LoadoutCapture _loadoutCapture = new();

    // Dungeon run-id last observed by the loadout run-boundary check — separate from Plugin.Replay.cs's
    // _replayRunId (different reset semantics; see IsNewLoadoutRun's doc for why).
    private long _loadoutRunId;

    /// <summary>Read-only view of every class captured so far this run, for the upload assembler.</summary>
    internal IReadOnlyList<CapturedLoadout> LoadoutSnapshot() => _loadoutCapture.Snapshot();

    // Throttled tick — called from OnUpdate at the existing ~10 Hz snapshot cadence (Plugin.cs's
    // _snapshotAccum block), not every frame. Nothing here READS the game on a quiet tick: every
    // capture is driven by an armed flag (an event, or the run-start arm below).
    //
    // ORDER IS LOAD-BEARING and unchanged: the run boundary runs FIRST, so the accumulator is reset
    // (and the run-start capture armed) before the recapture that fills it on the very same tick —
    // exactly the ordering the deleted PollLocalProfession relied on.
    private void TickLoadoutCapture()
    {
        TickLoadoutRunBoundary();
        TickBuildRecapture();
        TickAttrRangeSample();
        TickClassTimeline();   // per-entity professionId timeline (self + party) — Plugin.ClassTimeline.cs
    }

    // Set on ILoadout.LiveStateChanged — the framework's POST-PARSE "the live build state I serve
    // actually changed" event (equipped gear/module slots, class, talent stage/nodes, or the equipped
    // Battle Imagine pair). ONE trigger for everything this file used to chase separately.
    //
    // Owner ruling 2026-08-23 — capture is EVENT-DRIVEN at the right probe point, never polled. What
    // this replaced, and why each piece was wrong:
    //   • IInventory.SelfGearChanged (the old _gearDirty path) fires on the NETWORK thread the moment
    //     a container delta arrives — BEFORE the framework re-reads the game's Lua containers. The
    //     recapture ~100 ms later still read PRE-edit data, so a single talent node activation was
    //     never recorded (owner run sea/CdPgKYHQ6e) and an imagine swap served the pre-swap pair.
    //     Worse, that event was gated on a per-field allowlist that did not include the fields the
    //     gear UI's "Replace" button actually emits (measured 2/55/96/104), so a Replace produced no
    //     event at all. It is now field-agnostic AND post-parse.
    //   • TickImagineRecapture / TickTalentRecapture were per-tick COMPARE-POLLS added to paper over
    //     that race. Both are gone: the framework does the comparison once, at the source, and only
    //     tells us when something it serves genuinely differs.
    //   • PollLocalProfession was the LAST compare-poll here (2026-08-23, second pass): it re-read
    //     IPlayerState.Profession every ~10 Hz tick and captured whenever the value differed. A class
    //     switch necessarily rewrites professionList.curProfessionId in the container, so the very
    //     same merge event already covers it — and covers it BETTER, because the event fires after
    //     the framework re-read the class AND that class's equipped set together, while attr 220 is
    //     an independent broadcast that can land on either side of the container delta.
    // The handler runs on the game Update thread but still only flips this flag, so the capture (and
    // its allocations) happen on OUR tick, in the plugin's own cadence.
    //
    // Starts TRUE so the class the player is already on is captured as soon as it resolves at boot —
    // the one thing PollLocalProfession did that no event covers. Re-armed at every run start
    // (TickLoadoutRunBoundary) for the same reason: the accumulator was just emptied.
    private volatile bool _buildDirty = true;

    // What armed _buildDirty — carried into the capture-decision log line (diagnostics only; nothing in
    // the capture path branches on it). Written on the game Update thread, same as _buildDirty.
    private volatile CaptureTrigger _buildTrigger = CaptureTrigger.Boot;

    private void OnLoadoutLiveStateChanged()
    {
        _buildTrigger = CaptureTrigger.LiveState;
        _buildDirty = true;
    }

    // Re-capture the active class whenever the framework reports a real change to the live setup —
    // gear, modules, talents, imagines, or a class swap's gear re-sync (which used to freeze the OLD
    // class's stale gear at the attr-220 instant; root cause docs/recon/combatmeter-data-facts.md).
    // At most once per change event, on the game tick; the normal capture flow does the rest (draft
    // replacement / mint / swap-back re-match + activation stamping are all inherited unchanged).
    private void TickBuildRecapture()
    {
        if (!_buildDirty) return;   // allocation-free no-op on every quiet tick
        var trigger = _buildTrigger;
        var prof = ResolveCaptureProfession(_services.Loadout.LiveState?.ProfessionId ?? 0, _services.PlayerState.Profession);
        if (!ShouldRecaptureOnLiveStateChange(_buildDirty, prof))
        {
            LogCaptureHeld(trigger, prof, "profession");   // class unknown — HOLD the flag
            return;
        }
        _buildDirty = false;
        LogGearSyncDiag(prof);   // Part B gear investigation — no-op unless STELLAR_DIAGNOSTICS
        // A change reported before the game surfaces are up is HELD, never dropped — the same rule the
        // unknown-class branch above follows. Without the re-arm, an event that landed during a scene
        // transition (no local player entity yet) silently lost the player's edit for the whole run.
        if (!CaptureActiveClassLoadout(prof, trigger)) _buildDirty = true;
    }

    /// <summary>THE single profession source for capture keying (unit-tested). The framework's LIVE
    /// class — <c>ILoadout.LiveState.ProfessionId</c>, i.e. the container's <c>curProfessionId</c> —
    /// wins; <c>IPlayerState.Profession</c> (attr 220) is only the fallback for before the live read
    /// has ever resolved.
    ///
    /// <para><b>Why not attr 220.</b> The capture TRIGGER is the container merge, and the live read
    /// that raises it parses the class and that class's equipped set in one pass — so at event time
    /// <c>LiveState.ProfessionId</c> is the class the fresh gear belongs to, by construction. Attr 220
    /// is an independent AOI broadcast that can arrive before or after the container delta; keying on
    /// it while triggering on the container is a cross-source skew that would file the NEW setup under
    /// the OLD class. That skew used to self-heal on the next tick because the deleted
    /// <c>PollLocalProfession</c> re-fired when attr 220 caught up — with no poll left, the two
    /// sources must be one.</para></summary>
    internal static int ResolveCaptureProfession(int liveProfessionId, int playerStateProfession)
        => liveProfessionId != 0 ? liveProfessionId : playerStateProfession;

    /// <summary>Pure trigger decision (unit-tested): re-capture exactly when the framework REPORTED a
    /// live-state change and a real class is known.
    ///
    /// <para>Two properties are pinned here. (1) It never asks WHICH field changed — that is the whole
    /// point of the field-agnostic container-merge signal, and asking is what lost the gear UI's
    /// "Replace" edit (its delta's top-level fields were 2/55/96/104, outside the old
    /// 12/28/57/61/101 allowlist). (2) It never fires without a report — no compare-poll, no timer
    /// (owner ruling 2026-08-23). A change reported before the class is known is HELD, not dropped:
    /// the caller only consumes the flag once <paramref name="professionId"/> is real.</para></summary>
    internal static bool ShouldRecaptureOnLiveStateChange(bool liveStateChanged, int professionId)
        => liveStateChanged && professionId != 0;

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
            // The accumulator is now empty, and entering a dungeon changes nothing about the player's
            // build — so no container merge will fire on its own. ARM the capture instead of polling
            // for it: TickBuildRecapture runs immediately after this on the SAME tick (see
            // TickLoadoutCapture's ordering note), which is exactly when the deleted PollLocalProfession
            // used to re-capture after clearing its _lastPolledProfession memo here.
            _buildTrigger = CaptureTrigger.RunStart;
            _buildDirty = true;
        }
        _loadoutRunId = runId;
    }

    /// <summary>Purely additive and self-only. Returns FALSE when the game surfaces this capture needs
    /// are not up yet (loadout API unresolved, or no local player entity) — the caller re-arms so the
    /// reported change is delivered late rather than dropped.</summary>
    private bool CaptureActiveClassLoadout(int professionId, CaptureTrigger trigger)
    {
        if (!_services.Loadout.IsAvailable) { LogCaptureHeld(trigger, professionId, "loadout"); return false; }
        var self = _services.CombatSnapshot.LocalEntityId;
        if (!self.IsPlayer) { LogCaptureHeld(trigger, professionId, "self-entity"); return false; }

        var capture = BuildActiveClassCapture(professionId, self);
        // combatMarker: sampled NOW (Plugin.Capture.cs's _combatEventMarker) so LoadoutCapture.Capture
        // can tell "this class was fought with since its last capture" from "this is just another
        // unfought gear-browsing draft" — see that method's doc for the full decision table.
        // nowMs: the activation-timeline stamp (owner feature 2026-08-23) — ServerNowMs, the SAME clock
        // TickClassTimeline stamps the uploaded classSpans with, so the site intersects them.
        var decision = _loadoutCapture.Capture(capture, _combatEventMarker, _services.CombatSnapshot.ServerNowMs);
        LogCaptureDecision(trigger, capture, decision);   // no-op unless STELLAR_DIAGNOSTICS
        return true;
    }

    private CapturedLoadout BuildActiveClassCapture(int professionId, EntityId self)
    {
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
        // swap re-syncs gear a moment later (TickBuildRecapture re-fires this), and latest-wins overwrites
        // the stale read with the settled per-class value (same lifecycle as the gear re-capture).
        var slot = FindLoadoutSlot(professionId);
        var slotGear = slot?.Gear;
        return new CapturedLoadout(
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
            Imagines:      BuildLoadoutImagines());
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
