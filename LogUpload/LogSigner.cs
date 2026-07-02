// SP1: ECDSA P-256 signing of the canonical upload payload.
// Uses System.Security.Cryptography (available in .NET 6 / netstandard2.1).

using System;
using System.Security.Cryptography;
using System.Text;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Signs a StellarLogs upload payload with ECDSA P-256.
/// The private key is loaded once from a base64-PKCS#8 DER string (env-var / config).
/// </summary>
internal sealed class LogSigner : IDisposable
{
    private readonly ECDsa _ecdsa;

    /// <summary>
    /// Initialises the signer from a base64-encoded PKCS#8 private key.
    /// </summary>
    /// <param name="pkcs8Base64">
    /// Base-64 encoded PKCS#8 DER bytes of the ECDSA P-256 private key.
    /// Obtain from the Stellar service key-management tooling; never hard-code a real secret.
    /// </param>
    internal LogSigner(string pkcs8Base64)
    {
        var raw = Convert.FromBase64String(pkcs8Base64);
        _ecdsa = ECDsa.Create();
        _ecdsa.ImportPkcs8PrivateKey(raw, out _);
    }

    /// <summary>
    /// Returns the base64-encoded IEEE P1363 signature over <paramref name="payload"/>.
    /// Canonical payload format (matches verify.ts canonicalPayload):
    /// <c>{logId}|{levelUuid}|{localUid}|{startMs}|{endMs}|{nonce}|{sha256hex(JSON.stringify(events))}</c>
    /// </summary>
    internal string Sign(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        // ECDSA P-256 with SHA-256, IEEE P1363 signature format (raw r||s, 64 bytes).
        var sig = _ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(sig);
    }

    public void Dispose() => _ecdsa.Dispose();
}
