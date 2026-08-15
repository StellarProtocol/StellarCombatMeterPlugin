using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>One player's damage/healing/taken, summed across the run's banked entries.</summary>
internal sealed record DiscordPlayerRow(string Name, long Damage, long Healing, long Taken);

/// <summary>Whole-run party summary folded from a run's banked history entries. <c>MapName</c> is left
/// RAW (the observer resolves the display name) and <c>Link</c> is null (the observer attaches it via
/// a <c>with</c> expression once the run page URL is known).</summary>
internal sealed record DiscordRunSummary(
    string MapName,
    string Verdict,
    long RealDurationMs,
    long RunCombatSpanMs,
    IReadOnlyList<DiscordPlayerRow> Rows,
    string? Link);
