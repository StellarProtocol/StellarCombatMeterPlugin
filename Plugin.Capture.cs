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

    // Healing accrues to the source's total + per-skill heal total + healing timeline.
    private void CaptureHeal(SourceStats s, CombatEvent.DamageDealt d)
    {
        s.TotalHealing += d.Amount;
        if (!s.BySkill.TryGetValue(d.SkillId, out var hsk)) { hsk = new SkillStats(); s.BySkill[d.SkillId] = hsk; }
        hsk.HealTotal += d.Amount;
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
        if (d.IsCrit) s.Crits += 1;
        if (d.IsLucky) s.Luckys += 1;
        if (d.IsDead) s.Kills += 1;
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
        if (d.Amount > sk.TopHit) sk.TopHit = d.Amount;
    }

    // Spec comes from the framework's shared cast-resolved cache (ICombatSpec): the framework recognises
    // spec-defining skill ids as events flow, so every consumer (this meter + EntityInspector) agrees. The
    // meter no longer infers spec from the AOI loadout (that carries both specs' signature skills →
    // mislabelled players, e.g. Falconry shown as Wildpack). Returns 0 until a spec-defining skill is cast.
    private int ResolveSpec(EntityId id) => _services.CombatSpec.GetSubProfession(id);

    // Feed the inferred-others cooldown tracker when a Battle-Imagine cast is seen (all players incl.
    // self — harmless, self display uses LocalCooldowns). GetImagineForSkill is null for non-imagine
    // skills. Multi-charge skills recharge on EnergyChargeTime; single-charge on the per-cast cooldown.
    private void ObserveResonanceCast(CombatEvent.DamageDealt d)
    {
        if (!d.SourceId.IsPlayer) return;
        if (_services.ResonanceData.GetImagineForSkill(d.SkillId) is not { } info) return;
        LogImagineCast(d.SourceId, d.SkillId, info.SkillId);   // TEMP capture: is the cast seen? how many hits?
        int ms = info.ChargeCount > 1 ? info.RechargeMs : info.CooldownMs;
        // Key by the BASE imagine skill id (info.SkillId), not the leveled cast id (d.SkillId), so OtherSlot —
        // which looks the tracker up by the equipped loadout's base id — finds it.
        _resTracker.OnCast(d.SourceId, info.SkillId, info.ChargeCount, ms, _services.CombatSnapshot.ServerNowMs);
    }
}
