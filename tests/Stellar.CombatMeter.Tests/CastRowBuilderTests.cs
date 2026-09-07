using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>The cast row (rDPS phase 2, spec § 6.1.2 — controller decision D1): a player's cast is the write of
/// attribute 100 (AttrSkillId) in its AOI attr stream, the same signal ZDPS's Skill Cast Timeline fires on. It
/// becomes the EXISTING `skill` wire row with phase 101 (SkillBegin), stamped like the packet.</summary>
public sealed class CastRowBuilderTests
{
    static readonly EntityId Mate = new(0x0000_0002_0000_0280);

    static CombatEvent.EntityAttributesChanged Attrs(long ms, EntityId who, params (int id, long v)[] pairs)
    {
        var list = new List<AttrValue>();
        foreach (var (id, v) in pairs) list.Add(new AttrValue(id, v));
        return new CombatEvent.EntityAttributesChanged(ms, who, list);
    }

    [Fact]
    public void An_AttrSkillId_write_is_a_SkillBegin_row_for_that_entity()
    {
        var row = CastRowBuilder.Project(Attrs(77L, Mate, (11710, 3350), (100, 2313), (106, 1_788_000_000_000L)))!;
        Assert.Equal(77L, row.Ms);
        Assert.Equal(Mate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Src);
        Assert.Equal(2313, row.Skill);
        Assert.Equal((int)SkillEventPhase.Begin, row.Phase);
    }

    [Fact]
    public void No_AttrSkillId_or_a_non_positive_one_yields_no_row()
    {
        Assert.Null(CastRowBuilder.Project(Attrs(1L, Mate, (11710, 3350), (101, 2), (106, 5L))));   // stage/begin-time alone are not a cast
        Assert.Null(CastRowBuilder.Project(Attrs(1L, Mate, (100, 0))));
        Assert.Null(CastRowBuilder.Project(Attrs(1L, Mate, (100, -5))));
        Assert.Null(CastRowBuilder.Project(Attrs(1L, Mate, (100, (long)int.MaxValue + 1))));
    }
}
