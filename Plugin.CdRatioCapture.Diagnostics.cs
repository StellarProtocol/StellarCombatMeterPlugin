using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>
/// Diagnostic-mode logging for the D10 cooldown-ratio capture (<see cref="TickCdRatio"/>). Gated on
/// <see cref="StellarDiagnostics.IsEnabled"/> like every other <c>.Diagnostics.cs</c> partial, so the
/// production partial calls it unconditionally (coding-standards § Diagnostics).
/// </summary>
public sealed partial class Plugin
{
    /// <summary>Logs the emitted ratio together with the rows that produced it. This line is how the first
    /// testing run settles the accelerate-vs-reduce formula (xDPS spec § 6.5): the per-row duration/valid-cd
    /// pair beside the ratio says whether the game shortens the cooldown by the ratio or accelerates its
    /// countdown. <c>disagreements</c> is the running count of ticks whose fresh rows did not agree.</summary>
    private void LogCdRatio(int ratio, List<SkillCooldown> fresh)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var sb = new StringBuilder("[CombatMeter][cd-ratio] ratio=").Append(ratio)
            .Append(" disagreements=").Append(_cdRatio.Disagreements)
            .Append(" fresh=");
        for (var i = 0; i < fresh.Count; i++)
        {
            var r = fresh[i];
            sb.Append(" skill=").Append(r.SkillId)
              .Append(" dur=").Append(r.DurationMs)
              .Append(" valid=").Append(r.ValidCdTimeMs)
              .Append(" accel=").Append(r.AccelerateCdRatio);
        }
        _services.Log.Info(sb.ToString());
    }
}
