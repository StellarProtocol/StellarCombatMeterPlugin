using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

// Upload API base override (owner 2026-08-14) — lets a locally-built TEST plugin point every upload
// artifact at STAGING without editing source. Config key "uploadApiBase" in the "combatmeter" section of
// <game_mini>/stellar/plugins/stellar.combatmeter.config.json; empty/absent (the default) = production.
//
// Scope: resolving it here sets the ONE base that summary, chunks, positions, supplement, portraits AND
// the content-kind fetch all build from — a build must never split its uploads across prod + staging,
// which corrupts BOTH datasets and is worse than pointing wholly at either. The account claim base is a
// separate knob ("stellarlogs.claimApiBase", see Plugin.Account) and is NOT affected.
public sealed partial class Plugin
{
    private const string PrefUploadApiBase = "uploadApiBase";

    /// <summary>Resolves the configured upload base and applies it to <see cref="LogUploader"/>. Called
    /// ONCE from the constructor, immediately after <c>_prefs</c> is bound and before anything can upload
    /// or fetch. Logs at Info when overridden — UNGATED by diagnostics on purpose: "my uploads are going
    /// somewhere other than production" must be visible in any log the owner or a user sends us, not only
    /// in a diagnostics-enabled one. Silence means production.</summary>
    private void InitUploadApiBase()
    {
        var configured = _prefs.Get(PrefUploadApiBase, "");
        LogUploader.SetApiBase(configured);

        if (LogUploader.IsApiBaseOverridden)
        {
            _services.Log.Info(
                $"[CombatMeter] upload API base OVERRIDDEN → {LogUploader.ApiBase} " +
                $"(config '{PrefUploadApiBase}') — uploads are NOT going to production.");
        }
        else if (!string.IsNullOrWhiteSpace(configured))
        {
            // Non-empty but rejected by NormalizeApiBase (not https:// or scheme-only). Ignored rather
            // than applied — a malformed base would silently swallow every upload.
            _services.Log.Warning(
                $"[CombatMeter] '{PrefUploadApiBase}' value \"{configured}\" ignored " +
                $"(must be an https:// origin) — uploading to {LogUploader.ApiBase}.");
        }
    }
}
