using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.CombatMeter;

/// <summary>
/// Part B step 1 — class-swap → gear-sync tracing (owner gear-per-class investigation, 2026-08-03).
/// The event-driven gear re-capture (<see cref="Plugin.TickBuildRecapture"/>) didn't produce per-class
/// gear; these diagnostics reveal the ACTUAL wire sequence so the fix is designed from data, not a
/// third guess: on a class swap, does a gear sync fire, WHAT gear does <c>GetSelfGear</c> then carry,
/// and does it differ from the previous class's gear? All entry points short-circuit on
/// <see cref="StellarDiagnostics.IsEnabled"/> and log only on the game TICK (main thread — never off
/// the network/sync thread), so no IL2CPP read happens off-thread. Enable via STELLAR_DIAGNOSTICS=1.
/// </summary>
public sealed partial class Plugin
{
    // A stable one-line signature of the self gear (slot:configId …), so two log lines can be eyeballed
    // for "did the equipped set actually change on the swap?".
    private static string GearSig(IReadOnlyList<GearInstance> gear)
    {
        var sb = new StringBuilder(gear.Count * 12);
        foreach (var g in gear) sb.Append(g.Slot).Append(':').Append(g.ConfigId).Append(' ');
        return sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd();
    }

    // Fired from PollLocalProfession on the tick when attr 220 flips — records the profession change +
    // the gear GetSelfGear returns AT the swap instant (expected: still the OLD class's gear).
    private void LogProfChangeDiag(int oldProf, int newProf)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[ClassGearDiag] prof {oldProf}->{newProf} @ {_services.CombatSnapshot.ServerNowMs}ms " +
            $"gear-at-swap=[{GearSig(_services.Inventory.GetSelfGear())}]");
    }

    // Fired from TickBuildRecapture on the tick after ILoadout.LiveStateChanged landed — records the
    // profession now + the gear GetSelfGear returns (expected IF the fix worked: the NEW class's gear;
    // if unchanged from the swap line above, the wire never re-synced per-class gear on the swap).
    // Since 2026-08-23 this line is ALSO the plugin-side acceptance marker for the event rework: one
    // per real change the framework reported, none on unrelated container deltas.
    private void LogGearSyncDiag(int prof)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[ClassGearDiag] gear SYNC consumed @ {_services.CombatSnapshot.ServerNowMs}ms " +
            $"prof={prof} gear=[{GearSig(_services.Inventory.GetSelfGear())}]");
    }

    // EVENT-DRIVEN (fires when CaptureActiveClassLoadout captures — i.e. on a gear/module-change or
    // profession-change EVENT, never a poll): logs the LIVE captured per-class gear + modules. A
    // class+equipment switch run confirms, before any fight/upload, that the capture reflects the ACTUAL
    // equipped set — distinct per class, with manual edits + refine/enchant. Gated on STELLAR_DIAGNOSTICS.
    private void LogLiveCaptureDiag(CapturedLoadout cap)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var sb = new StringBuilder();
        sb.Append("[LiveCapture] prof=").Append(cap.ProfessionId).Append(" gear=").Append(cap.Gear.Count).Append(" [");
        foreach (var g in cap.Gear) sb.Append(g[0]).Append(':').Append(g[1]).Append(' ');
        sb.Append("] mods=").Append(cap.Modules.Count).Append(" [");
        foreach (var m in cap.Modules) sb.Append(m.Slot).Append(':').Append(m.ConfigId).Append('(').Append(m.Parts.Count).Append("p) ");
        sb.Append(']');
        if (cap.GearDetail.Count > 0)
        {
            var d = cap.GearDetail[0];
            sb.Append(" g0(slot=").Append(d.Slot).Append(" refine=").Append(d.RefineLevel).Append(" enchant=").Append(d.EnchantId).Append(')');
        }
        _services.Log.Info(sb.ToString());
    }
}
