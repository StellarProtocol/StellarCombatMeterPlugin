using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    /// <summary>Process rules § 15 hedge — a hook that never fires looks identical to a working one. The first N
    /// ticks that carried ANY fresh cooldown row are logged whether or not the ratio moved, so the very first run
    /// proves the tick is live even when every row states the same ratio; after that the line is change-only.</summary>
    private const int CdRatioDiagFirstTicks = 5;

    // DIAGNOSTICS-ONLY counter (never read unless STELLAR_DIAGNOSTICS is on; nothing in the capture path
    // consults it) — how many fresh-row ticks have already been logged unconditionally.
    private int _cdRatioDiagTicks;

    /// <summary>One line per fresh-row tick (see <see cref="CdRatioDiagFirstTicks"/>), then one per ratio CHANGE.
    /// This line is how the first testing run settles the accelerate-vs-reduce formula (spec § 6.5), so it prints
    /// the raw per-row fields, not just the derived scalar.</summary>
    private void LogCdRatio(int? changed, List<SkillCooldown> fresh)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_cdRatioDiagTicks < CdRatioDiagFirstTicks) _cdRatioDiagTicks++;
        else if (changed is null) return;
        var sb = new StringBuilder("[CombatMeter][cd-ratio] ratio=").Append(changed ?? _cdRatio.Last ?? 0)
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
