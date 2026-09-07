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
        if (changed is { } ratio)
            Spool.AddSheetRow(SheetRowBuilder.Synthetic(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), CdRatioTracker.AttrId, ratio));
        // Every tick that SAW a fresh cooldown row is offered to diagnostics, changed or not — the partial decides
        // what it prints (process rules § 15: a run whose rows never move the ratio must not log silence).
        if (_cdRatioFresh.Count > 0) LogCdRatio(changed, _cdRatioFresh);
    }

    /// <summary>The spool's keyframe reader: the live attribute sheet composed with the last known cooldown ratio.
    /// The composition rule (and why it must NOT overlay onto a sheet with no tracked game attr — the keyframe
    /// deferral) lives in <see cref="SheetRowBuilder.ComposeSelfSheet"/>.</summary>
    private IReadOnlyDictionary<int, long> ReadSelfSheetWithCdRatio() =>
        SheetRowBuilder.ComposeSelfSheet(
            _services.EntityDetail.GetAttributes(_services.CombatSnapshot.LocalEntityId), _cdRatio.Last);
}
