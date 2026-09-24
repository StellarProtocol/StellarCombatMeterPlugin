using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.CombatMeter;

/// <summary>One resolved status effect for a member's 2×2 block (icon resolution happens later, in
/// Plugin.Debuffs). <c>SourceSkillId</c> is the live skill that applied it (ActiveBuff.SourceId when
/// SourceKind==Skill), used to render imagine-sourced effects as their Battle-Imagine card. <c>IsBuff</c> marks
/// a BUFF (green tile) vs a DEBUFF (red tile) — the block mixes both kinds (owner 2026-09-24).</summary>
public readonly record struct DebuffEntry(int BaseId, int Stacks, float RemainFraction, int SourceSkillId = 0, bool IsBuff = false, long ExpireMs = 0, int DurationMs = 0);

/// <summary>Up to twelve status effects (2 rows × up to 6 columns) + an overflow count for a member's block.</summary>
public readonly record struct DebuffStripResult(
    DebuffEntry E0, DebuffEntry E1, DebuffEntry E2, DebuffEntry E3,
    DebuffEntry E4, DebuffEntry E5, DebuffEntry E6, DebuffEntry E7,
    DebuffEntry E8, DebuffEntry E9, DebuffEntry E10, DebuffEntry E11, int Count, int Overflow)
{
    /// <summary>No effects.</summary>
    public static readonly DebuffStripResult Empty = default;

    /// <summary>Index accessor (0..11) for the kept entries.</summary>
    public DebuffEntry At(int i) => i switch
    {
        0 => E0, 1 => E1, 2 => E2, 3 => E3, 4 => E4, 5 => E5,
        6 => E6, 7 => E7, 8 => E8, 9 => E9, 10 => E10, _ => E11,
    };
}

/// <summary>
/// Pure builder for a member's party-focus status-effect strip: filter (buff OR debuff, via the
/// user-configurable <see cref="DebuffSelection"/>), stable FIFO order by CreateTimeMs, cap 4 + overflow,
/// remaining-duration fraction. Buffs and debuffs share the one selection (their id spaces don't overlap) and
/// are colour-coded per tile via <see cref="DebuffEntry.IsBuff"/>. Unity-free + testable.
/// </summary>
public static class DebuffStrip
{
    /// <summary>Default / minimum effect-cell count (2×2). The renderer supports up to <see cref="MaxCellsCap"/>.</summary>
    public const int MaxCells = 4;
    /// <summary>Hard cap on visible cells (2 rows × 6 columns).</summary>
    public const int MaxCellsCap = 12;

    /// <summary>Filters, orders and caps a member's live buffs+debuffs into up to <paramref name="maxCells"/>
    /// cells (the LAST cell is reserved for the overflow "+N" whenever the kept count exceeds it). Each kind is
    /// gated by its own selection: debuffs by <paramref name="debuffSel"/>, buffs by <paramref name="buffSel"/>.</summary>
    public static DebuffStripResult Build(
        IReadOnlyList<ActiveBuff> buffs, Func<int, BuffInfo?> getBuff,
        DebuffSelection debuffSel, DebuffSelection buffSel, long nowMs, int maxCells = MaxCells)
    {
        var kept = KeptSorted(buffs, getBuff, debuffSel, buffSel);
        if (kept.Count == 0) return DebuffStripResult.Empty;
        int cap = maxCells < 1 ? 1 : (maxCells > MaxCellsCap ? MaxCellsCap : maxCells);
        // The renderer sacrifices the LAST cell for "+N" whenever there's overflow, so reserve it here too —
        // otherwise the last kept effect is silently dropped without ever showing in the overflow count.
        int count = kept.Count <= cap ? kept.Count : cap - 1;
        int overflow = kept.Count - count;
        DebuffEntry E(int i) => i < count ? ToEntry(kept[i], nowMs) : default;
        return new DebuffStripResult(E(0), E(1), E(2), E(3), E(4), E(5), E(6), E(7),
                                     E(8), E(9), E(10), E(11), count, overflow);
    }

    /// <summary>The FULL filtered + stably-ordered effect list for a member (no 4-cell cap) — used by the
    /// click tooltip's "show all" view. Same filter/order as <see cref="Build"/>. Empty when nothing passes.</summary>
    public static List<DebuffEntry> BuildAll(
        IReadOnlyList<ActiveBuff> buffs, Func<int, BuffInfo?> getBuff,
        DebuffSelection debuffSel, DebuffSelection buffSel, long nowMs)
    {
        var kept = KeptSorted(buffs, getBuff, debuffSel, buffSel);
        var result = new List<DebuffEntry>(kept.Count);
        foreach (var k in kept) result.Add(ToEntry(k, nowMs));
        return result;
    }

    // Filter to the kept (ActiveBuff, isBuff) pairs and stably order them by CreateTimeMs.
    private static List<(ActiveBuff b, bool isBuff)> KeptSorted(
        IReadOnlyList<ActiveBuff> buffs, Func<int, BuffInfo?> getBuff, DebuffSelection debuffSel, DebuffSelection buffSel)
    {
        var kept = new List<(ActiveBuff, bool)>(buffs?.Count ?? 0);
        if (buffs == null || buffs.Count == 0) return kept;
        foreach (var b in buffs)
            if (Passes(b.BaseId, getBuff, debuffSel, buffSel, out var isBuff)) kept.Add((b, isBuff));
        StableSortByCreateTime(kept);
        return kept;
    }

    // Filter predicate: a buff OR debuff whose name/icon pass its OWN selection (debuffs → debuffSel, buffs →
    // buffSel). Outputs the kind (isBuff = NOT a debuff) so the strip can colour the tile.
    private static bool Passes(int baseId, Func<int, BuffInfo?> getBuff,
                              DebuffSelection debuffSel, DebuffSelection buffSel, out bool isBuff)
    {
        isBuff = false;
        if (getBuff(baseId) is not { } bi) return false;
        isBuff = !bi.IsDebuff;
        var sel = isBuff ? buffSel : debuffSel;
        return sel.ShouldShow(baseId, !string.IsNullOrEmpty(bi.Name), !string.IsNullOrEmpty(bi.IconPath));
    }

    // In-place insertion sort by CreateTimeMs: stable (only shifts on strictly-greater, so equal-time
    // entries keep input order) and allocation-free — List.Sort is unstable, LINQ OrderBy allocates.
    private static void StableSortByCreateTime(List<(ActiveBuff b, bool isBuff)> kept)
    {
        for (int i = 1; i < kept.Count; i++)
        {
            var cur = kept[i];
            int j = i - 1;
            while (j >= 0 && kept[j].b.CreateTimeMs > cur.b.CreateTimeMs)
            {
                kept[j + 1] = kept[j];
                j--;
            }
            kept[j + 1] = cur;
        }
    }

    private static DebuffEntry ToEntry(in (ActiveBuff b, bool isBuff) k, long nowMs)
    {
        var b = k.b;
        float remain = b.DurationMs <= 0 ? 1f
            : Math.Clamp((b.CreateTimeMs + b.DurationMs - nowMs) / (float)b.DurationMs, 0f, 1f);
        // Absolute expiry + duration so a consumer (the tooltip cooldown tile) can recompute the live countdown
        // each frame rather than freeze the value at snapshot time. 0 duration = permanent (no countdown).
        long expireMs = b.DurationMs <= 0 ? 0 : b.CreateTimeMs + b.DurationMs;
        // SourceKind 0 = Skill (EFightSource) -> SourceId is the applying skill id (the imagine for a lockout).
        return new DebuffEntry(b.BaseId, b.Stacks, remain, b.SourceKind == 0 ? b.SourceId : 0, k.isBuff, expireMs, b.DurationMs);
    }
}
