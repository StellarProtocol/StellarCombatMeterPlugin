using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

// Segment-outcome logging for an assembled log, split out of Plugin.LogUpload.cs (which sits at the
// file-size guardrail) to keep it under the pre-branch line count. Pure diagnostic logging — it
// observes an already-decided segment/log, it never decides whether to archive/upload/discard, so it
// carries none of the Spool.* call sites (those all stay in Plugin.LogUpload.cs; see
// docs/recon/combatmeter-archive-flow.md invariant "Rotation observes archive decisions; it never
// makes them").
public sealed partial class Plugin
{
    /// <summary>
    /// Emits the one info line both the upload path (<c>AssembleAndUpload</c>) and the retain path
    /// (<c>RetainAssembled</c>) print for an assembled <paramref name="log"/>/<paramref name="seg"/> —
    /// dmg/buff chunk counts plus <paramref name="buffEffectCount"/> — with the verb/outcome wording
    /// supplied by <paramref name="what"/> (e.g. "Uploading" vs "Retained (not uploaded)"), then, only
    /// when <paramref name="seg"/> recorded a write fault, the accompanying warning that those chunks
    /// will be skipped at upload (blob missing).
    /// </summary>
    private void LogSegmentOutcome(string what, CombatLog log, SpoolSegment seg, int buffEffectCount)
    {
        _services.Log.Info(
            $"[CombatMeter.SP1] {what} log {log.Header.LogId} levelUuid={log.Header.Encounter.LevelUuid} " +
            $"({seg.Dmg.Count} dmg chunk(s), {seg.Buff.Count} buff chunk(s), {buffEffectCount} buff effect(s)).");
        if (seg.WriteFaults > 0)
            _services.Log.Warning($"[CombatMeter.SP1] {seg.WriteFaults} spool blob write(s) failed for segment {seg.SegmentId} — those chunks will be skipped at upload (blob missing).");
    }
}
