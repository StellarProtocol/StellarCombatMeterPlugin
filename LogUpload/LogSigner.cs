// SP1: ECDSA P-256 signing of the canonical upload payload.
// Uses a managed BouncyCastle signer (see Es256) so signing works under Wine/Proton, where the
// OS/CNG-backed System.Security.Cryptography.ECDsa throws NTE_NOT_SUPPORTED (0x80090029).

using System;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Signs a StellarLogs upload payload with ECDSA P-256 / SHA-256.
/// The private key is loaded once from a base64-PKCS#8 DER string (env-var / config).
/// </summary>
internal sealed class LogSigner : IDisposable
{
    private readonly ECPrivateKeyParameters _priv;

    /// <summary>
    /// Initialises the signer from a base64-encoded PKCS#8 private key.
    /// </summary>
    /// <param name="pkcs8Base64">
    /// Base-64 encoded PKCS#8 DER bytes of the ECDSA P-256 private key.
    /// Obtain from the Stellar service key-management tooling; never hard-code a real secret.
    /// </param>
    internal LogSigner(string pkcs8Base64)
        => _priv = Es256.ImportPkcs8(Convert.FromBase64String(pkcs8Base64));

    /// <summary>
    /// Returns the base64-encoded IEEE P1363 signature over <paramref name="payload"/>.
    /// Canonical payload format (matches verify.ts canonicalPayload):
    /// <c>{logId}|{levelUuid}|{localUid}|{startMs}|{endMs}|{nonce}|{sha256hex(JSON.stringify(events))}</c>
    /// </summary>
    internal string Sign(string payload)
        => Convert.ToBase64String(Es256.SignP1363(_priv, Encoding.UTF8.GetBytes(payload)));

    public void Dispose() { /* BouncyCastle key parameters hold no unmanaged resources */ }
}
