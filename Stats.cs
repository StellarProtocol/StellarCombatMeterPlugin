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
    public long TotalTaken;          // incoming damage to this entity
    public long TopHit;
    public int  Hits;
    public int  Crits;
    public int  Luckys;              // lucky-hit count (DamageDealt.IsLucky) — for Luck% alongside Crit%
    public int  CritLuckys;          // hits that were BOTH crit and lucky
    public int  Kills;
    public int  Deaths;              // times this entity died (killing-blow incoming hits)
    public long FirstHitMs;
    public long LastHitMs;

    // ZDPS-parity damage splits (values, not just counts) + shield break.
    public long CritDamage;          // damage dealt by crit hits (incl. crit-lucky)
    public long LuckyDamage;         // damage dealt by lucky hits (incl. crit-lucky)
    public long CritLuckyDamage;     // damage dealt by crit+lucky hits
    public long ShieldBreak;         // sum of ShieldAbsorbed on outgoing damage

    // Healing splits (mirror of the damage splits) + effectiveness.
    public int  HealHits;
    public int  HealCrits;
    public int  HealLuckys;
    public int  HealCritLuckys;
    public long CritHealing;
    public long LuckyHealing;
    public long CritLuckyHealing;
    public long TopHeal;             // max single heal
    public long EffectiveHealing;    // sum of ActualAmount on heals (total - effective = overheal)

    public Dictionary<int, SkillStats> BySkill = new();
    public Dictionary<int, IncomingSkillStats> IncomingBySkill = new();  // attacker-skill -> taken

    /// <summary>Field-complete deep copy — archive snapshots MUST use this, never hand-listed
    /// initializers (a hand copy silently dropped newly added fields from every upload).</summary>
    public SourceStats Clone()
    {
        var c = (SourceStats)MemberwiseClone();
        c.BySkill = new Dictionary<int, SkillStats>(BySkill.Count);
        foreach (var (k, v) in BySkill) c.BySkill[k] = v.Clone();
        c.IncomingBySkill = new Dictionary<int, IncomingSkillStats>(IncomingBySkill.Count);
        foreach (var (k, v) in IncomingBySkill) c.IncomingBySkill[k] = v.Clone();
        return c;
    }
}

/// <summary>Per-skill aggregate inside a <see cref="SourceStats"/> entry. Damage and healing keep
/// SEPARATE hit/crit/lucky counters — a hybrid skill's heal rows must not inherit its damage
/// counts (the pre-split upload builder did exactly that).</summary>
internal sealed class SkillStats
{
    public long Total;               // damage total
    public int  Hits;
    public int  Crits;
    public int  Luckys;
    public int  CritLuckys;          // hits that were BOTH crit and lucky
    public long TopHit;
    public long MinHit;              // smallest non-zero damage hit (0 = none yet)
    public int  Kills;               // killing blows dealt by this skill

    public long HealTotal;           // healing total for this skill
    public int  HealHits;
    public int  HealCrits;
    public int  HealLuckys;
    public long HealTop;

    /// <summary>Field-complete copy — archive deep-copies MUST use this, never hand-listed
    /// initializers (a hand copy silently dropped the v4 fields from every upload).</summary>
    public SkillStats Clone() => (SkillStats)MemberwiseClone();
}

/// <summary>Incoming damage to a source, grouped by the attacker's skill id (Taken-mode drill-in).</summary>
internal sealed class IncomingSkillStats
{
    public long Total;
    public int  Hits;
    public long TopHit;

    /// <summary>Field-complete copy (see <see cref="SkillStats.Clone"/>).</summary>
    public IncomingSkillStats Clone() => (IncomingSkillStats)MemberwiseClone();
}
