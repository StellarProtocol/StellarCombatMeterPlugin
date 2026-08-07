using System;
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

    private IWindowControl _accountWindow = null!;
    private string _linkCode = string.Empty;
    private string _linkStatus = string.Empty;

    private IWindowControl BuildAndRegisterAccount()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.account",
                Title:       "Link to site",
                DefaultRect: new WindowRect(900f, 120f, 360f, 250f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, HideUntilInWorld = true, Closable = true, Draggable = true },
            BuildAccountRoot(),
            OnClose: () => _accountWindow.SetVisible(false)));

    private void ToggleAccount() => _accountWindow.SetVisible(!_accountWindow.IsShown);

    private HudElement BuildAccountRoot()
        => new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Link this character to your StellarLogs account.", Emphasis: true),
            new TextElement(() => "On the site: account menu → Claim a character. Enter its code here.", MutedCol),
            new SeparatorElement(),
            new RowElement(new HudElement[]
            {
                new TextElement(() => "Code", MutedCol, Width: 54f),
                new InputElement(() => _linkCode, OnSubmitLinkCode, 180f, OnChange: s => _linkCode = s),
            }, Gap: 8f),
            new RowElement(new HudElement[]
            {
                new SpacerElement(Width: 0f),
                new ButtonElement(() => "Link", SubmitLinkCode),
            }, Gap: 8f),
            new TextElement(() => _linkStatus, MutedCol),
        }, Gap: 6f);

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
        if (code.Length == 0) { SetLinkStatus("Enter the code shown on the site."); return; }
        var localUid = LocalUidForUpload();
        if (localUid <= 0) { SetLinkStatus("Log into a character first."); return; }

        SetLinkStatus("Linking…");
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
            using var resp = await AccountHttp.PostAsync(LogUploader.ApiBase + "/claim/submit", content).ConfigureAwait(false);
            SetLinkStatus(resp.StatusCode switch
            {
                HttpStatusCode.OK        => "✓ Linked! Refresh the site.",
                HttpStatusCode.Conflict  => "This character is owned by another account (dispute on the site).",
                HttpStatusCode.Gone      => "Code expired — mint a new one on the site.",
                HttpStatusCode.Unauthorized => "Signature rejected — update the plugin.",
                _                        => $"Link failed ({(int)resp.StatusCode}). Try a fresh code.",
            });
        }
        catch (Exception ex)
        {
            SetLinkStatus("Link failed (network). Try again.");
            _services.Log.Warning($"[CombatMeter] link-to-site POST failed: {ex.Message}");
        }
    }

    // Reference-assignment is atomic in .NET (no torn read of the string field); MarkDirty is a
    // best-effort nudge (wrapped — the render model may only refresh on interaction, which is fine).
    private void SetLinkStatus(string s)
    {
        _linkStatus = s;
        try { _accountWindow.MarkDirty(); } catch { /* refresh is best-effort */ }
    }
}
