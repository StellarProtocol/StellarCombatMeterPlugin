using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// Display name for a damage-attributed id. A combat event's <c>SkillId</c> is a skill, bullet OR buff id
/// (<c>SyncDamageInfo.OwnerId</c> per DamageSource), and buff-sourced rows — sub-buffs, damage zones, procs — have no
/// localized name, so the framework labels them "Buff &lt;id&gt;" (Knowledge Base Buff-Tracking.md, 2026-11-17).
/// Order:
/// <list type="number">
///   <item>real skill name (the game's, else the framework's English one);</item>
///   <item>curated <c>overrides</c>;</item>
///   <item>real buff name (same rule — a buff the English table knows as unnamed is not "real");</item>
///   <item>English design name from the framework (<see cref="IGameText.GetDesignName"/>) + " (&lt;id&gt;)" — the id marks it
///         as a designer label, e.g. "Firmament Fall-child buff (2203291)"; the skill's when the id is a skill, else the buff's;</item>
///   <item>null → the caller's raw fallback.</item>
/// </list>
/// Fillers ("气刃突刺计数", "场地标记01") and the framework's "Buff/Skill &lt;id&gt;" count as no name
/// (<see cref="IGameText.IsPlaceholderName"/>). Pure — the caller looks the game names up and caches the result.
/// </summary>
internal static class SourceNameResolver
{
    /// <param name="id">The damage-attributed id.</param>
    /// <param name="skillName">The game's skill name for <paramref name="id"/>, or null when it is not a skill.</param>
    /// <param name="buffName">The game's buff name for <paramref name="id"/>, or null when it is not a buff.</param>
    /// <param name="overrides">Curated id → name map.</param>
    /// <param name="text">The framework's English game text (may not be ready yet: then only steps 1–3 apply).</param>
    internal static string? Resolve(int id, string? skillName, string? buffName, IReadOnlyDictionary<int, string> overrides, IGameText text)
    {
        if (skillName != null && RealName(GameTextKind.Skill, id, skillName, text) is { } skill) return skill;
        if (overrides.TryGetValue(id, out var curated)) return curated;
        if (buffName != null && RealName(GameTextKind.Buff, id, buffName, text) is { } buff) return buff;

        var design = (skillName != null ? text.GetDesignName(GameTextKind.Skill, id) : null)
                     ?? (buffName != null ? text.GetDesignName(GameTextKind.Buff, id) : null);
        return design != null ? design + " (" + id.ToString(CultureInfo.InvariantCulture) + ")" : null;
    }

    // The game's name, or the framework's English name when the game has none; null when the row is unnamed.
    private static string? RealName(GameTextKind kind, int id, string gameName, IGameText text)
    {
        var name = text.ResolveDisplayName(kind, id, gameName, out bool isDesign);
        return isDesign || text.IsPlaceholderName(name) ? null : name;
    }
}
