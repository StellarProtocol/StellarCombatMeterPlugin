using System.Collections.Concurrent;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Phase of a per-run upload, surfaced to the history UI to drive the Upload button's label/state.
/// </summary>
internal enum UploadPhase { Idle, InFlight, Done, Failed }

/// <summary>
/// Tracks per-entry upload status, keyed by the archived <see cref="Plugin.EncounterHistoryEntry"/>.
/// Written by the fire-and-forget upload callback (thread-pool thread) and read by the history UI
/// (main thread); the stored value is an immutable record so a cross-thread read is never torn.
/// Services-free so the state machine is unit-testable without constructing a full <see cref="Plugin"/>.
/// </summary>
internal sealed class UploadStatusTable
{
    private sealed record Status(UploadPhase Phase, string? Url);

    private readonly ConcurrentDictionary<Plugin.EncounterHistoryEntry, Status> _store = new();

    /// <summary>Records the current phase (and optional run URL) for an entry.</summary>
    internal void Set(Plugin.EncounterHistoryEntry entry, UploadPhase phase, string? url = null)
        => _store[entry] = new Status(phase, url);

    /// <summary>Phase for an entry; <see cref="UploadPhase.Idle"/> for one never uploaded.</summary>
    internal UploadPhase PhaseFor(Plugin.EncounterHistoryEntry entry)
        => _store.TryGetValue(entry, out var s) ? s.Phase : UploadPhase.Idle;

    /// <summary>Run URL for an entry, or <see langword="null"/> if none recorded.</summary>
    internal string? UrlFor(Plugin.EncounterHistoryEntry entry)
        => _store.TryGetValue(entry, out var s) ? s.Url : null;

    /// <summary>Drops the status for an entry that has left history, so it (and its Stats/Series/Entities)
    /// is no longer rooted by this table. Call when an entry is evicted or deleted.</summary>
    internal void Forget(Plugin.EncounterHistoryEntry entry) => _store.TryRemove(entry, out _);

    /// <summary>Drops all recorded statuses. Call when history is cleared wholesale.</summary>
    internal void Clear() => _store.Clear();

    /// <summary>The phase to PERSIST for a live phase: a transient <see cref="UploadPhase.InFlight"/>
    /// collapses to <see cref="UploadPhase.Idle"/> (never persist "Uploading…" — a relaunch caught
    /// mid-upload would otherwise show a run stuck in flight); terminal Done/Failed and Idle persist
    /// as-is so an uploaded run restores "✓ Uploaded" and a failed one stays retryable.</summary>
    internal static UploadPhase Persistable(UploadPhase phase)
        => phase == UploadPhase.InFlight ? UploadPhase.Idle : phase;
}
