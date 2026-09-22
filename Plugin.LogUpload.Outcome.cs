using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Replay;

namespace Stellar.CombatMeter;

// Segment-outcome logging for an assembled log, split out of Plugin.LogUpload.cs (which sits at the
// file-size guardrail) to keep it under the pre-branch line count. Pure diagnostic logging — it
// observes an already-decided segment/log, it never decides whether to archive/upload/discard, so it
// carries none of the Spool.* call sites (those all stay in Plugin.LogUpload.cs; see
// docs/recon/combatmeter-archive-flow.md invariant "Rotation observes archive decisions; it never
// makes them").
//
// Also carries the summary-upload OUTCOME plumbing (UploadOutcome, ShouldSendChunks,
// OnSummaryUploadFailed — review F2, 2026-09-20): Plugin.LogUpload.cs grew back over its pre-branch
// line count (606 -> 626) adding the header-retry attempt telemetry, so these three members moved
// here rather than growing an already-over-limit file further. OnSummaryUploadOk stays in
// Plugin.LogUpload.cs (it owns the chunk/positions SEND call sites this file deliberately does not
// carry, per the header-comment invariant above); UploadOutcome/ShouldSendChunks/OnSummaryUploadFailed
// are pure decision/logging leaves with no Spool.* or upload-fire-and-forget call sites of their own
// beyond what they already had. Zero behaviour change from the move itself — same partial class, same
// accessibility.
public sealed partial class Plugin
{
    /// <summary>
    /// Emits the one info line both the upload path (<c>AssembleAndUpload</c>) and the retain path
    /// (<c>RetainAssembled</c>) print for an assembled <paramref name="log"/>/<paramref name="seg"/> —
    /// dmg/buff/sheet chunk counts plus the segment's cast-row count (rides the segment via
    /// <see cref="SpoolSegment.Counts"/>'s <see cref="SpoolCounts.CastRows"/> so BOTH archive paths print
    /// it, not just the upload one) — with the verb/outcome wording supplied by <paramref name="what"/>
    /// (e.g. "Uploading" vs "Retained (not uploaded)"), then, only when <paramref name="seg"/> recorded a
    /// write fault, the accompanying warning that those chunks will be skipped at upload (blob missing).
    /// </summary>
    private void LogSegmentOutcome(string what, CombatLog log, SpoolSegment seg)
    {
        _services.Log.Info(
            $"[CombatMeter.SP1] {what} log {log.Header.LogId} levelUuid={log.Header.Encounter.LevelUuid} " +
            $"({seg.Dmg.Count} dmg chunk(s), {seg.Buff.Count} buff chunk(s), {seg.Sheet.Count} sheet chunk(s)) casts={seg.Counts.CastRows}.");
        if (seg.Counts.WriteFaults > 0)
            _services.Log.Warning($"[CombatMeter.SP1] {seg.Counts.WriteFaults} spool blob write(s) failed for segment {seg.SegmentId} — those chunks will be skipped at upload (blob missing).");
    }

    // Bundles the header POST's final HTTP status with which attempt (1-based) it landed on (spec
    // 2026-09-20 § 2 header-retry telemetry) as ONE parameter — rather than a bare extra `int attempt`
    // — so this doesn't grow OnSummaryUploadOk's already-tracked 6-parameter count (docs/tech-debt.md;
    // CLAUDE.md: a pre-existing size violation must not grow further).
    private readonly record struct UploadOutcome(int Status, int Attempt);

    // Pure decision: does a landed summary send its captured event chunks? Extracted (mirrors
    // ShouldRetainUnsentArchive/PhaseFromResult in Plugin.LogUpload.cs) so the "upload all — never skip
    // on the merge verdict" rule (owner 2026-08-25) pins headless without a live Plugin/IPluginServices
    // instance (LogUploadTests' own header: no test in this suite constructs one). `verdict` is
    // deliberately UNUSED — chunks send whenever the segment actually has any, regardless of `Kept`;
    // spec 2026-09-20 § 1(b) re-confirms the pre-2026-08-25 "skip on Kept=false" behaviour stays gone.
    internal static bool ShouldSendChunks(int chunkCount, UploadVerdict? verdict) => chunkCount > 0;

    // Failure leg of the summary-upload callback (thread-pool thread — thread-safe calls only;
    // never touch uGUI). The header exhausted every retry (LogUploader.RetryDelays) — chunks are
    // deliberately NEVER sent here (spec 2026-09-20 § 2 point 3: the server never saw this logId, so
    // there is nothing for a chunk to attach to); PersistReUpload already retained the true bodies
    // (auto path) so the info line below names the durable recovery the owner can actually click.
    //
    // `attempt` is 0 only when NOTHING was ever sent (a serialize/gzip error before the first HTTP
    // attempt — see LogUploader.UploadFireAndForget/UploadAsync's two `onComplete?.Invoke(false, 0, …,
    // 0)` call sites); printing "attempt=0" there read as a bug to a tester (review F5), so 0 prints as
    // "n/a" instead — any real attempt count is always >= 1.
    //
    // The "Retained locally — use Re-upload" line is gated on `verdict is null` (review F3): a true
    // header failure never reached the server, so nothing is uploaded and Re-upload is the only path
    // back. A non-null `verdict` here can only come from the 409-supplement-transient arm
    // (LogUploader.cs's HandleAlreadyUploadedAsync: `onComplete(false, supStatus, "supplement upload
    // failed", verdict, …)`) — the server ALREADY holds the run (that is what produced the 409/verdict
    // in the first place); only the tiny own-detail supplement failed, so telling the owner to
    // "re-upload" the whole run is misleading noise on a run that is not actually missing.
    private void OnSummaryUploadFailed(PositionUploadDoc? replayDoc, int status, string? err, UploadVerdict? verdict, int attempt)
    {
        var attemptLabel = attempt == 0 ? "n/a" : attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _services.Log.Warning($"[CombatMeter.SP1] Upload FAILED (HTTP {status}) attempt={attemptLabel}: {err}");
        if (verdict is null)
            _services.Log.Info("[CombatMeter.SP1] Retained locally — use \"Re-upload\" in the history window to retry.");
        // Summary failed — fall back to today's behavior: positions upload ungated
        // (they attach via the pending path even without a matching segment). The one
        // exception: a failed SUPPLEMENT still carried a verdict whose HavePositions
        // came from the 409 body — respect it (Task 10's path).
        if (replayDoc is not null && verdict?.HavePositions != true) UploadReplayDoc(replayDoc);
    }
}
