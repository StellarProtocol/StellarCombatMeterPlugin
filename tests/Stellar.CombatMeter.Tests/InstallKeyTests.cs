using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Stellar.CombatMeter.LogUpload;
using Xunit;

public class InstallKeyTests
{
    private static (Func<string, string?>, Action<string, string>) FakePrefs(Dictionary<string, string> d)
        => (k => d.TryGetValue(k, out var v) ? v : null, (k, v) => d[k] = v);

    [Fact]
    public void GeneratesOnce_StablePubKeyAcrossLoads()
    {
        var store = new Dictionary<string, string>();
        var (get, set) = FakePrefs(store);
        using var a = InstallKey.LoadOrCreate(get, set);
        using var b = InstallKey.LoadOrCreate(get, set); // second load reuses the stored key
        Assert.False(string.IsNullOrEmpty(a.PubKeySpkiBase64));
        Assert.Equal(a.PubKeySpkiBase64, b.PubKeySpkiBase64);
    }

    [Fact]
    public void SignInstall_VerifiesUnderItsOwnPubKey()
    {
        var store = new Dictionary<string, string>();
        var (get, set) = FakePrefs(store);
        using var key = InstallKey.LoadOrCreate(get, set);
        var payload = "claim|123|K7-42QX|nonce-abc";
        var sig = Convert.FromBase64String(key.SignInstall(payload));

        using var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PubKeySpkiBase64), out _);
        Assert.True(pub.VerifyData(Encoding.UTF8.GetBytes(payload), sig, HashAlgorithmName.SHA256));
    }
}
