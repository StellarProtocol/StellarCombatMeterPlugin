using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.CombatMeter;

/// <summary>One resolved debuff for a member's 2×2 block (icon resolution happens later, in Plugin.Debuffs).</summary>
/// <summary>One resolved debuff. <c>SourceSkillId</c> is the live skill that applied it (ActiveBuff.SourceId
/// when SourceKind==Skill), used to render imagine-lockout debuffs as their Battle-Imagine card.</summary>
public readonly record struct DebuffEntry(int BaseId, int Stacks, float RemainFraction, int SourceSkillId = 0);

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
/// Pure builder for a member's party-focus debuff strip: filter (IsDebuff + user-configurable
/// <see cref="DebuffSelection"/>), stable FIFO order by CreateTimeMs, cap 4 + overflow, remaining-duration
/// fraction. Unity-free + testable.
/// </summary>
public static class DebuffStrip
{
    /// <summary>Maximum debuff cells rendered per member.</summary>
    public const int MaxCells = 4;

    /// <summary>Filters, orders and caps a member's live buffs into the up-to-four debuff strip (4th cell reserved for the overflow "+N" whenever the kept count exceeds <see cref="MaxCells"/>).</summary>
    public static DebuffStripResult Build(
        IReadOnlyList<ActiveBuff> buffs, Func<int, BuffInfo?> getBuff,
        DebuffSelection selection, long nowMs)
    {
        if (buffs == null || buffs.Count == 0) return DebuffStripResult.Empty;
        var kept = new List<ActiveBuff>(buffs.Count);
        foreach (var b in buffs)
            if (Passes(b.BaseId, getBuff, selection)) kept.Add(b);
        if (kept.Count == 0) return DebuffStripResult.Empty;
        StableSortByCreateTime(kept);
        // Renderer sacrifices the 4th cell for "+N" whenever there's overflow, so reserve it here too —
        // otherwise the last kept debuff is silently dropped without ever showing in the overflow count.
        int count = kept.Count <= MaxCells ? kept.Count : MaxCells - 1;
        int overflow = kept.Count - count;
        var e0 = 0 < count ? ToEntry(kept[0], nowMs) : default;
        var e1 = 1 < count ? ToEntry(kept[1], nowMs) : default;
        var e2 = 2 < count ? ToEntry(kept[2], nowMs) : default;
        var e3 = 3 < count ? ToEntry(kept[3], nowMs) : default;
        return new DebuffStripResult(e0, e1, e2, e3, count, overflow);
    }

    // The single filter predicate: a real debuff (BuffType 0) whose name/icon pass the user's
    // DebuffSelection (default mode + empty selection shows every named, icon'd debuff).
    private static bool Passes(int baseId, Func<int, BuffInfo?> getBuff, DebuffSelection selection)
    {
        var info = getBuff(baseId);
        if (info is not { IsDebuff: true } bi) return false;
        return selection.ShouldShow(baseId, !string.IsNullOrEmpty(bi.Name), !string.IsNullOrEmpty(bi.IconPath));
    }

    // In-place insertion sort by CreateTimeMs: stable (only shifts on strictly-greater, so equal-time
    // entries keep input order) and allocation-free — List.Sort is unstable, LINQ OrderBy allocates.
    // Fine for the tiny per-member kept list on this ~200/sec hot path (raid-20).
    private static void StableSortByCreateTime(List<ActiveBuff> kept)
    {
        for (int i = 1; i < kept.Count; i++)
        {
            var cur = kept[i];
            int j = i - 1;
            while (j >= 0 && kept[j].CreateTimeMs > cur.CreateTimeMs)
            {
                kept[j + 1] = kept[j];
                j--;
            }
            kept[j + 1] = cur;
        }
    }

    private static DebuffEntry ToEntry(in ActiveBuff b, long nowMs)
    {
        float remain = b.DurationMs <= 0 ? 1f
            : Math.Clamp((b.CreateTimeMs + b.DurationMs - nowMs) / (float)b.DurationMs, 0f, 1f);
        // SourceKind 0 = Skill (EFightSource) -> SourceId is the applying skill id (the imagine for a lockout debuff).
        return new DebuffEntry(b.BaseId, b.Stacks, remain, b.SourceKind == 0 ? b.SourceId : 0);
    }
}
