using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

// Account → "Link to site": enter the code minted by the StellarLogs claim modal; the plugin signs a
// claim for the LOGGED-IN character (per-install key + shared key) and POSTs it to /claim/submit. This
// is the plugin half of the account-claim flow (spec 2026-08-07). It touches NO archive/stage/upload
// code — it only reads the local uid + the signing keys and sends one new signed message.
public sealed partial class Plugin
{
    private static readonly HttpClient AccountHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    // The claim endpoint base. Defaults to prod (LogUploader.DefaultApiBase); overridable via the
    // "stellarlogs.claimApiBase" config key so the owner can point the claim flow at STAGING for
    // in-game verification before the account backend ships to prod. Contained to the claim path —
    // the protected upload base (LogUploader/ChunkUploader/PositionUploader) is unchanged.
    //
    // DELIBERATELY pinned to DefaultApiBase, not the effective LogUploader.ApiBase: the claim base is a
    // SEPARATE knob with its own key. A staging-pointed UPLOAD build must not silently drag the account
    // claim flow (which mints real, account-linking credentials) onto staging as a side effect — point
    // it there explicitly via claimApiBase or not at all.
    private string ClaimApiBase()
    {
        var v = _prefs.Get("stellarlogs.claimApiBase", "");
        return string.IsNullOrWhiteSpace(v) ? LogUploader.DefaultApiBase : v!.TrimEnd('/');
    }

    private string _linkCode = string.Empty;
    private string _linkStatus = string.Empty;

    // 0 = progress / prompt (muted), 1 = linked (green), 2 = error (red) — the status line's colour.
    private volatile int _linkStatusKind;
    private volatile bool _linkedThisSession;

    // Settings › Account (approved redesign §3): STELLARLOGS ACCOUNT panel — muted explainer, code input + Primary
    // "Link", status line. Re-localizes for free: the settings window is rebuilt on LanguageChanged
    // (Plugin.Localization.cs).
    private const float LinkLabelW = 48f;

    private HudElement BuildAccountTab() => SettingsTabColumn(
        SectionPanel(() => _loc.T("cfg.account.title"), () => _linkedThisSession ? _loc.T("cfg.account.linked") : "",
            new TextElement(() => _loc.T("cfg.account.explainer"), Color: () => MutedColor()) { MaxLines = 2 },
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, SettingRowH),
                new InputElement(() => _linkCode, OnSubmitLinkCode, SettingsFullInnerW - LinkLabelW - 2f * PillPadX - 2f * 8f,
                    OnChange: s => _linkCode = s) { SingleLine = true, Placeholder = () => _loc.T("cfg.account.codePlaceholder") },
                PrimaryButton(() => _loc.T("account.link"), SubmitLinkCode, LinkLabelW),
            }, Gap: 8f),
            FixedLine(new TextElement(() => _linkStatus, Color: () => _linkStatusKind switch
                {
                    1 => SuccessText(),
                    2 => ErrorText(),
                    _ => MutedColor(),
                }, NoWrap: true) { Ellipsize = true }, 20f)));

    private void OnSubmitLinkCode(string val)
    {
        _linkCode = val ?? string.Empty;
        SubmitLinkCode();
    }

    // Main-thread (button/Enter): validate, sign the claim, fire the POST. The claim's REAL outcome is
    // confirmed on the site (its /claim/status poll flips to "claimed" and reloads), so the plugin-side
    // status line is best-effort feedback — no fragile cross-thread UI refresh is required.
    private void SubmitLinkCode()
    {
        var code = (_linkCode ?? string.Empty).Trim();
        if (code.Length == 0) { SetLinkStatus(_loc.T("account.status.enterCode")); return; }
        var localUid = LocalUidForUpload();
        if (localUid <= 0) { SetLinkStatus(_loc.T("account.status.loginFirst"), 2); return; }

        SetLinkStatus(_loc.T("account.status.linking"));
        var nonce = Guid.NewGuid().ToString("N");
        var canonical = CanonicalPayload.BuildClaim(localUid, code, nonce);
        string pubkey = string.Empty, installSig = string.Empty, sig = string.Empty;
        try { var ik = InstallKeyInstance; pubkey = ik.PubKeySpkiBase64; installSig = ik.SignInstall(canonical); }
        catch (Exception ex) { _services.Log.Warning($"[CombatMeter] link install-sign failed: {ex.Message}"); }
        try { var sk = SignerKey; if (!string.IsNullOrEmpty(sk)) { using var s = new LogSigner(sk!); sig = s.Sign(canonical); } }
        catch (Exception ex) { _services.Log.Warning($"[CombatMeter] link shared-sign failed: {ex.Message}"); }

        var body = JsonSerializer.Serialize(new { localUid, code, nonce, sig, pubkey, installSig });
        _ = SubmitClaimAsync(body);
    }

    private async Task SubmitClaimAsync(string body)
    {
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await AccountHttp.PostAsync(ClaimApiBase() + "/claim/submit", content).ConfigureAwait(false);
            bool ok = resp.StatusCode == HttpStatusCode.OK;
            if (ok) _linkedThisSession = true;
            SetLinkStatus(resp.StatusCode switch
            {
                HttpStatusCode.OK        => _loc.T("account.status.linked"),
                HttpStatusCode.Conflict  => _loc.T("account.err.conflict"),
                HttpStatusCode.Gone      => _loc.T("account.err.gone"),
                HttpStatusCode.Unauthorized => _loc.T("account.err.unauthorized"),
                _                        => _loc.TFormat("account.err.failed", (int)resp.StatusCode),
            }, ok ? 1 : 2);
        }
        catch (Exception ex)
        {
            SetLinkStatus(_loc.T("account.err.network"), 2);
            _services.Log.Warning($"[CombatMeter] link-to-site POST failed: {ex.Message}");
        }
    }

    // Reference-assignment is atomic in .NET (no torn read of the string field); MarkDirty is a
    // best-effort nudge (wrapped — the render model may only refresh on interaction, which is fine).
    private void SetLinkStatus(string s, int kind = 0)
    {
        _linkStatusKind = kind;
        _linkStatus = s;
        try { _settingsWindow.MarkDirty(); } catch { /* refresh is best-effort */ }
    }
}
