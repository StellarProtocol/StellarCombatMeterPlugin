using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>The per-track truncation flags on `derived` (spec § 4.2). `truncatedBuffEvents` is always written
/// (schema: boolean); the retired sampler's `buffEffects` array is NEVER written any more (the schema keeps
/// accepting it from 2.6.0+fa95dd6 clients; the table ignores it).</summary>
public sealed class CombatLogWriterTrackFlagsTests
{
    static Derived Bare(bool buff) => new(
        CombatDurationMs: 1000, TruncatedEvents: false,
        PerActor: new Dictionary<string, ActorAgg>(), PerActorSkills: new Dictionary<string, IReadOnlyList<SkillAgg>>(),
        PerActorHealSkills: new Dictionary<string, IReadOnlyList<SkillAgg>>(), PerActorTakenSkills: new Dictionary<string, IReadOnlyList<TakenAgg>>(),
        Deaths: new List<DeathRec>(), Series: new SeriesBlock(1000, new Dictionary<string, ActorSeries>()),
        TruncatedBuffEvents: buff);

    [Fact]
    public void Writes_truncatedBuffEvents_both_ways_and_never_buffEffects()
    {
        var on  = CombatLogWriter.WriteDerivedForTest(Bare(true));
        var off = CombatLogWriter.WriteDerivedForTest(Bare(false));
        Assert.Contains("\"truncatedBuffEvents\":true", on);
        Assert.Contains("\"truncatedBuffEvents\":false", off);
        Assert.DoesNotContain("buffEffects", on);
    }
}
