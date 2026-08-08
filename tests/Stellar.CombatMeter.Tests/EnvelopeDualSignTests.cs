using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class EnvelopeDualSignTests
{
    private static InstallKey NewKey()
    {
        var store = new Dictionary<string, string>();
        return InstallKey.LoadOrCreate(k => store.TryGetValue(k, out var v) ? v : null, (k, v) => store[k] = v);
    }

    [Fact]
    public void Writer_EmitsPubkeyAndInstallSig_ThatVerifyUnderTheSameCanonicalAsSig()
    {
        using var key = NewKey();
        var log = ReUploadTestFixtures.MinimalLog("log-1", "sea", 123456789L);
        // pubkey/installSig are NOT part of the canonical (same as masterScore), so the canonical is
        // identical before/after attaching them — exactly what the worker recomputes.
        var canonical = CanonicalPayload.Build(log);
        var signed = log with
        {
            Header = log.Header with
            {
                Uploader = log.Header.Uploader with { PubKey = key.PubKeySpkiBase64, InstallSig = key.SignInstall(canonical) }
            }
        };

        var json = CombatLogWriter.Write(signed);
        using var doc = JsonDocument.Parse(json);
        var uploader = doc.RootElement.GetProperty("header").GetProperty("uploader");
        var pubkey = uploader.GetProperty("pubkey").GetString()!;
        var installSig = uploader.GetProperty("installSig").GetString()!;

        using var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubkey), out _);
        Assert.True(pub.VerifyData(Encoding.UTF8.GetBytes(canonical), Convert.FromBase64String(installSig), HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Writer_OmitsInstallFields_WhenNoInstallKey()
    {
        var log = ReUploadTestFixtures.MinimalLog("log-2", "sea", 987654321L); // uploader has no PubKey
        var json = CombatLogWriter.Write(log);
        Assert.DoesNotContain("\"pubkey\"", json);
        Assert.DoesNotContain("\"installSig\"", json);
    }
}
