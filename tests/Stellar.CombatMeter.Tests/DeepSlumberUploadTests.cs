using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>Pins the Deep-Slumber upload block (Phase 3, owner 2026-08-19): self-only, live
/// archive-time snapshot, additive JSON `deepSlumber` on the self actor. Do not weaken.</summary>
public class DeepSlumberUploadTests
{
    private static DeepSlumberState State() => new(
        SeasonLevels: new[] { new[] { 93, 65 } },
        Lines: new[]
        {
            new DeepSlumberLine(93, 3, new[]
            {
                new DeepSlumberArea(1, true, 120,
                    BigNodes:    new[] { new[] { 11, 5110001 } },
                    MiddleNodes: Array.Empty<int[]>(),
                    NormalNodes: new[] { new[] { 21, 4 } }),
            }),
        });

    [Fact]
    public void NonLocalActor_NeverCarriesDeepSlumber()
        => Assert.Null(CombatLogAssembler.BuildDeepSlumber(isLocal: false, State()));

    [Fact]
    public void NullSnapshot_IsOmitted_NotSentEmpty()
        => Assert.Null(CombatLogAssembler.BuildDeepSlumber(isLocal: true, null));

    [Fact]
    public void LocalActor_MapsTheSnapshotOneToOne()
    {
        var e = CombatLogAssembler.BuildDeepSlumber(isLocal: true, State())!;
        Assert.Equal(new[] { 93, 65 }, e.SeasonLevels[0]);
        var line = Assert.Single(e.Lines);
        Assert.Equal(93, line.LineId);
        Assert.Equal(3, line.SubType);
        var area = Assert.Single(line.Areas);
        Assert.True(area.Active);
        Assert.Equal(120, area.Score);
        Assert.Equal(new[] { 11, 5110001 }, area.Big[0]);
        Assert.Equal(new[] { 21, 4 }, area.Nodes[0]);
    }
}
