using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

// Upload compatibility floor — client side (owner ask 2026-08-16). When auto-upload is on, ask the
// server for the min accepted plugin version (GET /api/upload/compat) and, if this build is below it,
// tell the player to update BEFORE a run-end upload silently 426s.
//
// Surfaces (owner 2026-08-16 "it should stay until user close it"): a PERSISTENT, dismissible banner at
// the top of the meter window + a sticky line in the upload settings. NOT the framework toast — that is
// transient and undismissable by contract (INotifications: "auto-disappear ... no dismissal handle").
// The send is also WITHHELD (Plugin.LogUpload.cs) rather than fired-and-failed.
//
// FAIL-OPEN throughout — a 404 (route not deployed yet), an offline client, or an unreadable own version
// never nags or withholds. SCOPE: touches only the upload SEND decision and its notices — never the
// archive engine, verdict, run-id, boss detection, or capture (capture is always-on; this gates a send).
public sealed partial class Plugin
{
    // Written on the fetch's thread-pool callback, read on the Unity main thread (send gate, banner,
    // settings draw) — volatile so the read is never stale/torn.
    private volatile bool _uploadBelowFloor;
    private volatile string? _uploadFloorMin;
    private volatile string? _compatMessage;
    // Main-thread only: dismiss is a per-session UI action (a still-outdated build re-nags next launch);
    // the fetch fires at most once per session (InitUploadPolicy/SetUploadPolicy run on the main thread).
    private bool _compatDismissed;
    private bool _compatChecked;

    /// <summary>True when the player has at least one content cell set to <c>auto</c> — auto-upload is on
    /// for something, the owner's trigger for the compat check ("if that player turn on the auto upload").</summary>
    private bool AnyAutoUploadCell()
    {
        foreach (var kind in UploadPolicyTable.Kinds)
        foreach (var artifact in UploadPolicyTable.Artifacts)
            if (_uploadPolicy[kind, artifact] == UploadPolicyState.Auto) return true;
        return false;
    }

    /// <summary>Fires the compat check once per session when auto-upload is enabled. Called at startup
    /// (InitUploadPolicy) and when a policy cell changes (SetUploadPolicy) so turning auto ON later also
    /// triggers it. No-op when auto-upload is off entirely or already checked.</summary>
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
    // (offline / 404 / unparseable) — fail open, leaving _uploadBelowFloor false. The banner reads the
    // volatile flags directly on the main thread, so no main-thread drain is needed.
    private void OnUploadCompatFetched(string? minPluginVer, string? message)
    {
        if (minPluginVer is null) return;
        _uploadFloorMin = minPluginVer;
        if (!UploadCompat.IsBelowFloor(CurrentPluginVersion, minPluginVer)) return;
        _compatMessage = string.IsNullOrEmpty(message)
            ? $"Your CombatMeter is out of date — update via the Stellar launcher to keep uploading your runs (min {minPluginVer})."
            : message;
        _uploadBelowFloor = true;   // set LAST: the banner reads this as the ready flag
        _services.Log.Warning(
            $"[CombatMeter.SP1] plugin {CurrentPluginVersion ?? "?"} is below the server upload floor {minPluginVer} " +
            "— uploads will be withheld until the plugin is updated.");
    }

    /// <summary>Below the server floor AND the player hasn't dismissed the banner this session.</summary>
    internal bool ShowCompatBanner => _uploadBelowFloor && !_compatDismissed;

    /// <summary>Whether this build is below the server upload floor (drives the settings line + the withhold).
    /// Always false until the endpoint confirms it — fail-open.</summary>
    internal bool UploadBelowFloor => _uploadBelowFloor;

    /// <summary>Dismisses the meter banner for this session (the × button). A still-outdated build re-nags
    /// on the next launch, and the sticky settings line + the withhold stay regardless.</summary>
    internal void DismissCompatNotice() => _compatDismissed = true;

    /// <summary>The warning text for the meter banner + the settings line (server-owned copy, ⚠-prefixed).</summary>
    internal string UploadFloorNoticeLine =>
        "⚠ " + (_compatMessage
            ?? "This CombatMeter is out of date — update via the launcher to keep uploading"
               + (_uploadFloorMin is { } m ? $" (needs {m}+)." : "."));
}
