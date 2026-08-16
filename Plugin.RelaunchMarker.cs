using System.Text;
using Stellar.CombatMeter.AutoArchive;

namespace Stellar.CombatMeter;

// Mid-dungeon-relaunch recovery wiring (docs/recon/run-identity-relaunch-split.md § "Concrete
// owner-approved design", 2026-08-16). PROTECTED run-identity path — read
// docs/recon/combatmeter-archive-flow.md before touching. The pure decisions + serialization live in
// AutoArchive/RelaunchMarker.cs (unit-tested); this partial is the stateful glue over _services.Data:
//
//   • PERSIST  a {levelUuid, partyId, dungeonStartMs, lastAliveMs} marker the tick the run's dungeon-start
//              latches (PersistActiveRunMarker, from EnsureCombatStarted), then heartbeat lastAliveMs
//              (~30 s) so freshness measures the relaunch GAP, not the run's length.
//   • RESTORE  the original dungeonStartMs at the first combat start after a relaunch (ResolveRelaunchStartMs,
//              from EnsureCombatStarted, BEFORE LatchRunStartMs) — same instance AND party AND fresh only.
//   • CLEAR    at a normal run boundary (BankRunBoundary), AND — belt for the crash/timeout/kick residual —
//              once settled out of the marked run (PollStaleMarkerClear, grace-debounced).
//
// Why the marker only lives for dungeons: _lastRunStartMs stays 0 in open-world (no dungeon timer), so
// every persist path is guarded on it — an open-world run never writes a marker.
public sealed partial class Plugin
{
    // One file in the plugin's data dir. Deleted on a normal run boundary; survives only a crash/relaunch.
    private const string MarkerFileName = "active-run.marker";

    // Heartbeat cadence: refresh lastAliveMs at most this often (server ms). Rare disk writes.
    private const long RelaunchHeartbeatMs = 30_000;

    // Grace before a settled out-of-marked-run observation clears the marker (server ms). Conservative on
    // purpose: a premature clear would break the primary restore; a too-long grace only slightly widens the
    // (already narrow) kicked-then-reenter-same-instance residual. The good-case "in World but CurrentRunId
    // not populated yet" transient is sub-second, so this cleanly separates it from a real town.
    private const long StaleMarkerGraceMs = 8_000;

    // The persisted marker, read once at construction (null on a clean session — a normal leave deleted it;
    // non-null only after a crash/relaunch). All decisions read this in-memory copy; disk is touched only on
    // load / heartbeat write / clear, never per-tick.
    private ActiveRunMarker? _activeRunMarker;
    private long _lastMarkerWriteMs;        // server ms of the last marker write (heartbeat throttle)
    private long _staleMarkerFirstOutMs;    // server ms we first observed settled-out-of-run (0 = not observing)
    // The marker's party, captured when a restore fires — a LAST-RESORT fallback for the archive/marker party
    // when a reconnect's game party (GrpcTeam) hasn't re-synced (both latch and live read 0). Reset at
    // BankRunBoundary alongside _lastRunStartMs. See RelaunchMarker.ResolvePartyId (run sea/UD87unsYz2).
    private long _relaunchPartyFallback;

    // Read the persisted marker once at startup. A corrupt/foreign file is dropped so it can never re-arm
    // recovery with garbage.
    private void LoadActiveRunMarker()
    {
        var bytes = _services.Data.Read(MarkerFileName);
        if (bytes is not null && bytes.Length > 0)
        {
            if (RelaunchMarker.TryDeserialize(Encoding.UTF8.GetString(bytes), out var m))
                _activeRunMarker = m;
            else
                _services.Data.Delete(MarkerFileName);   // corrupt/foreign → drop so it can't re-arm recovery
        }
        // Log AFTER the read regardless — a clean session (no file) must still surface "no marker on disk",
        // which the earlier early-return made unreachable (2026-08-16 diagnostics fix).
        LogRelaunchMarkerLoaded(_activeRunMarker);
    }

    // Restore decision for EnsureCombatStarted (called BEFORE LatchRunStartMs, only while _lastRunStartMs==0
    // — the first combat start of the run this session). Returns the dungeonStartMs to restore, or 0 for
    // "use the live start". <paramref name="nowMs"/> is the combat event's own server-epoch timestamp
    // (guaranteed valid at the first hit post-relaunch, unlike ServerNowMs which may still be 0) — same
    // clock domain as the marker's lastAliveMs, so the freshness delta is sound. Reads the LIVE party id
    // (marker must match the party we're now in).
    private long ResolveRelaunchStartMs(long nowMs)
    {
        long livePartyId = _services.PartySnapshot.PartyId;
        long start = RelaunchMarker.ResolveRelaunchStart(
            _activeRunMarker, _lastRunId, livePartyId, nowMs, RelaunchMarker.MaxRelaunchGapMs);
        if (start != 0)
        {
            // Restore the run's PARTY too: a reconnect's game party often reads 0 for the whole short run,
            // so without this the archive uploads partyId=0 and splits from its party (run sea/UD87unsYz2).
            // Last-resort only — ResolvePartyId still prefers any known live party over this.
            _relaunchPartyFallback = _activeRunMarker!.Value.PartyId;
            LogRelaunchRestore(_lastRunId, start);
            return start;
        }
        // Marker present but declined — trace WHY (diagnostics) so a re-test is conclusive.
        if (_activeRunMarker is { } m) LogRelaunchDeclined(m, _lastRunId, livePartyId, nowMs);
        return start;
    }

    // Persist / refresh the marker. Guarded to dungeon runs with a valid clock (the freshness anchor).
    // <paramref name="nowMs"/> is the caller's server-epoch clock: the combat event timestamp at latch time
    // (always valid — so a crash seconds into a fresh run still left a marker), ServerNowMs from the heartbeat.
    private void PersistActiveRunMarker(long nowMs)
    {
        if (_lastRunStartMs == 0 || _lastRunId == 0) return;   // dungeon runs only (open-world has no timer)
        if (nowMs <= 0) return;                                 // no valid clock → invalid freshness anchor
        // Persist the BEST-KNOWN party (not the possibly-transient _lastTeamId): so a further crash restores
        // the real party, and the ~30 s heartbeat self-heals a marker written before the party synced.
        long party = RelaunchMarker.ResolvePartyId(_lastTeamId, _services.PartySnapshot.PartyId, _relaunchPartyFallback);
        var m = new ActiveRunMarker(_lastRunId, party, _lastRunStartMs, nowMs);
        _activeRunMarker = m;
        _lastMarkerWriteMs = nowMs;
        _services.Data.Write(MarkerFileName, Encoding.UTF8.GetBytes(RelaunchMarker.Serialize(m)));
    }

    // Per-tick relaunch-marker maintenance, called once from OnUpdate (one line there keeps the already-large
    // Plugin.cs footprint minimal): refresh the heartbeat, then run the stale-marker clear.
    private void TickRelaunchMarker()
    {
        TickRelaunchHeartbeat();
        PollStaleMarkerClear();
    }

    // ~30 s heartbeat refreshing lastAliveMs while a dungeon run is active, so the freshness bound measures
    // the relaunch downtime — a 20-min run that crashes must still restore.
    private void TickRelaunchHeartbeat()
    {
        if (_lastRunStartMs == 0 || _lastRunId == 0) return;
        long now = _services.CombatSnapshot.ServerNowMs;
        if (now <= 0 || now - _lastMarkerWriteMs < RelaunchHeartbeatMs) return;
        PersistActiveRunMarker(now);
    }

    // Delete the marker (both in-memory + disk) and reset the write/grace state. Called on a normal run
    // boundary (BankRunBoundary) and by the stale-marker clear.
    private void ClearActiveRunMarker()
    {
        _activeRunMarker = null;
        _lastMarkerWriteMs = 0;
        _staleMarkerFirstOutMs = 0;
        // Defense-in-depth (qa 2026-08-16): drop the party fallback whenever the marker is dropped, so the
        // stale-marker clear (kicked/timeout) can't leave a stale fallback for a later run even if a run
        // boundary were ever missed. BankRunBoundary also resets it unconditionally; this covers the
        // clear-without-boundary path.
        _relaunchPartyFallback = 0;
        _services.Data.Delete(MarkerFileName);
    }

    // Belt for the crash/DC residuals the owner flagged (2026-08-16): reconnect that lands OUTSIDE the
    // marked dungeon (timed out to town, moved on). Once settled out of the marked run for the grace window,
    // clear the marker so a later re-entry of the SAME instance is a fresh run, not wrongly glued. The
    // kicked-from-party residual is handled in the restore gate (party mismatch), not here.
    private void PollStaleMarkerClear()
    {
        if (_activeRunMarker is not { } m) return;
        if (!RelaunchMarker.IsSettledOutOfMarkedRun(
                m.LevelUuid, _services.Dungeon.CurrentRunId, _services.ClientState.IsWorldActive))
        {
            _staleMarkerFirstOutMs = 0;   // back in the marked run / loading / not in world → reset the debounce
            return;
        }
        long now = _services.CombatSnapshot.ServerNowMs;
        if (now <= 0) return;
        if (_staleMarkerFirstOutMs == 0) { _staleMarkerFirstOutMs = now; return; }
        if (now - _staleMarkerFirstOutMs >= StaleMarkerGraceMs)
        {
            LogRelaunchStaleClear(m.LevelUuid, _services.Dungeon.CurrentRunId);
            ClearActiveRunMarker();
        }
    }
}
