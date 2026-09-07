using System;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    /// <summary>Once per SEGMENT (not a poll): if the current spool segment has no buff keyframe yet, write one from the
    /// live buff set (rDPS phase 2, spec § 6.1.3). Rides the 10 Hz TickLoadoutCapture cadence beside TickSheetKeyframe;
    /// after the first call per segment it is a single bool read. Skipped while the local entity is unknown.</summary>
    private void TickBuffKeyframe()
    {
        if (!Spool.NeedsBuffKeyframe) return;
        var self = _services.CombatSnapshot.LocalEntityId;
        if (!self.IsPlayer) return;
        Spool.AddBuffKeyframe(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), self);
    }

    /// <summary>Buffs are scene-scoped and the framework drops its own buff cache on EnterScene WITHOUT per-buff Removed
    /// events, so the live set must follow — else a permanent buff from the last scene would keyframe into the next
    /// dungeon. Null-conditional: teardown must not build a spool just to clear it. Deliberately NOT inside the archive
    /// flow's OnSceneChanged (docs/recon/combatmeter-archive-flow.md — protected).</summary>
    private void OnSceneChangedClearLiveBuffs(string? newScene) => _spool?.ClearLiveBuffs();
}
