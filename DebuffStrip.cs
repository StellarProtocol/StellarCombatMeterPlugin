using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.CombatMeter;

/// <summary>One resolved debuff for a member's 2×2 block (icon resolution happens later, in Plugin.Debuffs).</summary>
public readonly record struct DebuffEntry(int BaseId, int Stacks, float RemainFraction);

/// <summary>Up to four debuffs + an overflow count for a member's trailing block.</summary>
public readonly record struct DebuffStripResult(
    DebuffEntry E0, DebuffEntry E1, DebuffEntry E2, DebuffEntry E3, int Count, int Overflow)
{
    /// <summary>No debuffs.</summary>
    public static readonly DebuffStripResult Empty = default;

    /// <summary>Index accessor (0..3) for the kept entries.</summary>
    public DebuffEntry At(int i) => i switch { 0 => E0, 1 => E1, 2 => E2, _ => E3 };
}

/// <summary>
/// Pure builder for a member's party-focus debuff strip: filter (IsDebuff + has-icon − denylist), stable
/// FIFO order by CreateTimeMs, cap 4 + overflow, remaining-duration fraction. Unity-free + testable.
/// </summary>
public static class DebuffStrip
{
    /// <summary>Maximum debuff cells rendered per member.</summary>
    public const int MaxCells = 4;

    public static DebuffStripResult Build(
        IReadOnlyList<ActiveBuff> buffs, Func<int, BuffInfo?> getBuff,
        IReadOnlyCollection<int> denylist, long nowMs)
    {
        if (buffs == null || buffs.Count == 0) return DebuffStripResult.Empty;
        var kept = new List<ActiveBuff>(buffs.Count);
        foreach (var b in buffs)
        {
            if (denylist.Contains(b.BaseId)) continue;
            var info = getBuff(b.BaseId);
            if (info is not { IsDebuff: true } bi) continue;
            if (string.IsNullOrEmpty(bi.IconPath)) continue;
            kept.Add(b);
        }
        if (kept.Count == 0) return DebuffStripResult.Empty;
        var ordered = kept.OrderBy(b => b.CreateTimeMs).ToList();   // stable FIFO (Enumerable.OrderBy is documented-stable; List.Sort is not)
        int count = Math.Min(ordered.Count, MaxCells);
        int overflow = ordered.Count - count;
        DebuffEntry E(int i) => i < count ? ToEntry(ordered[i], nowMs) : default;
        return new DebuffStripResult(E(0), E(1), E(2), E(3), count, overflow);
    }

    private static DebuffEntry ToEntry(in ActiveBuff b, long nowMs)
    {
        float remain = b.DurationMs <= 0 ? 1f
            : Math.Clamp((b.CreateTimeMs + b.DurationMs - nowMs) / (float)b.DurationMs, 0f, 1f);
        return new DebuffEntry(b.BaseId, b.Stacks, remain);
    }
}
