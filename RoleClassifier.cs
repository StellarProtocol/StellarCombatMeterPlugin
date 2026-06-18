using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>The broad role bucket a profession belongs to.</summary>
public enum Role { Dps, Tank, Healer }

/// <summary>
/// Maps a game profession id to a <see cref="Role"/> and its default UI colour.
/// Unity-free — safe to link into the net8 test project.
/// </summary>
public static class RoleClassifier
{
    // Tank profession ids known from recon.
    private static readonly System.Collections.Generic.HashSet<int> TankIds   = new() { 9, 12 };
    // Healer profession ids known from recon.
    private static readonly System.Collections.Generic.HashSet<int> HealerIds = new() { 5, 13 };

    /// <summary>Classify a profession id. Unknown ids default to <see cref="Role.Dps"/>.</summary>
    public static Role Classify(int profession)
    {
        if (TankIds.Contains(profession))   return Role.Tank;
        if (HealerIds.Contains(profession)) return Role.Healer;
        return Role.Dps;
    }

    /// <summary>Game-palette colour for a role (0xRRGGBBAA).</summary>
    public static ColorRgba DefaultColor(Role role) => role switch
    {
        Role.Tank   => ColorRgba.FromHex(0x1188D4FF),
        Role.Healer => ColorRgba.FromHex(0x00CC00FF),
        _           => ColorRgba.FromHex(0xE32424FF),  // Dps (default)
    };
}
