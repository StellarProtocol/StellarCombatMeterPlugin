using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    // D10 (xDPS spec § 6.5): the local player's cooldown ratio, read off the SAME LocalCooldowns snapshot
    // DetectSelfImagineCasts already polls (~10 Hz) — no new poll. Capture doctrine: never gated by pause or a toggle.
    private readonly CdRatioTracker _cdRatio = new();
    private readonly List<SkillCooldown> _cdRatioFresh = new(8);

    private void TickCdRatio()
    {
        if (!_services.CombatSnapshot.LocalEntityId.IsPlayer) return;
        var changed = _cdRatio.Observe(_services.CombatSnapshot.LocalCooldowns, _cdRatioFresh);
        if (changed is null) return;
        Spool.AddSheetRow(SheetRowBuilder.Synthetic(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), CdRatioTracker.AttrId, changed.Value));
        LogCdRatio(changed.Value, _cdRatioFresh);
    }

    /// <summary>The spool's keyframe reader: the live attribute sheet plus the last known cooldown ratio under the
    /// synthetic id, so every segment keyframe restates it (the worker's step function needs a value at segment start).</summary>
    private IReadOnlyDictionary<int, long> ReadSelfSheetWithCdRatio()
    {
        var attrs = _services.EntityDetail.GetAttributes(_services.CombatSnapshot.LocalEntityId);
        if (_cdRatio.Last is not { } ratio) return attrs;
        var copy = new Dictionary<int, long>(attrs.Count + 1);
        foreach (var (id, v) in attrs) copy[id] = v;
        copy[CdRatioTracker.AttrId] = ratio;
        return copy;
    }
}
