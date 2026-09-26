using System;
using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Spec upload design 2026-09-26 item 1: each player actor carries "specSpans": [[specId, startMs, endMs], …] (the
// classSpans time base), written only when talent-derived spans exist and OMITTED otherwise so an actor without a
// probed spec uploads byte-identically to 2.14.2.
public class SpecSpansUploadTests
{
    static Actor Player(IReadOnlyList<long[]>? specSpans) => new(
        Name: "Miyuki", Kind: "player", TeamId: 1, IsLocal: false, Uid: 999,
        ProfessionId: 5, Level: 60, AbilityScore: 0, MaxHp: 1,
        Attributes: new List<long[]>(), Gear: new List<int[]>(), Skills: new List<int[]>(),
        Fashion: new List<Fashion>(),
        ClassSpans: new List<long[]> { new long[] { 13, 0, 5000 }, new long[] { 5, 5000, 12_000 } },
        SpecSpans: specSpans);

    static CombatLog Log(Actor actor) =>
        new(1,
            new LogHeader("cm-spec-test", 0, "2.15", "sea", null, null, "unlisted",
                new Encounter("dungeon", 1, null, 1, 0, null, 0, null, null, 0, "kill", 0, 0, 0, 0),
                new Uploader(1248014, "sig", "nonce")),
            new Dictionary<string, Actor> { ["999"] = actor },
            Array.Empty<CombatLogEvent>());

    [Fact]
    public void WriteActor_emits_specSpans_after_classSpans_when_present()
    {
        var json = CombatLogWriter.Write(Log(Player(new List<long[]>
            { new long[] { 130002, 0, 5000 }, new long[] { 50001, 5000, 12_000 } })));
        Assert.Contains("\"classSpans\":[[13,0,5000],[5,5000,12000]],\"specSpans\":[[130002,0,5000],[50001,5000,12000]]", json);
    }

    [Fact]
    public void WriteActor_omits_specSpans_when_null_or_empty_and_is_otherwise_unchanged()
    {
        var none = CombatLogWriter.Write(Log(Player(null)));
        var empty = CombatLogWriter.Write(Log(Player(new List<long[]>())));
        Assert.DoesNotContain("specSpans", none);
        Assert.Equal(none, empty);
    }

    [Fact]
    public void BuildActorSpecSpans_maps_the_frozen_arrays_and_is_null_when_empty()
    {
        var snap = new EntitySnapshot
        {
            SpecSpanId = new long[] { 50001, 50002 }, SpecSpanStart = new long[] { 1, 2 }, SpecSpanEnd = new long[] { 2, 3 },
        };
        var mapped = CombatLogAssembler.BuildActorSpecSpans(snap)!;
        Assert.Equal(new long[] { 50001, 1, 2 }, mapped[0]);
        Assert.Equal(new long[] { 50002, 2, 3 }, mapped[1]);
        Assert.Null(CombatLogAssembler.BuildActorSpecSpans(new EntitySnapshot()));
    }
}
