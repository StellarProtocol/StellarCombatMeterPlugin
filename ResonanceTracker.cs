using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

// Inferred Battle-Imagine cooldown/charge cache for OTHER players. Self uses authoritative
// LocalCooldowns; foreign players have no wire cooldown, so we infer from observed casts (the
// same DamageDealt stream that drives spec inference): each cast consumes a charge and starts a
// per-charge recharge timer. State() advances the available-charge count to the query time.
// Pure logic, no Unity. Server-epoch ms throughout. Allocation-light on reads (no LINQ, struct
// state mutated in place). Keyed by the cast skill id (a Battle-Imagine skill).
internal sealed class ResonanceTracker
{
    private struct Track
    {
        public int ChargesAvailable;   // charges in hand at LastSeenMs
        public long LastSeenMs;        // when ChargesAvailable was last accurate
    }

    // entity -> (skillId -> track).
    private readonly Dictionary<EntityId, Dictionary<int, Track>> _tracks = new();

    // Records an observed cast: consume a charge, advancing to nowMs first so we don't over-consume.
    public void OnCast(EntityId src, int skillId, int chargeCount, int rechargeMs, long nowMs)
    {
        if (skillId == 0) return;
        int max = chargeCount < 1 ? 1 : chargeCount;
        var bySkill = Tracks(src);
        int avail = bySkill.TryGetValue(skillId, out var t)
            ? Advance(t, max, rechargeMs, nowMs)
            : max;   // first sighting — assume it was full before this cast
        int after = avail > 0 ? avail - 1 : 0;
        bySkill[skillId] = new Track { ChargesAvailable = after, LastSeenMs = nowMs };
    }

    // Current charges + ms-until-next-charge for (src, skillId), advanced to nowMs.
    public (int chargesAvail, int remainingMs) State(EntityId src, int skillId, int chargeCount, int rechargeMs, long nowMs)
    {
        int max = chargeCount < 1 ? 1 : chargeCount;
        if (!_tracks.TryGetValue(src, out var bySkill) || !bySkill.TryGetValue(skillId, out var t))
            return (max, 0);
        int avail = Advance(t, max, rechargeMs, nowMs);
        if (avail >= max) return (max, 0);
        int remaining = rechargeMs > 0 ? rechargeMs - (int)((nowMs - t.LastSeenMs) % rechargeMs) : 0;
        return (avail, remaining < 0 ? 0 : remaining);
    }

    public void Clear() => _tracks.Clear();

    public void Forget(EntityId src) => _tracks.Remove(src);

    // Advance a track to nowMs: one charge returns per rechargeMs elapsed since LastSeenMs.
    private static int Advance(in Track t, int max, int rechargeMs, long nowMs)
    {
        if (t.ChargesAvailable >= max) return max;
        if (rechargeMs <= 0) return t.ChargesAvailable;
        long elapsed = nowMs - t.LastSeenMs;
        if (elapsed <= 0) return t.ChargesAvailable;
        int gained = (int)(elapsed / rechargeMs);
        int avail = t.ChargesAvailable + gained;
        return avail > max ? max : avail;
    }

    private Dictionary<int, Track> Tracks(EntityId src)
    {
        if (!_tracks.TryGetValue(src, out var bySkill)) { bySkill = new Dictionary<int, Track>(); _tracks[src] = bySkill; }
        return bySkill;
    }
}
