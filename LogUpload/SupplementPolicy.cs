// Task 13: the pure decision logic behind a 409 ("server already has this run") response.
//
// A 409 from /upload means the run's summary is already on the server (deduped by run identity),
// so the RUN itself is uploaded — that is a resolved success, not a failure. The only thing left to
// consider is the tiny own-detail supplement (self actor gear/skills the elected uploader may lack).
// We send it ONLY when we actually hold local actor detail to contribute; a deferred/reloaded manual
// retry carries no local actor in its snapshot (the local entity-id differs across sessions), so
// there is nothing to send and the run is already fully covered. Whether a supplement WE DID send
// should revert the UI to a retryable failure depends on the error class: transport/5xx are transient
// (retryable); a definitive 2xx/4xx is not.
//
// Services-free + pure so it is unit-testable without HTTP or a live Plugin.

namespace Stellar.CombatMeter.LogUpload;

internal static class SupplementPolicy
{
    /// <summary>
    /// True when the log carries local (self) actor detail worth supplementing to a run the server
    /// already has. False for a reloaded/deferred entry whose snapshot has no local actor — there is
    /// nothing to send, so the 409 resolves as a plain "already uploaded" success.
    /// </summary>
    internal static bool ShouldSendSupplement(CombatLog log)
    {
        foreach (var a in log.Actors.Values)
            if (a.IsLocal) return true;
        return false;
    }

    /// <summary>
    /// True when a supplement HTTP status is a transient error worth a UI retry: a transport failure
    /// (status 0 — no response received) or a 5xx server error. A 2xx succeeded; a 4xx (incl. 404
    /// window-gone) is definitive and a retry cannot fix it — those leave the run resolved-uploaded.
    /// </summary>
    internal static bool IsRetryable(int supplementStatus)
        => supplementStatus == 0 || (supplementStatus >= 500 && supplementStatus < 600);
}
