// Task 13: the pure retry-decision logic for a 409 ("server already has this run") response.
// The server dedupes by run identity, so a 409 means the summary is already up. We only bother
// sending the tiny own-detail supplement when we actually hold local actor detail to contribute
// (the multi-uploader courtesy path); a deferred/reloaded manual retry carries no local actor in
// its snapshot, so there is nothing to send and the run is already fully covered — never a failure.

using System;
using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class SupplementPolicyTests
{
    private static Actor A(bool isLocal) => new(
        Name: "x", Kind: "player", TeamId: 1, IsLocal: isLocal, Uid: 1,
        ProfessionId: 1, Level: 60, AbilityScore: 0, MaxHp: 1,
        Attributes: new List<long[]>(), Gear: new List<int[]>(), Skills: new List<int[]>(),
        Fashion: new List<Fashion>());

    private static CombatLog Log(params Actor[] actors)
    {
        var dict = new Dictionary<string, Actor>();
        for (var i = 0; i < actors.Length; i++) dict[i.ToString()] = actors[i];
        var enc = new Encounter("dungeon", 1, null, 0, 0, null, 0, null, null, 0, "kill", 0, 0, 0, 0);
        var hdr = new LogHeader("cm-x", 0, "1", "SEA", null, null, "unlisted", enc, new Uploader(1, "", "n"));
        return new CombatLog(1, hdr, dict, Array.Empty<CombatLogEvent>());
    }

    [Fact]
    public void Sends_supplement_only_when_a_local_actor_is_present()
    {
        Assert.True(SupplementPolicy.ShouldSendSupplement(Log(A(isLocal: true), A(isLocal: false))));
        // Reloaded/deferred entry: N actors survive in the snapshot but NONE is flagged local
        // (the local entity-id differs across sessions) → nothing worth supplementing.
        Assert.False(SupplementPolicy.ShouldSendSupplement(Log(A(isLocal: false), A(isLocal: false))));
        Assert.False(SupplementPolicy.ShouldSendSupplement(Log()));   // no actors at all
    }

    [Theory]
    [InlineData(0, true)]      // transport failure (no HTTP response) — retryable
    [InlineData(500, true)]    // server error — retryable
    [InlineData(503, true)]
    [InlineData(200, false)]   // success — not a failure
    [InlineData(204, false)]
    [InlineData(400, false)]   // definitive client error — a retry can't fix it
    [InlineData(404, false)]   // window reshaped/gone — documented non-retryable
    public void Classifies_only_transport_and_5xx_as_retryable(int status, bool retryable)
        => Assert.Equal(retryable, SupplementPolicy.IsRetryable(status));
}
