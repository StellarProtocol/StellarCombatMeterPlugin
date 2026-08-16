using System;

namespace Stellar.CombatMeter.AutoArchive;

/// <summary>
/// The persisted "active run" marker for mid-dungeon-relaunch recovery
/// (docs/recon/run-identity-relaunch-split.md § "Concrete owner-approved design", 2026-08-16). A run's
/// server identity is keyed on <c>&lt;levelUuid&gt;-&lt;dungeonStartMs/1000&gt;</c> (+ party); the game
/// RE-STAMPS <c>RunTimerStartMs</c> across a relaunch/reconnect, so a crash/relaunch mid-run yields a new
/// <c>runStartS</c> and the server files a SECOND run page for one physical dungeon. This marker persists
/// the run's ORIGINAL dungeon-start (the in-memory <c>_lastRunStartMs</c> pin) across the process death so
/// the next combat start can restore it and the run continues under one identity.
///
/// Pure value type — no Abstractions dependency, fully unit-testable (mirrors
/// <see cref="RunBoundaryTracker"/>; Plugin itself can't be headless-instantiated).
/// </summary>
internal readonly struct ActiveRunMarker
{
    internal readonly long LevelUuid;      // the dungeon run id (IDungeonState.CurrentRunId == _lastRunId)
    internal readonly long PartyId;        // GrpcTeam team_id latched at combat start (0 = solo/unformed)
    internal readonly long DungeonStartMs; // the run's original _lastRunStartMs (RunTimerStartMs, pre-restamp)
    internal readonly long LastAliveMs;    // server epoch ms of the last heartbeat — freshness anchor

    internal ActiveRunMarker(long levelUuid, long partyId, long dungeonStartMs, long lastAliveMs)
    {
        LevelUuid = levelUuid;
        PartyId = partyId;
        DungeonStartMs = dungeonStartMs;
        LastAliveMs = lastAliveMs;
    }
}

/// <summary>
/// Pure serialization + restore/clear decisions for the mid-dungeon-relaunch marker. Everything here is
/// static and clock-injected so it is unit-testable without a live game (see RelaunchMarkerTests). The
/// stateful wiring (read/write plugindata, heartbeat throttle, grace debounce) lives in
/// Plugin.RelaunchMarker.cs.
/// </summary>
internal static class RelaunchMarker
{
    /// <summary>Owner-chosen freshness bound: only restore when the relaunch GAP (now − lastAliveMs, which
    /// the ~30 s heartbeat keeps measuring the downtime, NOT the run's length) is within 10 minutes.</summary>
    internal const long MaxRelaunchGapMs = 600_000;

    private const string Tag = "crm1";   // versioned envelope tag; bump on any field-layout change

    internal static string Serialize(in ActiveRunMarker m)
        => $"{Tag} {m.LevelUuid} {m.PartyId} {m.DungeonStartMs} {m.LastAliveMs}";

    /// <summary>Parses a serialized marker. Never throws; returns <c>false</c> (marker default) on any
    /// malformed / wrong-version / non-numeric input so a corrupt file simply disables recovery.</summary>
    internal static bool TryDeserialize(string? text, out ActiveRunMarker marker)
    {
        marker = default;
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text.Split(' ');
        if (parts.Length != 5 || parts[0] != Tag) return false;
        if (!long.TryParse(parts[1], out var level) || !long.TryParse(parts[2], out var party)
            || !long.TryParse(parts[3], out var start) || !long.TryParse(parts[4], out var alive))
            return false;
        marker = new ActiveRunMarker(level, party, start, alive);
        return true;
    }

    /// <summary>The restore gate — returns the <c>DungeonStartMs</c> to restore, or <c>0</c> for "no
    /// restore, use the live (fresh) start". Restores ONLY when we are standing in the SAME instance AND
    /// the SAME party AND the relaunch gap is fresh. The party check (owner add 2026-08-16) makes the
    /// client's runStartS decision consistent with the server's party-keyed identity: kicked-from-party or
    /// re-formed → a different key server-side, so a same-instance continuation with a different party is a
    /// NEW run, not a glue. Clock guards (no server time yet / clock ran backwards) fail SAFE to no-glue.</summary>
    internal static long ResolveRelaunchStart(
        ActiveRunMarker? marker, long currentRunId, long currentPartyId, long nowMs, long maxGapMs)
    {
        if (marker is not { } m) return 0;
        if (m.LevelUuid == 0 || currentRunId == 0) return 0;   // no dungeon on one side → nothing to continue
        if (m.LevelUuid != currentRunId) return 0;             // different instance (town / other run)
        // Party mismatch rejects ONLY when the live party is KNOWN (non-zero). On reconnect the GrpcTeam
        // snapshot lags the first combat event (measured: live party=0 while the marker held 206285121,
        // run sea/kqCsvtAMx3), so a strict compare wrongly declined the good-case restore and the run
        // split on the re-stamped runStartS. Treat live 0 as "not synced yet → don't reject": the server
        // still separates by the UPLOADED partyId (resolved late via LatchTeamId at archive, once the party
        // HAS synced), so a genuine different-party continuation can't false-merge even when restored. A
        // CONFIRMED different party (both non-zero and different) still rejects.
        if (currentPartyId != 0 && m.PartyId != currentPartyId) return 0;
        if (m.DungeonStartMs == 0) return 0;                   // marker never held a real start (guard)
        if (nowMs <= 0 || m.LastAliveMs <= 0) return 0;        // no valid clock → can't judge freshness
        if (nowMs < m.LastAliveMs) return 0;                   // clock went backwards → distrust
        if (nowMs - m.LastAliveMs > maxGapMs) return 0;        // relaunch gap too long → a new run
        return m.DungeonStartMs;
    }

    /// <summary>The party id an archive stamps as its run identity, with the mid-relaunch fallback.
    /// Preference: the once-per-run latch (<paramref name="latched"/>, <c>_lastTeamId</c>) if non-zero
    /// (unchanged from <c>LatchTeamId</c> — a mid-run party change never relabels the run); else the live
    /// party if known (non-zero); else <paramref name="relaunchFallback"/> — the marker's party, used ONLY
    /// when a reconnect's game-party (GrpcTeam) has not re-synced yet so both latch and live read 0. Without
    /// this, a short post-reconnect run uploads <c>partyId=0</c> and splits from its party (run
    /// sea/UD87unsYz2). Live is preferred over the fallback so a genuine party CHANGE (kick to a new party)
    /// still wins once it syncs. Non-relaunch runs pass <c>relaunchFallback=0</c> → identical to the old
    /// <c>latched != 0 ? latched : live</c>.</summary>
    internal static long ResolvePartyId(long latched, long live, long relaunchFallback)
        => latched != 0 ? latched : (live != 0 ? live : relaunchFallback);

    /// <summary>Whether a run-boundary bank should clear the marker: ONLY when the run that is ending
    /// (<paramref name="outgoingRunId"/> = the latched <c>_lastRunId</c> being banked) IS the marker's run.
    /// BankRunBoundary also fires on a dungeon ENTRY (a reconnect loads into the instance → OnSceneChanged
    /// with <c>outgoingRunId == 0</c>, BEFORE the first combat) — clearing there would wipe the marker the
    /// restore needs. Gating on the outgoing id matching the marker means: a genuine LEAVE/run-end of the
    /// marked run clears it (so a later re-entry is fresh), while a reconnect's entry (outgoing 0) leaves it
    /// standing for the restore. Root cause of the 2026-08-16 first owner test's missing restore.</summary>
    internal static bool ShouldClearOnBoundary(ActiveRunMarker? marker, long outgoingRunId)
        => outgoingRunId != 0 && marker is { } m && m.LevelUuid == outgoingRunId;

    /// <summary>Raw "settled out of the marked run" condition for the stale-marker clear (the caller applies
    /// a grace debounce on top — see Plugin.RelaunchMarker.cs). True only once the client is in a STABLE
    /// world scene (<paramref name="worldActive"/> = <c>IClientState.IsWorldActive</c>, the framework's
    /// sole protective gate — false mid-transition AND during in-world zone loads, stricter than
    /// <c>Phase == World</c>) AND not standing in the marked instance — i.e. kicked to town / timed out.
    /// Gating on <c>IsWorldActive</c> (rather than a hand-rolled <c>Phase==World &amp;&amp; !Loading</c>) keeps a
    /// transient loading-<c>0</c> or a zone-load hop from being misread as a town observation, which would
    /// wrongly clear a marker the good-case restore still needs.</summary>
    internal static bool IsSettledOutOfMarkedRun(long markerLevelUuid, long currentRunId, bool worldActive)
    {
        if (markerLevelUuid == 0) return false;   // no meaningful marker
        if (!worldActive) return false;            // not a stable world scene (boot/title/load/zone-load) → unreliable
        return currentRunId != markerLevelUuid;    // settled in world but not in the marked instance → out
    }
}
