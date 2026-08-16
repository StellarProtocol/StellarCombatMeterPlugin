using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;

namespace Stellar.CombatMeter;

/// <summary>
/// Diagnostic-mode logging for <see cref="Plugin"/>. All entry points short-circuit
/// on <see cref="StellarDiagnostics.IsEnabled"/> so production partials can call
/// them unconditionally — keeps the production code clean of inline gates
/// (per coding-standards § Diagnostics; same pattern as
/// <c>FileConfigStore.Diagnostics.cs</c>).
/// </summary>
public sealed partial class Plugin
{
    // Logs (once per id, diagnostics-gated) a damage-attributed id that resolved to neither a real skill, a curated
    // override, nor a buff name — i.e. it renders as a raw "#id". Use the output to add an entry to
    // Plugin.SkillBreakdown's SkillNameOverrides map.
    private readonly HashSet<int> _loggedUnresolvedSkillNames = new();
    private void LogUnresolvedSkillName(int skillId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (!_loggedUnresolvedSkillNames.Add(skillId)) return;
        _services.Log.Info(
            $"[CombatMeter][name] unresolved id={skillId} (no skill, override, or buff name) — add to SkillNameOverrides if needed");
    }

    // Scene-boundary replay reset (93:53 cross-scene-carryover fix). Logs the outgoing/incoming
    // scene, the current run id, samples held at reset, and whether the outgoing scene archived —
    // so an in-game diagnostics pass can confirm the reset fires on a no-combat scene change (the
    // path that previously leaked pre-dungeon samples into the next run's replay upload).
    private void LogReplaySceneReset(string? outgoing, string? incoming, int samplesAtReset, bool archived, bool kept)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[CombatMeter.Replay][scene] reset '{outgoing}' -> '{incoming}' " +
            $"runId={_services.Dungeon.CurrentRunId} samplesAtReset={samplesAtReset} outgoingArchived={archived} kept={kept}");
    }

    // One-shot per encounter: fires the first time TickReplayCapture observes ReplayCapture.TrackCapHit
    // (the 512-track hard cap was reached), so an unexpected id-churn scenario is visible in the log
    // without flooding it every subsequent frame. Latch (_trackCapLogged) lives in Plugin.Replay.cs,
    // reset by ResetReplay().
    private void LogReplayTrackCapHit()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[CombatMeter.Replay][diag] track cap hit — refusing new tracks beyond {ReplayCapture.MaxTracks}");
    }

    // Periodic (~60s, throttled in OnUpdate) field artifact for the FPS cache-leak fix: makes the live
    // ReplayCapture track count directly observable without an in-game debugger. In open world this must
    // read tracks=0 — TickReplayCapture only ever calls NoteEntity while IsInstancedRun() is true.
    private void LogReplayTrackCount()
    {
        if (!StellarDiagnostics.IsEnabled || _replay is null) return;
        _services.Log.Info($"[CombatMeter.Replay][diag] tracks={_replay.Tracks.Count} capHit={_replay.TrackCapHit}");
    }
    // TEMP cast-time-redesign capture: wire cd row vs what we render for a SELF imagine, on change + every
    // ~0.5s. Pins the multi-charge recharge model (does `begin` reset per cast? parallel vs sequential?) and
    // shows where our seconds/charges diverge from the game's own [Z]/[X]. Remove before the next commit.
    private int _imgDiagCount;
    private string _imgDiagSig = "";
    private long _imgDiagNow;
    private void LogSelfImagine(int baseSkill, in SkillCooldown cd, in ImagineSlot slot)
    {
        if (!StellarDiagnostics.IsEnabled || _imgDiagCount >= 300) return;
        long now = _services.CombatSnapshot.ServerNowMs;
        var sig = $"{baseSkill}:{cd.SkillId}:{cd.BeginTimeMs}:{cd.DurationMs}:{slot.ChargesAvailable}";
        bool changed = sig != _imgDiagSig;
        bool tick = now - _imgDiagNow >= 500;
        if (!changed && !tick) return;
        _imgDiagSig = sig; _imgDiagNow = now; _imgDiagCount++;
        long wireRem = cd.BeginTimeMs + cd.DurationMs > now ? cd.BeginTimeMs + cd.DurationMs - now : 0;
        _services.Log.Info(
            $"[CombatMeter][img] base={baseSkill} cdId={cd.SkillId} kind={cd.Kind} ch={cd.ChargeCount} " +
            $"begin={cd.BeginTimeMs} dur={cd.DurationMs} wireRem={wireRem} | render charges={slot.ChargesAvailable}/{slot.ChargeCount} " +
            $"secs={slot.RemainingSeconds} frac={slot.CooldownFraction:F2} now={now}");
    }

    // One line per RECORDED imagine-cast entry (post burst-gap/dedup, i.e. exactly what lands in
    // _imagineCasts and therefore in the upload's derived.imagineCasts). Validation trail: after a
    // run, grep "[img-cast] recorded" and diff against the uploaded log's imagineCasts array.
    private void LogImagineCastRecorded(EntityId src, int baseSkillId, long ms)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        bool isSelf = src.Value == _services.CombatSnapshot.LocalEntityId.Value;
        _services.Log.Info(
            $"[CombatMeter][img-cast] recorded src={src.Value} self={isSelf} base={baseSkillId} ms={ms} now={_services.CombatSnapshot.ServerNowMs}");
    }

    // One line per CombatEvent.EntitySummonAppeared attributed to a tracked player — the signal
    // ObserveSummonAppeared caches to nudge a foreign imagine cast's recorded timestamp earlier than its
    // first-hit time. Validation trail: after a run with imagines cast by other players, grep
    // "[img-summon] appeared" and diff its ms against the matching "[img-cast] recorded" line — the
    // recorded ms should equal (or be very close to) the appear ms, not the (usually later) hit ms.
    private void LogSummonAppeared(CombatEvent.EntitySummonAppeared sa)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[CombatMeter][img-summon] appeared summoner={sa.SummonerId.Value} summon={sa.SummonId.Value} ms={sa.TimestampMs}");
    }

    // One line per appear-RESOLUTION outcome (recorded / rejected+reason) from the appear-sourced
    // imagine-cast channel (Plugin.Capture.cs, TryRecordImagineCastFromAppear). This is the evidence
    // line for the owner's one-shot in-game verification of the channel's load-bearing assumption —
    // that buff-only companions (Tina et al.) carry summoner attribution on APPEAR at all: after a
    // run with a companion-user in the party, grep "[img-appear]". A "recorded"/"not-imagine"/
    // "no-config" line proves the appear fired WITH attribution (the framework only raises
    // EntitySummonAppeared for summoner-attributed entities); no [img-appear]/[img-summon] line for
    // the companion means the appear carried no summoner attrs and this channel cannot see it.
    // "no-config" additionally isolates GetMonsterByEntity failing on the summon entity, and
    // "not-imagine" isolates the composite probe (old framework without the aoyi closure, or a
    // non-imagine summon). config/base read 0 for stages that never resolved them (fixed field
    // count, same sentinel convention as LogArchiveOutcome's "n/a").
    private void LogAppearCastOutcome(CombatEvent.EntitySummonAppeared sa, string outcome, int configId, int baseSkillId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[CombatMeter][img-appear] {outcome} summoner={sa.SummonerId.Value} summon={sa.SummonId.Value} " +
            $"config={configId} base={baseSkillId} ms={sa.TimestampMs}");
    }

    // Const-string tag for the pure gate's reject reasons (precedent: ArchiveReasonTag,
    // Plugin.History.cs) — no ToString() allocation on the (rare) appear path.
    internal static string AppearGateTag(AppearCastGate gate) => gate switch
    {
        AppearCastGate.SelfSummoner => "self",
        AppearCastGate.RepeatSummon => "repeat-summon",
        AppearCastGate.OwnerNotInCombat => "owner-not-in-combat",
        _ => "record",
    };

    // Every SkillUsed event that either belongs to the local player or maps to a Battle Imagine. Kept
    // as an id-space probe: SkillUsed-Begin-based imagine detection was tried and matched ZERO real
    // casts (run 282346129222270976) — these lines show what ids/phases/casters the stream ACTUALLY
    // carries so any future attempt starts from data, not assumption.
    private int _skillUsedLogCount;
    private void LogSkillUsed(CombatEvent.SkillUsed su)
    {
        if (!StellarDiagnostics.IsEnabled || _skillUsedLogCount >= 200) return;
        bool isSelf = su.CasterId.Value == _services.CombatSnapshot.LocalEntityId.Value;
        var img = _services.ResonanceData.GetImagineForSkill(su.SkillId);
        // Only log self casts + anything that maps to an imagine (keeps the flood down while still catching
        // imagine casts by other players).
        if (!isSelf && img is null) return;
        _skillUsedLogCount++;
        _services.Log.Info($"[CombatMeter][skill-used] caster={su.CasterId.Value} self={isSelf} skill={su.SkillId} phase={su.Phase} -> imagine={(img is { } i ? i.SkillId : 0)} now={su.TimestampMs}");
    }

    // One line per auto-archive fire — the Task 10 verification artifact. With the idle-settle delay
    // this marks the moment the engine DECIDED (the pending was armed); the commit lands once combat
    // goes quiet — see LogAutoArchiveCommit. The gap between the two lines is the trailing-damage
    // settle window (the pending waits out _archiveSettleMs of no combat events).
    private void LogAutoArchiveFired(AutoArchive.ArchiveReason reason, in AutoArchive.AutoArchiveInputs s)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[CombatMeter][auto-archive] fired reason={ArchiveReasonTag(reason)} dead={s.DeadCount}/{s.RosterSize} unknown={s.UnknownCount} " +
            $"idleMs={(s.LastDamageMs > 0 ? s.NowMs - s.LastDamageMs : 0)} flowVer={s.FlowStateVersion} run={s.InstancedRun}");
    }

    // One line per deferred AUTO archive that actually commits after the idle-settle wait — pair it
    // with the preceding [auto-archive] fired line to confirm the quiet-window gap in-game. quietMs is
    // how long the general damage clock (_lastDamageMs — every deferrable reason watches the SAME clock
    // as of the 2026-07-28 owner ruling; a prior boss-only narrowing is retired) had been silent at
    // commit; armedMs is the wait since the trigger. Takes the already-computed settle clock rather
    // than re-deriving it, so this line can never drift from what PendingArchiveDue actually used.
    private void LogAutoArchiveCommit(AutoArchive.ArchiveReason reason, long nowMs, long settleClockMs)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[CombatMeter][auto-archive] commit reason={ArchiveReasonTag(reason)} now={nowMs} " +
            $"quietMs={nowMs - settleClockMs} armedMs={nowMs - _pendingArchiveArmedMs} settle={_archiveSettleMs}");
    }

    // One line per ManualArchive ATTEMPT with its outcome (skip-empty | suppressed | banked |
    // banked+upload) — deliberately UNGATED Info, like the SP1 "Uploading log" line: archives are
    // rare per-run lifecycle events, and their SILENT skip variants are exactly what field
    // debugging needs (2026-07-19: a full dungeon run produced no history entry and no upload
    // with zero log evidence; the overwritten-on-boot BepInEx log then destroyed the trail).
    //
    // 2026-07-26: extended with the settle facts (quietMs / armedMs / settle). The gated
    // [auto-archive] fired|commit pair was the only place these appeared, so a 1.6 MB owner log
    // carried ZERO evidence of why a boss archive cut mid-damage — the diagnosis needed a site chart.
    // quietMs is how long the general damage clock (_lastDamageMs) had been silent at this attempt —
    // owner ruling 2026-07-28: every deferrable reason, BossKill included, watches this SAME clock now;
    // a prior boss-only narrowing (SettleClockMs) is retired, see Plugin.AutoArchive.cs's note. armedMs
    // is the deferred wait and reads 0 for an immediate archive.
    //
    // quietMs uses a sentinel "n/a" when the clock was never set (no damage landed in that segment);
    // a numeric reading is a real elapsed age. This avoids ambiguity: a segment with only heals and
    // damage-taken carries _lastDamageMs=0 (never incremented), which would collide with a legitimately
    // 0-ms quiet segment if we printed the numeric 0.
    //
    // armedMs is gated on IsDeferrableArchive deliberately: ManualArchive nulls _pendingArchiveReason
    // on entry but _pendingArchiveArmedMs is never cleared, so an immediate archive (manual / scene /
    // the inline BossPhase cut) would otherwise print the age of some earlier, unrelated pending.
    //
    // 2026-07-28 (P0 gone-timeout fix): cause= distinguishes a BossKill fired by a CONFIRMED death from
    // one fired by AutoArchiveEngine.BossGoneTimeoutMs — the owner's stage-1 raid boss never reads
    // HP<=0 at all, so without this the ungated line alone could not tell the two apart on the next
    // field diagnosis. Reads "n/a" for every non-BossKill reason (the field is meaningless there — a
    // fixed sentinel, same pattern quietMs already uses, so the line's field COUNT never varies).
    private void LogArchiveOutcome(AutoArchive.ArchiveReason reason, string outcome, int statsCount, long durMs,
                                   IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)>? entryBosses = null)
    {
        var now = _services.CombatSnapshot.ServerNowMs;
        var quietMsText = _lastDamageMs == 0 ? "n/a" : (now - _lastDamageMs).ToString();
        var armedMs = IsDeferrableArchive(reason) && _pendingArchiveArmedMs != 0
            ? now - _pendingArchiveArmedMs
            : 0;
        var causeText = reason != AutoArchive.ArchiveReason.BossKill ? "n/a"
            : _autoArchive.BossKillWasTimeout ? "timeout" : "death";
        // flow=<state>#<version>: WHICH run-end state cut this archive, and how many transitions have been
        // seen. The stage trigger arms on a transition into ANY of End/Settlement/Vote and the game steps
        // through them in sequence, so one run end produced up to three archives (owner report 2026-07-30:
        // bosskill + 2x stage, ~5s apart, merged only by the Min-gap cooldown). The reason tag alone says
        // "stage" for all of them, so the log could not distinguish them and there was no evidence for
        // WHICH states actually fire in real content — which is what the per-stage settings need in order
        // to pick a default rather than guess. Read at archive time: the stage archive commits within
        // ~15-20ms of arming (see armedMs), so this is the arming state in practice.
        // The FULL stage-boss SET, DIAGNOSTICS-gated (2026-08-12 review, amendment 5 — replaces the
        // single-representative "segBoss=" mirror retired by Task 6): lists every member as
        // configId:killed:hpFrac so an accept run catches BOTH admission risks a single scalar could
        // hide — a co-boss NOT IsBoss-flagged (never admitted, so simply absent here) and an
        // IsBoss-flagged add joining the set (an extra pair appears, delaying the all-gone cut). Off in
        // production so the [archive] line's cost is unchanged; the base line stays ungated as before.
        // A BANKED archive passes its ENTRY's latched StageBosses (owner pre-production checklist
        // 2026-08-13, item 3): by the time a deferred boss-kill archive logs, BossStatus's
        // DrainIfAllGone has already emptied the live set (and a scene archive runs AFTER
        // ResetRunScopedTrackers), so formatting the live set printed bosses=[] while the entry it
        // just banked carried the real members — the exact drain the entry's own sticky-latch
        // snapshot exists to survive (Plugin.BossDetection.cs, _segmentStageBosses).
        var segText = !StellarDiagnostics.IsEnabled ? ""
            : $" bosses={(entryBosses is null ? FormatStageBosses() : FormatStageBosses(entryBosses, _memberLastHpFrac))}";
        _services.Log.Info(
            $"[CombatMeter][archive] {outcome} reason={ArchiveReasonTag(reason)} stats={statsCount} durMs={durMs} " +
            $"quietMs={quietMsText} armedMs={armedMs} settle={_archiveSettleMs} cause={causeText} " +
            $"flow={_services.Dungeon.CurrentFlowState}#{_services.Dungeon.FlowStateVersion}{segText}");
    }

    // Diagnostics-only formatter for the [archive] line's "bosses=" field (amendment 5). Never called
    // outside the StellarDiagnostics.IsEnabled gate above, so its Count/MemberAt loop + string building
    // is not a hot-path cost. hpFrac reads the SAME per-member map BossStatus feeds
    // (_memberLastHpFrac); -1 means the member was never seen with a valid HP reading.
    private string FormatStageBosses()
    {
        if (_stageBosses.Count == 0) return "[]";
        var sb = new System.Text.StringBuilder("[");
        for (var i = 0; i < _stageBosses.Count; i++)
        {
            var (id, configId, killed) = _stageBosses.MemberAt(i);
            var hpFrac = _memberLastHpFrac.TryGetValue(id, out var f) ? f : -1f;
            if (i > 0) sb.Append(',');
            sb.Append(configId).Append(':').Append(killed).Append(':').Append(hpFrac.ToString("0.###"));
        }
        return sb.Append(']').ToString();
    }

    // ENTRY-list overload: formats an archived entry's latched StageBosses (see the banked-line note
    // in LogArchiveOutcome above). hpFrac still reads the live per-member map — a member already
    // pruned at drain/reset reads -1 (killed:True is the meaningful field on such lines). Internal
    // static (pure) so the tests pin the format headless, same pattern as the tracker/guard tests.
    internal static string FormatStageBosses(
        IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> members,
        IReadOnlyDictionary<EntityId, float> lastHpFrac)
    {
        if (members.Count == 0) return "[]";
        var sb = new System.Text.StringBuilder("[");
        for (var i = 0; i < members.Count; i++)
        {
            var (id, configId, killed) = members[i];
            var hpFrac = lastHpFrac.TryGetValue(id, out var f) ? f : -1f;
            if (i > 0) sb.Append(',');
            sb.Append(configId).Append(':').Append(killed).Append(':').Append(hpFrac.ToString("0.###"));
        }
        return sb.Append(']').ToString();
    }

    // Remembers the last flow version SEEN here, so only real transitions log (this runs per tick).
    private int _loggedFlowVersion = -1;

    /// <summary>One line per dungeon flow-state TRANSITION. The archive outcome line only reveals the state
    /// at the moment an archive cuts, which leaves the states that armed the stage trigger and then lost to
    /// the Min-gap cooldown completely invisible — a cooldown-suppressed tick returns null and logs nothing.
    /// The owner's 2026-07-30 run showed <c>Playing#3 -> Settlement#5 -> None#7</c>: transitions 4 and 6
    /// happened unobserved, and whether they are End/Vote was inference, not measurement. The per-stage
    /// auto-archive settings need the real sequence to decide which stages to OFFER and which to default to;
    /// guessing risks shipping a control that matches nothing.
    ///
    /// <para>UNGATED, like the sibling [archive] line: bounded at a handful per run (a dungeon has ~7
    /// transitions), and it is the only record of WHY an archive did or did not cut.</para></summary>
    private void LogFlowTransition(DungeonFlowState state, int version)
    {
        if (version == _loggedFlowVersion) return;
        var previous = _loggedFlowVersion;
        _loggedFlowVersion = version;
        if (previous < 0) return;   // first observation adopts silently, matching the engine's own rule
        _services.Log.Info($"[CombatMeter][flow] {state}#{version} (was #{previous})");
    }

    // One line per run-boundary COMMIT (rb-task-3, spec 2026-08-12-combatmeter-run-boundary-design.md):
    // which layer fired (scene = OnSceneChanged's always-firing scene archive; poll-runid =
    // PollRunBoundary's missed-scene-event heal via RunBoundaryCore, now B-mode-live post rb-task-4;
    // combat-belt = Plugin.Capture.cs's OnCombatEvent resolving an already-ARMed boundary the instant a
    // real combat event proves the load already resolved, ahead of the next poll tick) and the old/new
    // run id it committed across. Deliberately UNGATED Info, same reasoning as LogArchiveOutcome: a run boundary
    // is a rare per-run lifecycle event and its evidence trail (which path banked the outgoing run,
    // under which id) is exactly what field diagnosis of a merged/split run needs — gating it behind
    // StellarDiagnostics.IsEnabled would leave the owner's normal log with zero record of which layer
    // fired. Reuses FormatStageBosses() (added by the multi-boss Task 6) when the stage-boss set is
    // non-empty. MINOR 8 (final review): both callers now log BEFORE their respective reset clears
    // _stageBosses — PollRunBoundary already did (Plugin.RunBoundary.cs, before RunBoundaryCore's
    // ResetRunScopedTrackers); OnSceneChanged (Plugin.History.cs) now does too, so a scene-sourced line
    // can carry bosses= exactly like poll-runid, instead of always logging an already-emptied set.
    private void LogRunBoundary(string source, long oldId, long newId, int statsCount)
    {
        var bossesText = _stageBosses.Count > 0 ? $" bosses={FormatStageBosses()}" : "";
        _services.Log.Info(
            $"[CombatMeter][boundary] source={source} old={oldId} new={newId} stats={statsCount}{bossesText}");
    }

    // Mid-dungeon-relaunch recovery (Plugin.RelaunchMarker.cs) — diagnostics-gated (owner ruling 2026-08-16:
    // keep these off the production log). A restore means a relaunch/crash mid-run was stitched back onto its
    // original server run instead of splitting into a second page.
    private void LogRelaunchRestore(long runId, long dungeonStartMs)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[CombatMeter][relaunch] restored dungeonStart={dungeonStartMs} for run={runId} (mid-run relaunch continued, not split)");
    }

    // Diagnostics-gated — the marker was for a run we are no longer in (kicked to town / timed out); cleared
    // so a later re-entry of the same instance is a fresh run.
    private void LogRelaunchStaleClear(long markerRunId, long currentRunId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[CombatMeter][relaunch] cleared stale marker run={markerRunId} (now settled out, currentRun={currentRunId})");
    }

    // Diagnostics-gated trace of what LoadActiveRunMarker read at startup — confirms a prior session wrote a
    // marker and this session read it across the relaunch (or that there was none: a clean session).
    private void LogRelaunchMarkerLoaded(AutoArchive.ActiveRunMarker? marker)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(marker is { } m
            ? $"[CombatMeter][relaunch] loaded marker level={m.LevelUuid} party={m.PartyId} start={m.DungeonStartMs} alive={m.LastAliveMs}"
            : "[CombatMeter][relaunch] no marker on disk (clean session)");
    }

    // Diagnostics-gated: the exact run-identity an archive will UPLOAD (levelUuid + dungeon-start + party),
    // so a relaunch re-test is verifiable straight from the client log — not inferred from the server.
    private void LogArchiveIdentity(EncounterHistoryEntry entry)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[CombatMeter][archive-id] levelUuid={entry.LevelUuid} dungeonStartMs={entry.DungeonStartMs} " +
            $"runStartS={entry.DungeonStartMs / 1000} partyId={entry.PartyId} trigger={entry.Trigger}");
    }

    // Diagnostics-gated: a marker EXISTED but the restore declined — dump marker vs live so the reason
    // (instance/party mismatch or stale gap) is readable without another blind test cycle.
    private void LogRelaunchDeclined(AutoArchive.ActiveRunMarker m, long currentRunId, long currentPartyId, long nowMs)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[CombatMeter][relaunch] restore DECLINED — marker(level={m.LevelUuid} party={m.PartyId} start={m.DungeonStartMs} alive={m.LastAliveMs}) " +
            $"vs live(run={currentRunId} party={currentPartyId} now={nowMs} gapMs={nowMs - m.LastAliveMs})");
    }

    // One line when AutoArchive.KilledBossTracker evicts its OLDEST mark to make room for a new one
    // (review round, 2026-07-26) — deliberately UNGATED Warning, same reasoning as LogArchiveOutcome
    // above: the standing rule is diagnostics stay gated behind StellarDiagnostics.IsEnabled, but a
    // saturation here means a correctness guarantee just weakened (the evicted boss id becomes
    // re-adoptable again — the exact loop this tracker exists to close), and hitting 64 distinct
    // confirmed-dead bosses in one run is rarer than an ordinary archive. That combination — rare +
    // correctness-relevant — puts it with the sanctioned ungated [archive] line rather than behind the
    // per-event diagnostics gate.
    private void LogKilledBossEviction(EntityId evictedId, EntityId newlyMarkedId)
        => _services.Log.Warning(
            $"[CombatMeter][killed-boss] cap hit ({AutoArchive.KilledBossTracker.MaxEntries}) — evicted oldest mark id={evictedId.Value} to record id={newlyMarkedId.Value}; evicted id is re-adoptable again");
}
