using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Pins the OWNER-APPROVED class palette (design 2026-09-09-game-class-palette-and-gauge-color-design § 2,
/// owner "go"): the nine hexes are sampled from the game's class crest flames, with Shield Knight taking the
/// game's own DpsPanelColor gold ("shield knight use yellow").
///
/// These values MUST equal classColor() in services/stellar-logs/site/src/lib/format.ts byte-for-byte — the
/// meter and the logs site are two views of ONE palette. If a hex here changes, the site's own
/// format.test.ts pins must change in the same breath, and vice versa. Do not "tidy" a value.
/// </summary>
public class ClassPaletteTests
{
    // Rendered back to the "#rrggbb" form the site uses, so a mismatch reads as the actual hex in the
    // failure message rather than as four floats.
    private static string Hex(ColorRgba c)
    {
        static int B(float v) => (int)System.Math.Round(v * 255f);
        return string.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}", B(c.R), B(c.G), B(c.B));
    }

    // id → hex, exactly as the design table and the site's classColor() carry them.
    public static TheoryData<int, string> ApprovedPalette => new()
    {
        {  1, "#a45ce6" },   // Stormblade
        {  2, "#3a95dc" },   // Frost Mage
        {  3, "#f04a35" },   // Twin Striker
        {  4, "#2fd3d9" },   // Wind Knight
        {  5, "#5ccf36" },   // Verdant Oracle
        {  9, "#b47a3e" },   // Heavy Guardian
        { 11, "#f2dc3a" },   // Marksman
        { 12, "#dbaf2f" },   // Shield Knight
        { 13, "#f5761c" },   // Beat Performer
    };

    [Theory]
    [MemberData(nameof(ApprovedPalette))]
    public void Each_class_resolves_to_its_approved_hex(int professionId, string expectedHex)
    {
        Assert.True(ClassPalette.TryGet(professionId, out var color), $"profession {professionId} unmapped");
        Assert.Equal(expectedHex, Hex(color));
    }

    [Fact]
    public void Every_class_colour_is_fully_opaque()
    {
        foreach (var kv in ClassPalette.All) Assert.Equal(1f, kv.Value.A);
    }

    [Fact]
    public void The_palette_holds_exactly_the_nine_real_professions()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 9, 11, 12, 13 }, ClassPalette.All.Keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public void All_nine_colours_are_distinct()
    {
        // A per-class chart is unreadable the moment two classes share a hue — this is why the game's
        // DpsPanelColor was rejected as the source (Twin Striker and Beat Performer share #e99078 there).
        var hexes = new HashSet<string>();
        foreach (var kv in ClassPalette.All)
            Assert.True(hexes.Add(Hex(kv.Value)), $"duplicate colour {Hex(kv.Value)} on profession {kv.Key}");
        Assert.Equal(9, hexes.Count);
    }

    // 0 = unresolved; 6, 7, 8 and 10 are profession ids that do not exist in the game (the logs site's
    // legacy palette mapped them, which is part of what this design corrected).
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(99)]
    [InlineData(-1)]
    public void An_unknown_profession_does_not_resolve(int professionId)
    {
        Assert.False(ClassPalette.TryGet(professionId, out var color));
        Assert.Equal(default, color);
    }
}
