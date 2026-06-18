using System.Collections.Generic;

namespace Stellar.CombatMeter;

/// <summary>
/// Per-source aggregate. Mutated only on the Unity main thread from
/// <see cref="Plugin.OnCombatEvent"/>; read from the same thread during
/// <see cref="Plugin.OnDraw"/>. No locks required.
/// </summary>
internal sealed class SourceStats
{
    public long TotalDamage;
    public long TotalHealing;
    public long TotalTaken;          // NEW: incoming damage to this entity
    public long TopHit;
    public int  Hits;
    public int  Crits;
    public int  Luckys;              // NEW: lucky-hit count (DamageDealt.IsLucky) — for Luck% alongside Crit%
    public int  Kills;
    public long FirstHitMs;
    public long LastHitMs;
    public Dictionary<int, SkillStats> BySkill = new();
    public Dictionary<int, IncomingSkillStats> IncomingBySkill = new();  // NEW: attacker-skill -> taken
}

/// <summary>Per-skill aggregate inside a <see cref="SourceStats"/> entry.</summary>
internal sealed class SkillStats
{
    public long Total;               // damage total
    public long HealTotal;           // NEW: healing total for this skill
    public int  Hits;
    public int  Crits;
    public int  Luckys;              // NEW: lucky-hit count for this skill
    public long TopHit;
}

/// <summary>Incoming damage to a source, grouped by the attacker's skill id (Taken-mode drill-in).</summary>
internal sealed class IncomingSkillStats
{
    public long Total;
    public int  Hits;
    public long TopHit;
}
