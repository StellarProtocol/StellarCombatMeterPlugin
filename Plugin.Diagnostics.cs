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
    // settle window (the pending waits out ArchiveIdleSettleMs of no combat events).
    private void LogAutoArchiveFired(AutoArchive.ArchiveReason reason, in AutoArchive.AutoArchiveInputs s)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[CombatMeter][auto-archive] fired reason={ArchiveReasonTag(reason)} dead={s.DeadCount}/{s.RosterSize} unknown={s.UnknownCount} " +
            $"idleMs={(s.LastDamageMs > 0 ? s.NowMs - s.LastDamageMs : 0)} flowVer={s.FlowStateVersion} run={s.InstancedRun}");
    }

    // One line per deferred AUTO archive that actually commits after the idle-settle wait — pair it
    // with the preceding [auto-archive] fired line to confirm the quiet-window gap in-game. quietMs is
    // how long combat had been silent (all channels) at commit; armedMs is the wait since the trigger.
    private void LogAutoArchiveCommit(AutoArchive.ArchiveReason reason, long nowMs)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[CombatMeter][auto-archive] commit reason={ArchiveReasonTag(reason)} now={nowMs} " +
            $"quietMs={nowMs - _lastCombatEventMs} armedMs={nowMs - _pendingArchiveArmedMs} settle={ArchiveIdleSettleMs}");
    }

    // One line per ManualArchive ATTEMPT with its outcome (skip-empty | suppressed | banked |
    // banked+upload) — deliberately UNGATED Info, like the SP1 "Uploading log" line: archives are
    // rare per-run lifecycle events, and their SILENT skip variants are exactly what field
    // debugging needs (2026-07-19: a full dungeon run produced no history entry and no upload
    // with zero log evidence; the overwritten-on-boot BepInEx log then destroyed the trail).
    private void LogArchiveOutcome(AutoArchive.ArchiveReason reason, string outcome, int statsCount, long durMs)
        => _services.Log.Info(
            $"[CombatMeter][archive] {outcome} reason={ArchiveReasonTag(reason)} stats={statsCount} durMs={durMs} " +
            $"flow={_services.Dungeon.CurrentFlowState} outcome={_services.Dungeon.LastOutcome} " +
            $"pass={_services.Dungeon.LastSettlement?.PassTimeSeconds ?? 0} result={CarriesRunResult()}");
}
