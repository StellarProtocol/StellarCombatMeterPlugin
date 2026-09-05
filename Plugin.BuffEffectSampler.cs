using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    private readonly BuffEffectSampler _buffEffects = new();

    /// <summary>Called from OnCombatEvent for every BuffChanged (Plugin.Capture.cs). Self-targeted only; the sampler
    /// applies the external-firer rule itself. In-run only (mirrors TickAttrRangeSample's gate) — a town buff is
    /// never sampled. Sheet read is lazy: only a genuinely admitted, quiet candidate reads it (BuffEffectSampler.OnSelfBuff).
    /// NOTE (clock skew): the feed stamps <c>b.TimestampMs</c> (client wall-clock at wire receive) while
    /// <see cref="TickBuffEffectSampler"/> ticks on <c>ServerNowMs</c> (server-anchored) — measured ~135ms apart
    /// against the 600ms window. If a P0 probe ever shows windows never closing (a `[BuffEffect]`-style yield ≈ 0),
    /// switch this feed's <c>nowMs</c> to <c>_services.CombatSnapshot.ServerNowMs</c> (one line).</summary>
    private void FeedBuffEffectSampler(CombatEvent evt)
    {
        if (evt is not CombatEvent.BuffChanged b) return;
        if (_services.Dungeon.CurrentRunId == 0) return;   // in-run only (spec perf constraint, same as attr-range)
        var self = _services.CombatSnapshot.LocalEntityId;
        if (b.TargetId != self || !self.IsPlayer) return;
        _buffEffects.OnSelfBuff(b, () => _services.EntityDetail.GetAttributes(self), b.TimestampMs);
    }

    /// <summary>10 Hz tick beside TickAttrRangeSample. Resolves pending windows; sheet read is lazy (only when a window closed).
    /// Skips building the sheet-read lambda entirely when nothing is pending (no per-tick closure while idle).</summary>
    private void TickBuffEffectSampler()
    {
        if (!_buffEffects.HasPending) return;
        var self = _services.CombatSnapshot.LocalEntityId;
        if (!self.IsPlayer) return;
        _buffEffects.Tick(() => _services.EntityDetail.GetAttributes(self), _services.CombatSnapshot.ServerNowMs);
    }
}
