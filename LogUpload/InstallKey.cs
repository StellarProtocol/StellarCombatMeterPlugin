using System;
using System.Security.Cryptography;
using System.Text;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Per-install ECDSA P-256 identity. The private key is generated locally and stored in prefs
/// (never transmitted, never shared) so there is no shared secret to leak. Signs the SAME canonical
/// payloads as the shared <see cref="LogSigner"/>, in IEEE-P1363 base64 (verify.ts-compatible).
/// </summary>
internal sealed class InstallKey : IDisposable
{
    private const string PrefInstallKey = "logUpload.installKey"; // base64 PKCS#8 private key
    private readonly ECDsa _ecdsa;

    private InstallKey(ECDsa ecdsa) => _ecdsa = ecdsa;

    /// <summary>Loads the stored per-install key, or generates + persists one on first use (or if the
    /// stored value is corrupt). Prefs access is injected so this is unit-testable off-game.</summary>
    internal static InstallKey LoadOrCreate(Func<string, string?> getPref, Action<string, string> setPref)
    {
        var stored = getPref(PrefInstallKey);
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (!string.IsNullOrEmpty(stored))
        {
            try
            {
                ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(stored!), out _);
                return new InstallKey(ecdsa);
            }
            catch { /* corrupt pref → regenerate below */ }
        }
        setPref(PrefInstallKey, Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()));
        return new InstallKey(ecdsa);
    }

    /// <summary>SPKI (SubjectPublicKeyInfo) base64 — what verify.ts <c>importPublicKey("spki", …)</c> expects.</summary>
    internal string PubKeySpkiBase64 => Convert.ToBase64String(_ecdsa.ExportSubjectPublicKeyInfo());

    /// <summary>Base64 IEEE-P1363 signature over <paramref name="payload"/> (P-256 / SHA-256).</summary>
    internal string SignInstall(string payload)
        => Convert.ToBase64String(_ecdsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));

    public void Dispose() => _ecdsa.Dispose();
}
