using System;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

// Pure formatting + archived-identity helpers of the Combat History window (durations, clocks, the archived class
// line). Split out of Plugin.HistoryWindow.cs (500-LoC cap) ahead of the house-style History redesign.
public sealed partial class Plugin
{
    // Class label for an ARCHIVED row: EVERY class the player actually played in THIS archive, from
    // the frozen classSpans clamped to the archive's own [EnteredAtMs, ArchivedAtMs] window, joined in
    // play order (e.g. "Verdant Oracle · Frost Mage"). The single frozen professionId (attr 220 at
    // bank time) can't express "was Oracle, ended as Frost Mage", so a clear-phase archive banked
    // after a swap mislabelled the player as the boss class (the LUz6opkvNX bug). A single-class
    // archive bakes no spans → falls back to the frozen professionId (unchanged for the common case);
    // an unfrozen/legacy row falls back to the live class line.
    private string ResolveArchivedClassLine(EntitySnapshot? frozen, long startMs, long endMs, EntityId id)
    {
        if (frozen != null)
        {
            // Battle Imagine transforms are not classes (owner 2026-09-25) — "Shield Knight · Natsu" → "Shield Knight".
            var profs = PlayableClass.PlayableOnly(ClassesPlayedInWindow(frozen, startMs, endMs));
            if (profs.Count > 0)
            {
                var line = ProfessionDisplayName(profs[0]);
                for (var i = 1; i < profs.Count; i++) line += " · " + ProfessionDisplayName(profs[i]);
                return line;
            }
            if (ResolveSnapProfession(frozen) is { Length: > 0 } fc) return fc;
        }
        return GetClassLine(id);
    }

    // ProfessionId → display name (mirrors ResolveSnapProfession's resolution for a single id).
    private string ProfessionDisplayName(int profId)
    {
        var prof = _services.GameData.Combat.GetProfession(profId);
        return prof is { Name: { Length: > 0 } n } ? n : _loc.TFormat("common.classNumbered", profId);
    }

    // ----- pure formatting helpers (carried over from the IMGUI build) -----

    private static long ComputeArchivedDps(long totalDamage, long durationMs)
    {
        long secs = durationMs / 1000; if (secs < 1) secs = 1; return totalDamage / secs;
    }

    // Compact session-list clock: "h:mm" + a single lowercase a/p meridiem (e.g. 2:14p, 11:05a).
    private static string FormatSessionClock(long ms)
    {
        if (ms <= 0) return "—";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        int hour12 = dt.Hour % 12; if (hour12 == 0) hour12 = 12;
        char ap = dt.Hour < 12 ? 'a' : 'p';
        return $"{hour12}:{dt.Minute:00}{ap}";
    }

    private static string FormatSessionTimestampLong(long ms)
        => ms <= 0 ? "—" : DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("M/d/yyyy, h:mm:ss tt");

    private static string FormatSessionDurationShort(long durationMs)
    {
        long secs = durationMs / 1000; if (secs < 0) secs = 0;
        long m = secs / 60, s = secs % 60;
        return m > 0 ? $"{m}m {s}s" : $"{s}s";
    }

    /// <summary>
    /// Real elapsed span of an archived segment: archive time minus combat start. Both fields are
    /// already persisted on every entry, so this is derivable retroactively — no schema change.
    ///
    /// This exists because <c>CombatDurationMs</c> is the DAMAGE-HIT SPAN, not elapsed time:
    /// <c>FirstHitMs</c>/<c>LastHitMs</c> are written only in the damage handler
    /// (<c>Plugin.Capture.cs</c>), so healing and damage-taken never move them. A heal-only tail has a
    /// legitimate span of 0 while seconds of wall-clock pass — which is why the owner saw `0s` rows on
    /// archives that plainly covered real time (report 2026-07-28, run 890357114281656320).
    ///
    /// Returns 0 when no combat start was recorded (otherwise <c>arch - 0</c> would report ~56 years)
    /// and clamps a backwards server clock.
    /// </summary>
    internal static long RealDurationMs(long enteredAtMs, long archivedAtMs)
    {
        if (enteredAtMs <= 0) return 0;
        var span = archivedAtMs - enteredAtMs;
        return span > 0 ? span : 0;
    }

    /// <summary>
    /// History-row duration label: real elapsed span, with the combat span in parentheses when the two
    /// differ — `8.3s (0s combat)`. Format chosen by the owner 2026-07-28. Under a minute it carries one
    /// decimal (their example was `8.3s`); at a minute and above it uses the existing m/s style. The
    /// parenthetical is omitted when both render identically, so ordinary fights stay uncluttered.
    ///
    /// Display only — <c>CombatDurationMs</c> is reported verbatim and never adjusted, because DPS
    /// divides by it.
    /// </summary>
    // combinedFormat carries the localized "{0} ({1} combat)" template (default = English) so the pinned
    // RealDurationTests keep asserting the exact English strings while the display path localizes the word.
    internal static string FormatRowDuration(long realMs, long combatMs, string combinedFormat = "{0} ({1} combat)")
    {
        if (realMs < 0) realMs = 0;
        if (combatMs < 0) combatMs = 0;
        // Suffix suppressed when both land on the same whole second — otherwise every ordinary fight
        // would repeat its own number. Compared on the VALUES, not the rendered strings, so the two
        // formats (tenths vs m/s) can't disagree about equality.
        if (realMs / 1000 == combatMs / 1000) return FormatDurationWithTenths(realMs);
        // Combat span uses the same whole-second formatter as the rest of the UI, so a 0 span reads
        // "0s combat" exactly as the owner specified — not "0.0s".
        return string.Format(combinedFormat, FormatDurationWithTenths(realMs), FormatSessionDurationShort(combatMs));
    }

    /// <summary>
    /// Full x-extent for the session chart, in seconds. Uses the REAL elapsed span, not the damage
    /// span: the timeline series are bucketed relative to <c>_combatStartMs</c> (= <c>EnteredAtMs</c>,
    /// see <c>Plugin.Capture.cs</c>'s <c>TimelineFor(...).Add(..., _combatStartMs, ...)</c>), so the
    /// series domain genuinely runs to the archive moment. Showing only the damage span cut the chart
    /// short of the data it holds — owner ruling 2026-07-28: if the row reports real duration, the
    /// graph must cover the whole duration too.
    ///
    /// Falls back to the combat span when no combat start was recorded, so the chart is never 0-wide.
    /// DPS/HPS rates are NOT affected — those divide by <c>CombatDurationMs</c> and are untouched.
    /// </summary>
    internal static float ChartExtentSeconds(long realMs, long combatMs)
    {
        var ms = realMs > 0 ? realMs : combatMs;
        return ms > 0 ? ms / 1000f : 0f;
    }

    // One decimal below a minute, whole seconds in m/s form above it.
    private static string FormatDurationWithTenths(long ms)
    {
        if (ms < 0) ms = 0;
        if (ms < 60_000) return $"{ms / 1000f:0.0}s";
        long secs = ms / 1000, m = secs / 60, s = secs % 60;
        return $"{m}m {s}s";
    }

    private static long ComputeSessionMetricTotal(EncounterHistoryEntry h, Metric m)
    {
        long sum = 0;
        foreach (var s in h.Stats.Values) sum += MetricValueOf(s, m);
        return sum;
    }

    private static string FormatPercent(float fraction) => $"{fraction * 100f:F1}%";
}
