// Header window clamp (owner "do 1", 2026-09-06 — the zero-duration double-bank tail): a
// `reason=boss durMs=0` archive whose EnteredAtMs was backdated from the NEXT segment's first hit can
// land AFTER this segment's own ArchivedAtMs, producing a negative header.encounter.durationMs — the
// server rejects the WHOLE upload outright (`400 /header/encounter/durationMs must be >= 0`) with no
// retry path. Split out of CombatLogAssembler.cs (CM-273 review, file-size discipline) — the clamp
// math and its Warning log line are one cohesive concern, kept together here so Assemble() only ever
// carries the single-line call site.

using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter.LogUpload;

internal sealed partial class CombatLogAssembler
{
    /// <summary>
    /// Clamps the upload header's encounter window to a non-negative duration. Observed: a
    /// `reason=boss durMs=0` archive banked shortly after a `boundary` archive whose OWN
    /// <paramref name="startMs"/> (EnteredAtMs) came from the NEXT segment's first hit — later than
    /// that same archive's <paramref name="rawEndMs"/> (ArchivedAtMs) — producing a NEGATIVE
    /// <c>header.encounter.durationMs</c> that the server rejects outright (<c>400
    /// /header/encounter/durationMs must be &gt;= 0</c>), failing the WHOLE upload with no retry
    /// path. Fixed at the envelope only: <c>durationMs = max(0, end - start)</c>, and when
    /// <paramref name="rawEndMs"/> is before <paramref name="startMs"/> the end is pulled UP to start
    /// so the window becomes a POINT rather than staying inverted. Pure — does not change how
    /// archives are decided/banked, and does not touch <c>EncounterHistoryEntry</c>
    /// (<c>CombatDurationMs</c>, real elapsed) the history UI reads; only this header's own numbers.
    /// The returned <c>Clamped</c> flag is the SAME condition <see cref="WarnIfClamped"/> logs on —
    /// the one place that decides "was this window clamped."
    /// </summary>
    internal static (long EndMs, long DurationMs, bool Clamped) ClampEncounterWindow(long startMs, long rawEndMs)
    {
        var clamped = rawEndMs < startMs;
        return clamped ? (startMs, 0L, true) : (rawEndMs, rawEndMs - startMs, false);
    }

    /// <summary>
    /// Logs a Warning when <see cref="ClampEncounterWindow"/> clamped <paramref name="entry"/>'s
    /// header window — called once from <see cref="Assemble"/> (the upload path only;
    /// <c>PrepareReplayDoc</c>'s <see cref="BuildEncounter"/> call site never logs). Reuses
    /// <see cref="ClampEncounterWindow"/>'s own <c>Clamped</c> result rather than re-deriving the
    /// inequality a second time, so there is exactly one condition that decides "was this clamped."
    /// </summary>
    private static void WarnIfClamped(Plugin.EncounterHistoryEntry entry, IPluginLog log)
    {
        var (_, _, clamped) = ClampEncounterWindow(entry.EnteredAtMs, entry.ArchivedAtMs);
        if (clamped)
        {
            log.Warning(
                $"[CombatMeter.SP1] header window clamped: enter={entry.EnteredAtMs} arch={entry.ArchivedAtMs} → durationMs 0");
        }
    }
}
