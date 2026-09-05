using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    private readonly BuffEffectSampler _buffEffects = new();

    /// <summary>Called from OnCombatEvent for every BuffChanged (Plugin.Capture.cs). Self-targeted only; the sampler
    /// applies the external-firer rule itself. In-run only (mirrors TickAttrRangeSample's gate) — a town buff is
    /// never sampled. Sheet read is lazy: only a genuinely admitted, quiet candidate reads it (BuffEffectSampler.OnSelfBuff).
    /// NOTE (one clock): this feed and <see cref="TickBuffEffectSampler"/> both stamp <c>nowMs</c> from
    /// <c>_services.CombatSnapshot.ServerNowMs</c> (server-anchored) — a single clock domain for the whole
    /// window. This feed used to stamp <c>b.TimestampMs</c> (client wall-clock at wire receive) instead,
    /// which measured ~135ms apart from <c>ServerNowMs</c> against the 600ms window: the window's deadline
    /// was SET against one clock and TESTED against another, so the two could disagree about how much of the
    /// window had actually elapsed. Feeding <c>ServerNowMs</c> here makes <see cref="BuffEffectSampler.WindowMs"/>
    /// mean the same 600ms on both the arm side and the close side.</summary>
    private void FeedBuffEffectSampler(CombatEvent evt)
    {
        if (evt is not CombatEvent.BuffChanged b) return;
        if (_services.Dungeon.CurrentRunId == 0) return;   // in-run only (spec perf constraint, same as attr-range)
        var self = _services.CombatSnapshot.LocalEntityId;
        if (b.TargetId != self || !self.IsPlayer) return;
        _buffEffects.OnSelfBuff(b, () => _services.EntityDetail.GetAttributes(self), _services.CombatSnapshot.ServerNowMs);
    }

    /// <summary>10 Hz tick beside TickAttrRangeSample. Resolves pending windows; sheet read is lazy (only when a window closed).
    /// Skips building the sheet-read lambda entirely when nothing is pending (no per-tick closure while idle).</summary>
    private void TickBuffEffectSampler()
    {
        if (!_buffEffects.HasPending) return;
        var self = _services.CombatSnapshot.LocalEntityId;
        // A stale pending must never resolve against a much-later sheet read for a DIFFERENT (or no-longer
        // player) entity — drop it rather than let it silently mis-attribute a reading.
        if (!self.IsPlayer) { _buffEffects.Reset(); return; }
        _buffEffects.Tick(() => _services.EntityDetail.GetAttributes(self), _services.CombatSnapshot.ServerNowMs);
    }
}
