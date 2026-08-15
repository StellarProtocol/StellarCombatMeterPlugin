using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

// Upload compatibility floor — client side (owner ask 2026-08-16). When auto-upload is on, ask the
// server for the min accepted plugin version (GET /api/upload/compat) and, if this build is below it,
// tell the player to update BEFORE a run-end upload silently 426s. Two surfaces: a one-shot startup
// toast + a sticky line in the upload settings; and the send is WITHHELD (Plugin.LogUpload.cs) rather
// than fired-and-failed. FAIL-OPEN throughout — a 404 (route not deployed yet), an offline client, or
// an unreadable version never nags or withholds. The floor logic + fetch live in LogUpload/UploadCompat.cs.
//
// SCOPE: this touches only the upload SEND decision and its notices — never the archive engine, verdict,
// run-id, boss detection, or capture (capture is always-on; this gates a send, consistent with the
// 2026-08-14 capture-is-default-on doctrine).
public sealed partial class Plugin
{
    // Written on the fetch's thread-pool callback, read on the Unity main thread (the send gate and the
    // settings draw) — volatile so the read is never stale/torn.
    private volatile bool _uploadBelowFloor;
    private volatile string? _uploadFloorMin;
    // Set by the fetch callback (thread-pool), drained on the main thread (DrainCompatNotice from OnUpdate) —
    // Notifications is main-thread only, same rule the content-kinds notice follows.
    private volatile string? _compatNotice;
    // Main-thread only (InitUploadPolicy + SetUploadPolicy run on the main thread): fetch at most once per
    // session — the floor cannot change mid-session, and every /api call bills a Cloudflare invocation.
    private bool _compatChecked;

    /// <summary>True when the player has at least one content cell set to <c>auto</c> — i.e. auto-upload is
    /// on for something, which is the owner's trigger for the compat check ("if that player turn on the
    /// auto upload").</summary>
    private bool AnyAutoUploadCell()
    {
        foreach (var kind in UploadPolicyTable.Kinds)
        foreach (var artifact in UploadPolicyTable.Artifacts)
            if (_uploadPolicy[kind, artifact] == UploadPolicyState.Auto) return true;
        return false;
    }

    /// <summary>Fires the compat check once per session when auto-upload is enabled. Called at startup
    /// (InitUploadPolicy) and when a policy cell changes (SetUploadPolicy) so turning auto ON later also
    /// triggers it. No-op when auto-upload is off entirely (nothing to warn about) or already checked.</summary>
    private void MaybeCheckUploadCompat()
    {
        if (_compatChecked || !AnyAutoUploadCell()) return;
        _compatChecked = true;
        UploadCompat.FetchFireAndForget(
            LogUploader.ApiBase,
            OnUploadCompatFetched,
            msg => _services.Log.Info(msg));
    }

    // Thread-pool thread: prefs/log/volatile only, never uGUI. A null floor means "could not determine"
    // (offline / 404 / unparseable) — fail open, leaving _uploadBelowFloor false.
    private void OnUploadCompatFetched(string? minPluginVer, string? message)
    {
        if (minPluginVer is null) return;
        _uploadFloorMin = minPluginVer;
        if (!UploadCompat.IsBelowFloor(CurrentPluginVersion, minPluginVer)) return;
        _uploadBelowFloor = true;
        // The server owns the copy; fall back to a local string if it sent none.
        _compatNotice = string.IsNullOrEmpty(message)
            ? $"CombatMeter is out of date — update via the Stellar launcher to keep uploading your runs (min {minPluginVer})."
            : message;
        _services.Log.Warning(
            $"[CombatMeter.SP1] plugin {CurrentPluginVersion ?? "?"} is below the server upload floor {minPluginVer} " +
            "— uploads will be withheld until the plugin is updated.");
    }

    private void DrainCompatNotice()
    {
        var notice = _compatNotice;
        if (notice is null) return;
        _compatNotice = null;
        _services.Notifications.Notify(notice, NotificationKind.Warning);
    }

    /// <summary>Whether this build is below the server upload floor (drives the settings sticky line + the
    /// send withhold). Always false until the endpoint confirms it — fail-open.</summary>
    internal bool UploadBelowFloor => _uploadBelowFloor;

    /// <summary>The sticky warning shown next to the upload toggles while below the floor.</summary>
    internal string UploadFloorNoticeLine =>
        "⚠ This CombatMeter is out of date — update via the launcher to keep uploading" +
        (_uploadFloorMin is { } m ? $" (needs {m}+)." : ".");
}
