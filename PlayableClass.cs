using System.Collections.Generic;

namespace Stellar.CombatMeter;

/// <summary>
/// The plugin's ONE list of player-selectable classes, and the base-class resolution used wherever a
/// run's class is REPORTED. Unity-free — safe to link into the test project.
/// </summary>
/// <remarks>
/// <para>
/// Attribute 220 (<c>AttrProfessionId</c>) is not always the player's class. A Battle Imagine that
/// transforms the player (e.g. Lucy, Natsu) temporarily rewrites attr 220 to a
/// <c>ProfessionSystemTable</c> row that is not a playable class: 14 = "Lucy", 15 = "Natsu",
/// 8 = "Thunder Flash - Hand Cannon", 10 = "Ritual Dance of Shadowspirits…" (6/7 also seen). Owner
/// ruling 2026-09-25: <i>"Class #14 is from battle imagine using which transform user temporary and it
/// change user class. it suppose not to show on stats, it suppose to show original class."</i> Measured:
/// prod run 233654948275945472, uid 1032773, plugin 2.10.0 uploaded <c>professionId=15</c> with
/// <c>classSpans=[12,15]</c> — the run ended while transformed.
/// </para>
/// <para>
/// Kept separate from <see cref="ClassPalette"/> on purpose: the palette answers "which colour", this
/// answers "is it a class at all". Today they hold the same nine ids, but coupling them would let a
/// cosmetic palette edit silently change what the upload reports. When the game adds a class, add it
/// HERE (and the palette, and the logs site) — until then a new id reports 0/unknown, never a guess.
/// </para>
/// <para>
/// Capture stays raw: nothing here filters the attribute array or the class-span timeline — those
/// still record exactly what the game did (the server folds transform spans itself). This only picks
/// which class the run is REPORTED under.
/// </para>
/// </remarks>
public static class PlayableClass
{
    // Stormblade 1, Frost Mage 2, Twin Striker 3, Wind Knight 4, Verdant Oracle 5, Heavy Guardian 9,
    // Marksman 11, Shield Knight 12, Beat Performer 13. Everything else in ProfessionSystemTable is a
    // transform / weapon form, not a class.
    private static readonly HashSet<int> Ids = new() { 1, 2, 3, 4, 5, 9, 11, 12, 13 };

    /// <summary>True when <paramref name="professionId"/> is a player-selectable class.</summary>
    public static bool IsPlayable(long professionId)
        => professionId > 0 && professionId <= int.MaxValue && Ids.Contains((int)professionId);

    /// <summary>Every playable class id (read-only; for tests and diagnostics).</summary>
    public static IReadOnlyCollection<int> All => Ids;

    /// <summary>
    /// The class a run is reported under: <paramref name="attr220"/> when it is playable; otherwise
    /// (a Battle Imagine transform, or unset) the LAST playable class in the actor's class-span timeline
    /// (<paramref name="classSpanProf"/>, in play order); otherwise 0 = unknown. Never guesses.
    /// </summary>
    public static int ResolveBaseProfession(int attr220, IReadOnlyList<long>? classSpanProf)
    {
        if (IsPlayable(attr220)) return attr220;
        if (classSpanProf is null) return 0;
        for (var i = classSpanProf.Count - 1; i >= 0; i--)
            if (IsPlayable(classSpanProf[i])) return (int)classSpanProf[i];
        return 0;
    }

    /// <summary>The class a snapshot's actor is REPORTED under (upload <c>professionId</c> and the self
    /// loadout/equipment lookups): <see cref="ResolveBaseProfession"/> over the snapshot's raw attr 220
    /// and its baked class-span timeline. Reads only; never rewrites the snapshot.</summary>
    internal static int ResolveActorProfession(EntitySnapshot snap)
    {
        const int AttrProfessionId = 220;
        var attr220 = 0;
        for (var i = 0; i < snap.AttrIds.Length; i++)
            if (snap.AttrIds[i] == AttrProfessionId) { attr220 = (int)snap.AttrValues[i]; break; }
        return ResolveBaseProfession(attr220, snap.ClassSpanProf);
    }
}
