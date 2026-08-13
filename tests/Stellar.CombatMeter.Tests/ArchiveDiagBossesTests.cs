using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Pins the [CombatMeter][archive] banked line's entry-list bosses formatter
/// (Plugin.FormatStageBosses static overload — owner pre-production checklist 2026-08-13, item 3).
/// Regression: the banked line used to format the LIVE _stageBosses set, which BossStatus's
/// DrainIfAllGone (deferred boss-kill archive) or ResetRunScopedTrackers (scene archive) had
/// already emptied — so the line printed bosses=[] while the entry it banked carried the real
/// members. The banked path now formats entry.StageBosses (the sticky-latch snapshot); these
/// tests pin that a latched non-empty list NEVER formats as "[]", whatever the live state.
/// </summary>
public class ArchiveDiagBossesTests
{
    private static EntityId E(long v) => new(v);

    private static readonly IReadOnlyDictionary<EntityId, float> NoHp =
        new Dictionary<EntityId, float>();

    [Fact]
    public void Latched_entry_bosses_format_even_after_live_state_drained()
    {
        // The regression shape: hp map already pruned (drain/reset ran), entry still carries the
        // stage's bosses — the line must list them (killed:True is the meaningful field), not "[]".
        var members = new (EntityId Id, int ConfigId, bool Killed)[]
        {
            (E(1), 102800, true),
            (E(2), 102801, true),
        };
        Assert.Equal("[102800:True:-1,102801:True:-1]", Plugin.FormatStageBosses(members, NoHp));
    }

    [Fact]
    public void Empty_entry_list_formats_as_empty_brackets()
    {
        Assert.Equal("[]", Plugin.FormatStageBosses(
            Array.Empty<(EntityId Id, int ConfigId, bool Killed)>(), NoHp));
    }

    [Fact]
    public void Members_still_in_hp_map_carry_their_last_fraction()
    {
        var members = new (EntityId Id, int ConfigId, bool Killed)[]
        {
            (E(1), 102800, false),
            (E(2), 102801, true),
        };
        var hp = new Dictionary<EntityId, float> { [E(1)] = 0.4567f, [E(2)] = 0.01f };
        // 0.### formatting, per-member lookup, missing members would read -1 (previous test).
        Assert.Equal("[102800:False:0.457,102801:True:0.01]", Plugin.FormatStageBosses(members, hp));
    }
}
