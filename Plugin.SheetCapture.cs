using System;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    /// <summary>Once per SEGMENT (not a poll): if the current spool segment has no sheet keyframe yet, write one
    /// from the live self sheet. Rides the existing 10 Hz TickLoadoutCapture cadence in place of the retired
    /// sampler tick; after the first call per segment it is a single bool read. Skipped while the local entity
    /// is unknown (loading). Delta rows are event-driven (EventSpool.Add on EntityAttributesChanged) — this only
    /// guarantees a keyframe for a segment in which no tracked attr ever changed (spec § 6.1 / decision 3).
    /// Stamp = wire-receive clock domain (the same DateTimeOffset.UtcNow the probe stamps packets with).</summary>
    private void TickSheetKeyframe()
    {
        if (!Spool.NeedsSheetKeyframe) return;
        if (!_services.CombatSnapshot.LocalEntityId.IsPlayer) return;
        Spool.AddSheetKeyframe(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
