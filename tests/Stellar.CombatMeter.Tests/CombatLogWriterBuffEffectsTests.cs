// Writer coverage for Task 9's derived.buffEffects + truncatedBuffEvents (spec § 4.2 / § 6.1).

using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class CombatLogWriterBuffEffectsTests
{
    [Fact]
    public void Writer_emits_truncatedBuffEvents_and_buffEffects()
    {
        var entry = new Plugin.EncounterHistoryEntry { LevelUuid = 1 };
        var derived = DerivedBuilder.Build(entry, truncatedEvents: false) with
        {
            TruncatedBuffEvents = true,
            BuffEffects = new[] { new BuffEffectAgg(55333, 1, 0, 2327, 3, new[] { (11710, 1000L) }) },
        };

        var header = new LogHeader("cm-buffeffects", 11_000L, "2.11", "SEA", "1.9.0", "1.1.0", "unlisted",
            CombatLogAssembler.BuildEncounter(entry), new Uploader(1L, "sig", "nonce"));
        var log = new CombatLog(1, header, new System.Collections.Generic.Dictionary<string, Actor>(),
            System.Array.Empty<CombatLogEvent>(), derived);
        var json = CombatLogWriter.Write(log);

        Assert.Contains("\"truncatedBuffEvents\":true", json);
        Assert.Contains("\"buffEffects\":[{\"base\":55333,\"stacks\":1,\"srcKind\":0,\"srcId\":2327,\"n\":3,\"deltas\":[[11710,1000]]}]", json);
    }
}
