using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.CombatMeter;

// Combat-event capture region: the OnCombatEvent dispatch and its per-channel accumulators
// (dealt / heal / taken), plus the spec + resonance observe helpers. Split out of Plugin.cs to
// keep each file under the 500-LoC cap (Phase 3 adds more here). Behaviour is identical.
public sealed partial class Plugin
{
    private void OnCombatEvent(CombatEvent evt)
    {
        // SP1: capture every event into the log buffer (runs even when the meter display is paused).
        MaybeCaptureForLog(evt);

        if (_paused) return;
        if (evt is CombatEvent.SkillUsed su) { LogSkillUsed(su); return; }   // TEMP capture (cast-time redesign)
        if (evt is not CombatEvent.DamageDealt d) return;

        // Establish combat start from the FIRST event of ANY channel (dealt / heal / taken). Previously
        // the latch lived in AccumulateDamage, so an encounter that opened with a heal or an incoming hit
        // dropped those events from the timeline (the `if (_combatActive)` guards were unsatisfied) and
        // skewed bucket indices against a later, damage-established start. Hoisting it here fixes that for
        // healers/tanks. When damage is the first event, _combatStartMs is identical to the old behaviour.
        EnsureCombatStarted(d.TimestampMs);

        _agg.AddDamage(d.SourceId, d.Amount, d.IsHeal);

        // Damage taken: accrue onto the TARGET's stats (so Taken-mode can rank/aggregate victims).
        if (!d.IsHeal && d.TargetId.IsPlayer) CaptureTaken(d);

        // Replay: note both source and target BEFORE the player-only early-out so boss/add target ids
        // (e.g. a mob being hit by a player) also enter the entity set for position tracking.
        NoteReplayEntity(d.SourceId, d.TargetId);

        // Per-source stats/timeline: PLAYERS ONLY — mirror the _agg guard above. Mob sources are never
        // shown (live rows come from _agg, which discards non-players; History/SkillBreakdown are
        // player-focused), so a SourceStats (2 dicts) + SourceTimeline (3 dicts) per mob was pure dead
        // weight. In a multi-round dungeon that grew _stats/_timelines with every mob ever seen (Clear()
        // only fires on scene change/archive, never between rounds), ballooning the managed heap and
        // driving the GC-pressure FPS decay users hit at the same dungeon spot each round.
        if (!d.SourceId.IsPlayer) return;

        var s = StatsFor(d.SourceId);

        ObserveResonanceCast(d);
        if (d.IsHeal) { CaptureHeal(s, d); return; }
        AccumulateDamage(s, d);
    }

    // Combat-start latch. Set once by the first combat event of any channel; reset by Clear().
    private void EnsureCombatStarted(long timestampMs)
    {
        if (_combatActive) return;
        _combatActive  = true;
        _combatStartMs = timestampMs;
        // Latch the dungeon run-id mid-run (valid here) as a fallback: IDungeonState.CurrentRunId can reset
        // to 0 on scene-leave, which may be exactly when the archive fires. ManualArchive uses this if the
        // live id is already 0 at archive time.
        _lastRunId     = _services.Dungeon.CurrentRunId;
    }

    // Get-or-create the per-source aggregate.
    private SourceStats StatsFor(EntityId id)
    {
        if (!_stats.TryGetValue(id, out var s))
        {
            s = new SourceStats();
            _stats[id] = s;
        }
        return s;
    }

    // Healing accrues to the source's total + per-skill heal counters + healing timeline.
    // Heal hits/crits/luckys are tracked SEPARATELY from the damage counters — the upload's heal
    // breakdown previously borrowed the damage-path Hits/Crits (0/0 for pure heals, wrong for hybrids).
    private void CaptureHeal(SourceStats s, CombatEvent.DamageDealt d)
    {
        s.TotalHealing += d.Amount;
        s.HealHits += 1;
        if (d.IsCrit) { s.HealCrits += 1; s.CritHealing += d.Amount; }
        if (d.IsLucky) { s.HealLuckys += 1; s.LuckyHealing += d.Amount; }
        if (d.IsCrit && d.IsLucky) { s.HealCritLuckys += 1; s.CritLuckyHealing += d.Amount; }
        if (d.Amount > s.TopHeal) s.TopHeal = d.Amount;
        s.EffectiveHealing += d.ActualAmount;   // real applied heal; total − effective = overheal
        if (!s.BySkill.TryGetValue(d.SkillId, out var hsk)) { hsk = new SkillStats(); s.BySkill[d.SkillId] = hsk; }
        hsk.HealTotal += d.Amount;
        hsk.HealHits  += 1;
        if (d.IsCrit) hsk.HealCrits += 1;
        if (d.IsLucky) hsk.HealLuckys += 1;
        if (d.Amount > hsk.HealTop) hsk.HealTop = d.Amount;
        if (_combatActive) TimelineFor(d.SourceId).Add(TimelineChannel.Healing, d.TimestampMs, _combatStartMs, d.Amount);
    }

    // Incoming damage to the target: total taken + per-attacker-skill breakdown + taken timeline.
    private void CaptureTaken(CombatEvent.DamageDealt d)
    {
        // Taken uses d.Amount (the gross Value/HpLessen/Lucky-precedence field the DPS path uses), NOT
        // d.ActualAmount — ActualValue is usually 0 on the wire, so Taken always read 0 in a long fight.
        _agg.AddTaken(d.TargetId, d.Amount);
        var ts = StatsFor(d.TargetId);
        ts.TotalTaken += d.Amount;
        if (d.IsDead) { ts.Deaths += 1; _deaths.Add(new DeathEntry(d.TimestampMs, d.TargetId, d.SkillId)); }
        if (!ts.IncomingBySkill.TryGetValue(d.SkillId, out var inc)) { inc = new IncomingSkillStats(); ts.IncomingBySkill[d.SkillId] = inc; }
        inc.Total += d.Amount; inc.Hits += 1; if (d.Amount > inc.TopHit) inc.TopHit = d.Amount;
        if (_combatActive) TimelineFor(d.TargetId).Add(TimelineChannel.Taken, d.TimestampMs, _combatStartMs, d.Amount);
    }

    private void AccumulateDamage(SourceStats s, CombatEvent.DamageDealt d)
    {
        _lastDamageMs = d.TimestampMs;

        s.TotalDamage += d.Amount;
        TimelineFor(d.SourceId).Add(TimelineChannel.Dealt, d.TimestampMs, _combatStartMs, d.Amount);
        s.Hits        += 1;
        if (d.IsCrit) { s.Crits += 1; s.CritDamage += d.Amount; }
        if (d.IsLucky) { s.Luckys += 1; s.LuckyDamage += d.Amount; }
        if (d.IsCrit && d.IsLucky) { s.CritLuckys += 1; s.CritLuckyDamage += d.Amount; }
        if (d.IsDead) s.Kills += 1;
        s.ShieldBreak += d.ShieldAbsorbed;
        if (d.Amount > s.TopHit) s.TopHit = d.Amount;
        if (s.FirstHitMs == 0) s.FirstHitMs = d.TimestampMs;
        s.LastHitMs = d.TimestampMs;

        if (!s.BySkill.TryGetValue(d.SkillId, out var sk))
        {
            sk = new SkillStats();
            s.BySkill[d.SkillId] = sk;
        }
        sk.Total += d.Amount;
        sk.Hits  += 1;
        if (d.IsCrit) sk.Crits += 1;
        if (d.IsLucky) sk.Luckys += 1;
        if (d.IsCrit && d.IsLucky) sk.CritLuckys += 1;
        if (d.IsDead) sk.Kills += 1;
        if (d.Amount > sk.TopHit) sk.TopHit = d.Amount;
        if (d.Amount > 0 && (sk.MinHit == 0 || d.Amount < sk.MinHit)) sk.MinHit = d.Amount;
    }

    // Spec comes from the framework's shared cast-resolved cache (ICombatSpec): the framework recognises
    // spec-defining skill ids as events flow, so every consumer (this meter + EntityInspector) agrees. The
    // meter no longer infers spec from the AOI loadout (that carries both specs' signature skills →
    // mislabelled players, e.g. Falconry shown as Wildpack). Returns 0 until a spec-defining skill is cast.
    private int ResolveSpec(EntityId id) => StickySpec(id, _services.CombatSpec.GetSubProfession(id));

    // Timeline cast log: one entry per cast (multi-hit imagines collapse via the per-source window);
    // capped as a runaway guard — 20 players × a cast every ~30s stays far below the cap for any fight.
    private const int MaxImagineCasts = 600;
    private const long ImagineCastDedupMs = 5000;

    private void RecordImagineCast(EntityId src, int baseSkillId, long ms)
    {
        if (_imagineCasts.Count >= MaxImagineCasts) return;
        var key = (src, baseSkillId);
        if (_lastImagineCastMs.TryGetValue(key, out var last) && ms - last < ImagineCastDedupMs) return;
        _lastImagineCastMs[key] = ms;
        _imagineCasts.Add(new ImagineCastEntry(ms, src, baseSkillId));
    }

    // Feed the inferred-others cooldown tracker when a Battle-Imagine cast is seen (all players incl.
    // self — harmless, self display uses LocalCooldowns). GetImagineForSkill is null for non-imagine
    // skills. Multi-charge skills recharge on EnergyChargeTime; single-charge on the per-cast cooldown.
    private void ObserveResonanceCast(CombatEvent.DamageDealt d)
    {
        if (!d.SourceId.IsPlayer) return;
        if (_services.ResonanceData.GetImagineForSkill(d.SkillId) is not { } info) return;
        LogImagineCast(d.SourceId, d.SkillId, info.SkillId);   // TEMP capture: is the cast seen? how many hits?
        RecordImagineCast(d.SourceId, info.SkillId, d.TimestampMs);
        int ms = info.ChargeCount > 1 ? info.RechargeMs : info.CooldownMs;
        // Key by the BASE imagine skill id (info.SkillId), not the leveled cast id (d.SkillId), so OtherSlot —
        // which looks the tracker up by the equipped loadout's base id — finds it.
        _resTracker.OnCast(d.SourceId, info.SkillId, info.ChargeCount, ms, _services.CombatSnapshot.ServerNowMs);
    }
}
