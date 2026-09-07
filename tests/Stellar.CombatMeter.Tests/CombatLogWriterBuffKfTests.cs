using System.Collections.Generic;
using System.Text.RegularExpressions;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// rDPS phase 2 (spec § 6.1.3): CombatLogWriter's OWN `events` array writer (LogUpload/CombatLogWriter.cs, the
// BuffEvent case) is a SEPARATE writer from EventsJsonWriter's chunk-stream writer (already pinned by
// EventsJsonWriterBuffTests) and had no coverage at all for its `kf` field until this test. Reuses the same
// full-CombatLog fixture shape EliteUploadTests already builds — no new production test seam needed, since
// CombatLogWriter.Write(CombatLog) is already public/internal and test-reachable.
public sealed class CombatLogWriterBuffKfTests
{
    [Fact]
    public void Writer_emits_kf_as_the_last_field_only_for_the_keyframe_row()
    {
        var enc = new Encounter("dungeon", 77L, null, 100, 0, null, 0, null, null, 0,
            "kill", 1000L, 2000L, 1000L, 0);
        var hdr = new LogHeader("cm-buff-kf", 2000L, "2.11", "SEA", null, null, "unlisted",
            enc, new Uploader(55L, "sig", "nonce"));
        var events = new List<CombatLogEvent>
        {
            new BuffEvent(1_000, "1", 80, 2110034, "applied", 1, 1, 15000, "1366688384", 0, 2900340, null, true),
            new BuffEvent(2_000, "1", 81, 2110034, "applied", 1, 1, 15000, "1366688384", 0, 2900341, null, false),
        };
        var log = new CombatLog(1, hdr, new Dictionary<string, Actor>(), events);

        var json = CombatLogWriter.Write(log);

        Assert.Single(Regex.Matches(json, "\"kf\":1"));                 // exactly one kf row in the whole array
        Assert.Contains("\"srcId\":2900340,\"kf\":1}", json);           // kf row: kf is the LAST field before the close
        Assert.Contains("\"srcId\":2900341}", json);                    // non-kf row: object closes right after srcId
    }
}
