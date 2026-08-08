using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Pins the wire contract the managed (Wine-safe) signer MUST satisfy so the worker keeps verifying it:
// P-256 / SHA-256, IEEE-P1363 raw r||s (64-byte) signature, SPKI public key, PKCS#8 private key —
// and backward compatibility with keys the OLD System.Security.Cryptography signer already stored.
// (The Wine crypto failure itself can't be reproduced on Linux .NET/OpenSSL, so these tests guard the
// FORMAT; the runtime fix is confirmed in-game.)
public class SignerFormatCompatTests
{
    private static (Func<string, string?>, Action<string, string>) FakePrefs(Dictionary<string, string> d)
        => (k => d.TryGetValue(k, out var v) ? v : null, (k, v) => d[k] = v);

    // The worker verifies with dsaEncoding "ieee-p1363": raw r||s, 64 bytes for P-256. A DER signature
    // (BouncyCastle's default output) is variable-length and would be REJECTED — so this must be 64.
    [Fact]
    public void SignInstall_ProducesRawP1363_64ByteSignature()
    {
        var (get, set) = FakePrefs(new Dictionary<string, string>());
        using var key = InstallKey.LoadOrCreate(get, set);
        var sig = Convert.FromBase64String(key.SignInstall("claim|1|C0DE|nonce"));
        Assert.Equal(64, sig.Length);
    }

    [Fact]
    public void LogSigner_ProducesRawP1363_64ByteSignature()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = new LogSigner(Convert.ToBase64String(ec.ExportPkcs8PrivateKey()));
        var sig = Convert.FromBase64String(signer.Sign("log|lvl|1|0|0|nonce|hash"));
        Assert.Equal(64, sig.Length);
    }

    // Backward compat: a key previously stored by the OLD .NET signer (base64 PKCS#8) must still load
    // and sign under the managed signer, verifying under the SAME key's .NET-derived pubkey, and export
    // the SAME SPKI (so the server's learned per-install owner keyed by pubkey string stays consistent).
    [Fact]
    public void InstallKey_LoadsDotNetStoredPkcs8_SameIdentity_AndVerifies()
    {
        using var dotnet = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var stored = Convert.ToBase64String(dotnet.ExportPkcs8PrivateKey());
        var (get, set) = FakePrefs(new Dictionary<string, string> { ["logUpload.installKey"] = stored });

        using var key = InstallKey.LoadOrCreate(get, set);
        var payload = "claim|9|C0DE|nonce-xyz";
        var sig = Convert.FromBase64String(key.SignInstall(payload));

        Assert.True(dotnet.VerifyData(Encoding.UTF8.GetBytes(payload), sig, HashAlgorithmName.SHA256));
        Assert.Equal(Convert.ToBase64String(dotnet.ExportSubjectPublicKeyInfo()), key.PubKeySpkiBase64);
    }

    // The shared-key LogSigner must accept a .NET-exported PKCS#8 and sign such that the matching .NET
    // pubkey verifies it (the shared signer key is provisioned as base64 PKCS#8 from service tooling).
    [Fact]
    public void LogSigner_SignsWithDotNetPkcs8_VerifiesUnderThatKeysPubKey()
    {
        using var dotnet = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = new LogSigner(Convert.ToBase64String(dotnet.ExportPkcs8PrivateKey()));
        var payload = "log|lvl|7|1|2|nonce|deadbeef";
        var sig = Convert.FromBase64String(signer.Sign(payload));
        Assert.True(dotnet.VerifyData(Encoding.UTF8.GetBytes(payload), sig, HashAlgorithmName.SHA256));
    }

    // A freshly generated install key round-trips: the persisted PKCS#8 re-loads to the SAME identity.
    [Fact]
    public void InstallKey_GeneratedKey_PersistsAndReloadsSameIdentity()
    {
        var store = new Dictionary<string, string>();
        var (get, set) = FakePrefs(store);
        string pub1, pub2;
        using (var a = InstallKey.LoadOrCreate(get, set)) pub1 = a.PubKeySpkiBase64;
        using (var b = InstallKey.LoadOrCreate(get, set)) pub2 = b.PubKeySpkiBase64;
        Assert.Equal(pub1, pub2);
        // The stored key must be importable by the .NET verifier too (standard PKCS#8).
        using var check = ECDsa.Create();
        check.ImportPkcs8PrivateKey(Convert.FromBase64String(store["logUpload.installKey"]), out _);
    }
}
