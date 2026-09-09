using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>
/// The game's own per-class colours, used when the active layout's Appearance says
/// <see cref="BarColorMode.Class"/>. Unity-free — safe to link into the net8 test project.
/// </summary>
/// <remarks>
/// <para>
/// Hues are sampled from the class crest flames in the owner's in-game crest sheet (owner reference
/// 2026-09-09: <i>"this is the right class color, the current class color that we used on logs site need to
/// change follow to original class color"</i>), lifted slightly so brown, blue and purple clear the 4.5:1
/// text floor on the logs site's dark panel. Shield Knight is absent from that sheet; the owner ruled
/// <i>"shield knight use yellow"</i>, so it takes the game's own <c>ProfessionTable.DpsPanelColor</c> gold.
/// </para>
/// <para>
/// <c>DpsPanelColor</c> was rejected as the primary source for the whole set: it gives Twin Striker and Beat
/// Performer the identical <c>#e99078</c>, which cannot carry a per-class chart.
/// </para>
/// <para>
/// These nine values MUST stay byte-for-byte identical to <c>classColor()</c> in the logs site
/// (<c>services/stellar-logs/site/src/lib/format.ts</c>) — the meter and the site are two views of one
/// palette, and a player comparing them must see the same colour per class. <c>ClassPaletteTests</c> pins
/// every hex; change both sides (and both test suites) or neither.
/// </para>
/// </remarks>
public static class ClassPalette
{
    // Keyed by the real game profession id. Ids 6, 7, 8 and 10 do not exist.
    private static readonly Dictionary<int, ColorRgba> Palette = new()
    {
        [1]  = ColorRgba.FromHex(0xA45CE6FF),   // Stormblade     · crest flame purple
        [2]  = ColorRgba.FromHex(0x3A95DCFF),   // Frost Mage     · crest flame blue
        [3]  = ColorRgba.FromHex(0xF04A35FF),   // Twin Striker   · crest flame red
        [4]  = ColorRgba.FromHex(0x2FD3D9FF),   // Wind Knight    · crest flame cyan
        [5]  = ColorRgba.FromHex(0x5CCF36FF),   // Verdant Oracle · crest flame green
        [9]  = ColorRgba.FromHex(0xB47A3EFF),   // Heavy Guardian · crest flame brown
        [11] = ColorRgba.FromHex(0xF2DC3AFF),   // Marksman       · crest flame yellow
        [12] = ColorRgba.FromHex(0xDBAF2FFF),   // Shield Knight  · DpsPanelColor gold (owner: yellow)
        [13] = ColorRgba.FromHex(0xF5761CFF),   // Beat Performer · crest flame orange
    };

    /// <summary>
    /// The class colour for a game profession id. Returns <c>false</c> for an unknown / unresolved id
    /// (0 and the non-existent 6, 7, 8, 10) — the caller then keeps the role colour rather than guessing.
    /// </summary>
    public static bool TryGet(int professionId, out ColorRgba color)
        => Palette.TryGetValue(professionId, out color);

    /// <summary>Every mapped profession id and its colour (read-only; for tests and diagnostics).</summary>
    public static IReadOnlyDictionary<int, ColorRgba> All => Palette;
}
