using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class SourceNameResolverTests
{
    // Mirrors the framework's IGameText contract over a few rows (the real table ships in Stellar.Application).
    private sealed class FakeGameText : IGameText
    {
        public bool Ready = true;
        public readonly Dictionary<(GameTextKind, int), (string? Name, string? Design)> Rows = new();
        public bool IsReady => Ready;
        event Action IGameText.Ready { add { } remove { } }
        public string? GetName(GameTextKind k, int id) => Ready && Rows.TryGetValue((k, id), out var r) ? r.Name : null;
        public string? GetDesignName(GameTextKind k, int id) => Ready && Rows.TryGetValue((k, id), out var r) ? r.Design : null;
        public string? GetDescription(GameTextKind k, int id) => null;
        public bool IsPlaceholderName(string? n) => string.IsNullOrWhiteSpace(n) || n == "气刃突刺计数" || n == "场地标记01"
            || n.StartsWith("Buff ", StringComparison.Ordinal) || n.StartsWith("Skill ", StringComparison.Ordinal);
        public string ResolveDisplayName(GameTextKind k, int id, string? game, out bool design)
        {
            design = false;
            bool known = Ready && Rows.TryGetValue((k, id), out _);
            var row = known ? Rows[(k, id)] : default;
            if (!IsPlaceholderName(game) && (!known || row.Name != null)) return game!;
            if (row.Name != null) return row.Name;
            if (row.Design != null) { design = true; return row.Design; }
            return string.IsNullOrWhiteSpace(game) ? $"{k} {id}" : game!;
        }
    }

    private static readonly Dictionary<int, string> Overrides = new() { [2031104] = "Lucky Strike (Lance)" };

    private static FakeGameText Text()
    {
        var t = new FakeGameText();
        t.Rows[(GameTextKind.Buff, 2203291)] = (null, "Firmament Fall-child buff");
        t.Rows[(GameTextKind.Buff, 2203521)] = (null, "Steel Beak - child buff, stack");
        t.Rows[(GameTextKind.Buff, 55240)] = (null, "Light Energy Bombardment damage zone");
        t.Rows[(GameTextKind.Buff, 4501)] = ("Burn", "Ignite effect Buff");
        t.Rows[(GameTextKind.Buff, 2031104)] = (null, "Lance - Luck");
        t.Rows[(GameTextKind.Skill, 2234)] = ("Radiance Barrage", null);
        return t;
    }

    [Theory]
    [InlineData(2203291, "Firmament Fall-child buff (2203291)")]
    [InlineData(2203521, "Steel Beak - child buff, stack (2203521)")]
    [InlineData(55240, "Light Energy Bombardment damage zone (55240)")]
    public void UnnamedBuff_ShowsDesignNameWithId(int id, string expected)
        => Assert.Equal(expected, SourceNameResolver.Resolve(id, null, $"Buff {id}", Overrides, Text()));

    [Fact]
    public void RealNames_NeverGetTheIdSuffix()
    {
        Assert.Equal("Burn", SourceNameResolver.Resolve(4501, null, "Burn", Overrides, Text()));
        Assert.Equal("Radiance Barrage", SourceNameResolver.Resolve(2234, "Radiance Barrage", null, Overrides, Text()));
    }

    [Fact]
    public void Override_WinsOverBuffDesignName()
        => Assert.Equal("Lucky Strike (Lance)", SourceNameResolver.Resolve(2031104, null, "Lance - Luck", Overrides, Text()));

    [Fact]
    public void FrameworkCuratedStandIn_ForAnUnnamedBuff_IsReplacedByDesignName()
        => Assert.Equal("Firmament Fall-child buff (2203291)",
            SourceNameResolver.Resolve(2203291, null, "Feather Burst (AoE sub-buff)", Overrides, Text()));

    [Fact]
    public void FillerOrMissing_ReturnsNull_ForRawFallback()
    {
        Assert.Null(SourceNameResolver.Resolve(999, "场地标记01", null, Overrides, Text()));
        Assert.Null(SourceNameResolver.Resolve(998, null, null, Overrides, Text()));
    }

    [Fact]
    public void NotReady_UnnamedBuffFallsThrough_RealNameStillWins()
    {
        var t = Text(); t.Ready = false;
        Assert.Null(SourceNameResolver.Resolve(2203291, null, "Buff 2203291", Overrides, t));
        Assert.Equal("Burn", SourceNameResolver.Resolve(4501, null, "Burn", Overrides, t));
    }
}
