using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class CombatLogEventConverterTests
{
    [Fact]
    public void BuffChanged_maps_firer_and_source()
    {
        var ev = new CombatEvent.BuffChanged(1_000, new EntityId(640), 80, 2203572, BuffChangeKind.Applied, 1, 1, 5000,
            FirerId: new EntityId(0x0000_0002_0000_0280), SourceKind: 0, SourceId: 2327);
        var b = Assert.IsType<BuffEvent>(CombatLogEventConverter.Convert(ev));
        Assert.Equal("8589935232", b.Src);      // 0x0000_0002_0000_0280 as decimal
        Assert.Equal(0, b.SrcKind);
        Assert.Equal(2327, b.SrcId);
        Assert.Equal("applied", b.Kind);
    }

    [Fact]
    public void BuffChanged_without_firer_writes_zero()
    {
        var ev = new CombatEvent.BuffChanged(1_000, new EntityId(640), 80, 1, BuffChangeKind.Removed, 0, 1, 0);
        var b = Assert.IsType<BuffEvent>(CombatLogEventConverter.Convert(ev));
        Assert.Equal("0", b.Src);
    }

    // -------------------------------------------------------------------------
    // Field-by-field mapping pins, MOVED VERBATIM out of LogUploadTests when the ring buffer that owned
    // the conversion (before it was extracted here) was replaced by EventSpool, 2026-09-05. The
    // assertions are unchanged — only the call goes straight to the converter now.
    // -------------------------------------------------------------------------

    [Fact]
    public void Converts_damage_event()
    {
        var ev = new CombatEvent.DamageDealt(3000L, new EntityId(10), new EntityId(20), 7,
            1234, 1000, 50, true, false, false, false,
            DamageElement.Water, DamageSourceKind.Buff);

        var de = Assert.IsType<DamageEvent>(CombatLogEventConverter.Convert(ev));
        Assert.Equal(3000L, de.Ms);
        Assert.Equal("10", de.Src);
        Assert.Equal("20", de.Tgt);
        Assert.Equal(7, de.Skill);
        Assert.Equal(1234L, de.Amt);
        Assert.Equal(1000L, de.Act);
        Assert.Equal(50L, de.Shield);
        Assert.True(de.Crit);
        Assert.False(de.Lucky);
        Assert.False(de.Heal);
        Assert.False(de.Dead);
        Assert.Equal((int)DamageElement.Water, de.Elem);
        Assert.Equal((int)DamageSourceKind.Buff, de.Kind);
    }

    [Fact]
    public void Converts_skill_event()
    {
        var se = Assert.IsType<SkillEvent>(CombatLogEventConverter.Convert(
            new CombatEvent.SkillUsed(5000L, new EntityId(3), 88, SkillEventPhase.StageBegin)));
        Assert.Equal(5000L, se.Ms);
        Assert.Equal("3", se.Src);
        Assert.Equal(88, se.Skill);
        Assert.Equal((int)SkillEventPhase.StageBegin, se.Phase);
    }

    [Fact]
    public void Converts_buff_event()
    {
        var be = Assert.IsType<BuffEvent>(CombatLogEventConverter.Convert(
            new CombatEvent.BuffChanged(6000L, new EntityId(99), 12345, 500, BuffChangeKind.Applied, 2, 0, 30000)));
        Assert.Equal(6000L, be.Ms);
        Assert.Equal("99", be.Tgt);
        Assert.Equal(12345, be.Uuid);
        Assert.Equal(500, be.Base);
        Assert.Equal("applied", be.Kind);
        Assert.Equal(2, be.Stacks);
        Assert.Equal(0, be.Layer);
        Assert.Equal(30000, be.DurMs);
    }

    [Fact]
    public void Buff_removed_maps_kind_correctly()
    {
        var be = Assert.IsType<BuffEvent>(CombatLogEventConverter.Convert(
            new CombatEvent.BuffChanged(7000L, new EntityId(1), 1, 1, BuffChangeKind.Removed, 0, 0, 0)));
        Assert.Equal("removed", be.Kind);
    }

    [Fact]
    public void Buff_refreshed_maps_kind_correctly()
    {
        var be = Assert.IsType<BuffEvent>(CombatLogEventConverter.Convert(
            new CombatEvent.BuffChanged(8000L, new EntityId(2), 2, 2, BuffChangeKind.Refreshed, 1, 0, 10000)));
        Assert.Equal("refreshed", be.Kind);
    }
}
