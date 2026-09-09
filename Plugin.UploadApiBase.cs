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
//
// 2.7.3 (owner "yes" 2026-09-06): on a TESTING-channel build (Plugin.UploadTarget.cs), when the user has
// NEVER chosen an upload target, the configured base defaults to the testing (dev) server instead of
// staying empty/production — production rejects every 2.7.x upload outright (schema predates the
// buff/sheet flags), so an un-redirected testing build silently uploads nothing. The decision itself is
// the pure Plugin.ResolveInitialUploadTarget; this file only wires it in before LogUploader.SetApiBase.
//
// 2.9.1 (owner 2026-09-09: "make sure combatmeter will point to production logs site"): the mirror rule.
// The 2.7.3 default above PERSISTS the dev base into config, so an install that ever ran a testing build
// carries "uploadApiBase = <dev>" forever — and a STABLE build has no Upload-target toggle to undo it
// with. A stable build therefore CLEARS a configured base that is the testing one (normalised compare:
// whitespace / letter case / trailing slash tolerant) and uploads to production, logging that it did so.
// Any OTHER non-empty value is still honoured untouched — that is the owner-only staging override this
// file exists for. STABLE ⇒ PRODUCTION unless the owner explicitly configured some third base.
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

        // Channel-dependent repair of the configured base (both directions) — BEFORE SetApiBase, so a
        // fresh testing-channel install uploads somewhere real from its very first archive AND a stable
        // build never inherits a leftover testing target from a build the user ran earlier.
        var isTestingBuild = _isTestingBuildGate();
        var (resolvedConfigured, writeDefault) = ResolveInitialUploadTarget(
            isTestingBuild, _prefs.Get(PrefUploadTargetChosen, false), configured ?? "");
        configured = resolvedConfigured;
        if (writeDefault)
        {
            _prefs.Set(PrefUploadApiBase, configured);
            _prefs.Set(PrefUploadTargetChosen, true);
            _prefs.Save();
            // ResolveInitialUploadTarget only ever asks for a write for ONE reason per channel: apply
            // the dev default (testing) or clear a leftover one (stable) — see its own doc comment.
            _services.Log.Info(isTestingBuild
                ? "[CombatMeter] testing-channel build: upload target defaulted to TESTING (dev)"
                : "[CombatMeter] stable build: testing upload target found in config — cleared; uploading to production.");
        }

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
