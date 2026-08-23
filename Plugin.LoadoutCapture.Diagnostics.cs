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

    // Previous profession seen by the line below. DIAGNOSTICS-ONLY (it is never read unless
    // STELLAR_DIAGNOSTICS is on, and nothing in the capture path consults it) — it exists so the log
    // still shows class TRANSITIONS now that PollLocalProfession, which used to own that memo and emit
    // its own `prof A->B` line, is deleted.
    private int _diagLastProf;

    // Fired from TickBuildRecapture on the tick after ILoadout.LiveStateChanged landed (or after the
    // run-start arm) — records the class the capture is keyed to and the gear GetSelfGear returns for
    // it. Since 2026-08-23 this is THE plugin-side acceptance marker for the event rework: exactly one
    // line per real change the framework reported (plus one per run start), none on quiet ticks, and
    // a `A->B` transition marker whenever the class itself moved.
    private void LogGearSyncDiag(int prof)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var moved = prof != _diagLastProf ? $" prof {_diagLastProf}->{prof}" : "";
        _diagLastProf = prof;
        _services.Log.Info(
            $"[ClassGearDiag] gear SYNC consumed @ {_services.CombatSnapshot.ServerNowMs}ms " +
            $"prof={prof}{moved} gear=[{GearSig(_services.Inventory.GetSelfGear())}]");
    }

    /// <summary>
    /// THE CAPTURE-DECISION LINE (owner demand 2026-08-23): one line per capture ATTEMPT, saying what
    /// armed it, which class it was keyed to, and exactly what the accumulator DID. This is what turns
    /// "did my equipment change get captured?" into a self-contained in-town check — replace an item,
    /// read the log — with no run, archive or upload round-trip needed.
    ///
    /// <para><b>Format</b> (one line, <c>[LoadoutCapture]</c> prefix):</para>
    /// <code>
    /// [LoadoutCapture] trigger=live-state prof=2 decision=MINTED entries=2 id=a1b2c3d4 gear=11 mods=5 imagines=[3923,3976] talent=105/70 marker=4
    /// [LoadoutCapture] trigger=live-state prof=2 decision=SKIPPED nosignal=loadout (held — will retry)
    /// </code>
    /// <list type="bullet">
    ///   <item><c>trigger</c> — <c>boot</c> | <c>run-start</c> | <c>live-state</c> (the framework's
    ///   <c>ILoadout.LiveStateChanged</c>).</item>
    ///   <item><c>decision</c> — <c>MINTED</c> (new setup entry appended), <c>REPLACED-DRAFT</c> (a
    ///   different setup overwrote an unfought draft — the normal outcome for an in-town edit with no
    ///   combat since), <c>REMATCHED</c> (identical to an earlier entry — re-activated, not duplicated),
    ///   <c>NOOP-SAME</c> (identical to the active entry — nothing changed), <c>SKIPPED</c> (nothing was
    ///   captured; <c>nosignal=</c> names the field that wasn't ready and the flag is HELD).</item>
    ///   <item><c>id</c> — 8-hex digest of the setup-identity fields (<see cref="LoadoutCapture.IdentityDigest"/>).
    ///   A moved <c>id</c> beside a non-<c>NOOP-SAME</c> decision IS the proof the edit was seen.</item>
    /// </list>
    /// </summary>
    private void LogCaptureDecision(CaptureTrigger trigger, CapturedLoadout cap, CaptureDecision decision)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _diagHeldField = null;   // a real decision ends the hold — the next one logs again
        var sb = new StringBuilder("[LoadoutCapture] trigger=").Append(TriggerName(trigger))
            .Append(" prof=").Append(cap.ProfessionId)
            .Append(" decision=").Append(DecisionName(decision))
            .Append(" entries=").Append(_loadoutCapture.EntryCount(cap.ProfessionId))
            .Append(" id=").Append(LoadoutCapture.IdentityDigest(cap).ToString("x8"))
            .Append(" gear=").Append(cap.Gear.Count)
            .Append(" mods=").Append(cap.Modules.Count)
            .Append(" imagines=[");
        var imagines = cap.Imagines;
        for (var i = 0; i < (imagines?.Count ?? 0); i++) sb.Append(i == 0 ? "" : ",").Append(imagines![i]);
        sb.Append("] talent=").Append(cap.TalentStageId).Append('/').Append(cap.TalentNodes?.Count ?? 0)
          .Append(" marker=").Append(_combatEventMarker);
        _services.Log.Info(sb.ToString());
        LogLiveCaptureDiag(cap);   // the full slot:configId dump behind the digest
    }

    /// <summary>The SKIPPED half of the decision line: a capture attempt that read nothing because a
    /// game surface wasn't ready. The flag is HELD (re-armed), so this is a "not yet", never a loss —
    /// a following line with the same trigger reports the real decision.</summary>
    private void LogCaptureHeld(CaptureTrigger trigger, int professionId, string noSignalField)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        // Once per hold, not once per tick: the flag stays armed at the plugin's ~10 Hz cadence, and a
        // repeated line per tick would bury the decision lines this exists to make findable.
        if (_diagHeldField == noSignalField) return;
        _diagHeldField = noSignalField;
        _services.Log.Info(
            $"[LoadoutCapture] trigger={TriggerName(trigger)} prof={professionId} " +
            $"decision=SKIPPED nosignal={noSignalField} (held — will retry)");
    }

    private string? _diagHeldField;   // DIAGNOSTICS-ONLY de-dupe memo for the held line above

    private static string TriggerName(CaptureTrigger t) => t switch
    {
        CaptureTrigger.RunStart  => "run-start",
        CaptureTrigger.LiveState => "live-state",
        _                        => "boot",
    };

    private static string DecisionName(CaptureDecision d) => d switch
    {
        CaptureDecision.Minted        => "MINTED",
        CaptureDecision.ReplacedDraft => "REPLACED-DRAFT",
        CaptureDecision.Rematched     => "REMATCHED",
        _                             => "NOOP-SAME",
    };

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
