using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    private readonly BuffEffectSampler _buffEffects = new();

    /// <summary>Called from OnCombatEvent for every BuffChanged (Plugin.Capture.cs). Self-targeted only; the sampler
    /// applies the external-firer rule itself. Reads the live self sheet once per candidate (buff-change rate, not per frame).</summary>
    private void FeedBuffEffectSampler(CombatEvent evt)
    {
        if (evt is not CombatEvent.BuffChanged b) return;
        var self = _services.CombatSnapshot.LocalEntityId;
        if (b.TargetId != self || !self.IsPlayer) return;
        _buffEffects.OnSelfBuff(b, _services.EntityDetail.GetAttributes(self), b.TimestampMs);
    }

    /// <summary>10 Hz tick beside TickAttrRangeSample. Resolves pending windows; sheet read is lazy (only when a window closed).</summary>
    private void TickBuffEffectSampler()
    {
        var self = _services.CombatSnapshot.LocalEntityId;
        if (!self.IsPlayer) return;
        _buffEffects.Tick(() => _services.EntityDetail.GetAttributes(self), _services.CombatSnapshot.ServerNowMs);
    }
}
