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
    /// flow's OnSceneChanged (docs/recon/combatmeter-archive-flow.md — protected).
    /// <para>ACCEPTED WINDOW: the framework clears its OWN buff cache on the WIRE EnterScene (WorldNtf method 3 —
    /// <c>PandaCombatStubProbe</c>'s <c>_sink.ClearAllBuffs()</c>), while THIS handler runs off <c>ClientState.SceneChanged</c>,
    /// which rides the GAME's own <c>Panda.Core.Game.OnEnterScene</c> lifecycle hook and fires a moment LATER. A buff
    /// applied in that gap reaches <see cref="LogUpload.LiveBuffSet"/> and is then cleared here before it is ever
    /// keyframed, so it is invisible to the worker until its own next applied/refreshed/removed row arrives — no
    /// capture is lost (the live row itself is always written to the spool) and the set self-heals on that row.
    /// This window is an accepted risk, not a defect to fix.</para></summary>
    private void OnSceneChangedClearLiveBuffs(string? newScene) => _spool?.ClearLiveBuffs();
}
