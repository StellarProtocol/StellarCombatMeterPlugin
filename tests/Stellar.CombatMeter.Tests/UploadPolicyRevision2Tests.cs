using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Spec § 8 (owner rulings 2026-07-29): a fifth content kind, `other` defaulting to OFF, and fail-open
/// when no taxonomy is cached. Internal types stay in method BODIES only (xUnit needs public methods and
/// a public method may not take an internal parameter type — CS0051).
/// </summary>
public class UploadPolicyRevision2Tests
{
    private static Plugin.EncounterHistoryEntry Entry(string scene) => new() { SceneName = scene };

    // A map with one id per kind, mirroring the worker payload's shape.
    private static ContentKindMap Map()
    {
        Assert.True(ContentKindMap.TryParse(
            "{\"version\":1,\"kinds\":{\"dungeon\":[1151],\"raid\":[13001],\"worldboss\":[7152]," +
            "\"vault\":[32130],\"other\":[]}}", out var m));
        return m;
    }

    // ---- § 8.1 the vault kind ---------------------------------------------

    [Fact]
    public void VaultIsAFifthKind_WithTheWireKeyAndItsOwnLabel()
    {
        // The key must match FEED_KINDS in services/stellar-logs/src/worker/routes/site.ts.
        Assert.Equal("vault", UploadPolicy.KindKey(ContentKind.Vault));
        Assert.Equal("logUpload.policy.vault.stats", UploadPolicy.PrefKey(ContentKind.Vault, UploadArtifact.Stats));
        // i18n P1: Label returns a catalog key; en.json carries the "Stimen Vaults" spelling (owner wrote "Stiment").
        Assert.Equal("upload.kind.vault", UploadPolicy.Label(ContentKind.Vault));
    }

    [Fact]
    public void TheTableCoversFiveKinds_TenCells()
    {
        Assert.Equal(5, UploadPolicyTable.Kinds.Length);
        Assert.Contains(ContentKind.Vault, UploadPolicyTable.Kinds);
        // Every (kind, artifact) pair must still address a distinct, independent cell.
        var t = UploadPolicyTable.AllAuto();
        t[ContentKind.Vault, UploadArtifact.Stats] = UploadPolicyState.Off;
        Assert.Equal(UploadPolicyState.Off, t[ContentKind.Vault, UploadArtifact.Stats]);
        Assert.Equal(UploadPolicyState.Auto, t[ContentKind.Vault, UploadArtifact.Replay]);
        Assert.Equal(UploadPolicyState.Auto, t[ContentKind.Other, UploadArtifact.Stats]);
    }

    [Fact]
    public void ContentKindMap_ClassifiesVaultFromThePayload()
    {
        var m = Map();
        Assert.Equal(ContentKind.Vault, m.KindOf(32130));
        Assert.Equal(ContentKind.Dungeon, m.KindOf(1151));
        Assert.Equal(ContentKind.Other, m.KindOf(99999));
        Assert.Equal(new[] { 32130 }, m.Ids(ContentKind.Vault));
    }

    [Fact]
    public void ContentKindMap_VaultSurvivesThePrefsRoundTrip()
    {
        var m = Map();
        var revived = ContentKindMap.FromIds(
            m.Ids(ContentKind.Dungeon), m.Ids(ContentKind.Raid),
            m.Ids(ContentKind.WorldBoss), m.Ids(ContentKind.Vault));
        Assert.Equal(ContentKind.Vault, revived.KindOf(32130));
        Assert.False(revived.IsEmpty);
    }

    // ---- § 8.2 `other` defaults to OFF on both artifacts -------------------

    // A first-run install must not push unclassified content anywhere.
    //
    // HISTORY, so this is not "corrected" a third time: on 2026-07-29 this pin was briefly changed to
    // expect replay=auto, to honour the owner's "replays always keep except open field" ruling. That
    // misread the ruling — it is about STORING the replay, not uploading it. Storage is now
    // unconditional (capture never consults the policy; a withheld upload retains its record), so `off`
    // withholds only the SEND and loses nothing. Both cells belong off here.
    [Fact]
    public void Defaults_AreAllAuto_ExceptOtherWhichIsOffOnBothArtifacts()
    {
        var t = UploadPolicyTable.Defaults();
        foreach (var kind in UploadPolicyTable.Kinds)
        foreach (var artifact in UploadPolicyTable.Artifacts)
        {
            var expected = kind == ContentKind.Other ? UploadPolicyState.Off : UploadPolicyState.Auto;
            Assert.Equal(expected, t[kind, artifact]);
        }
    }

    [Fact]
    public void Migration_ForcesOtherOff_ForExistingInstallsToo()
    {
        // Owner: "yes" — the new default applies on upgrade, knowingly breaking § 2.2's "nothing shifts",
        // to stop the activity flood (Wondrous Tag, Guild Hall, Unstable Space) filling the site feed.
        //
        // RE-PINNED 2026-07-29: STATS only. This test also asserted replay Off, which contradicted the
        // owner's older and still-standing ruling (2026-07-01 replay-R1 § 1: only field / open world is
        // off, so everything instanced keeps its replay). It cost a real run — a 20-player Giant Golem
        // Crusade came back with no replay at all. Open field is already excluded structurally by
        // PrepareReplayDoc's `entry.LevelUuid == 0` guard, and the flood this default fights is a STATS
        // problem: replay docs never enter the feed. Replay therefore follows the legacy pref.
        foreach (var (auto, replay) in new[] { (true, true), (false, true), (true, false), (false, false) })
        {
            var t = UploadPolicyTable.Migrate(auto, replay);
            Assert.Equal(UploadPolicyState.Off, t[ContentKind.Other, UploadArtifact.Stats]);
            Assert.Equal(UploadPolicyState.Off, t[ContentKind.Other, UploadArtifact.Replay]);
        }
    }

    [Fact]
    public void Migration_StillSeedsTheOtherFourKindsFromTheLegacyPrefs()
    {
        var t = UploadPolicyTable.Migrate(legacyAutoUpload: false, legacyUploadReplay: false);
        foreach (var kind in UploadPolicyTable.Kinds)
        {
            if (kind == ContentKind.Other) continue;
            Assert.Equal(UploadPolicyState.Manual, t[kind, UploadArtifact.Stats]);
            Assert.Equal(UploadPolicyState.Off, t[kind, UploadArtifact.Replay]);
        }
    }

    // ---- § 8.3 fail-open when no taxonomy is cached -----------------------

    [Fact]
    public void EmptyMap_FailsOPEN_SoAFreshInstallStillUploads()
    {
        // THE point: with other=off, resolving unknown content to `other` would upload NOTHING — not even
        // dungeons and raids. "No taxonomy yet" is a distinct state from "known unlisted content".
        var policy = UploadPolicyTable.Defaults();
        Assert.Equal(UploadPolicyState.Auto,
            Plugin.EffectivePolicy(ContentKindMap.Empty, policy, Entry("1151"), UploadArtifact.Stats));
        Assert.Equal(UploadPolicyState.Auto,
            Plugin.EffectivePolicy(ContentKindMap.Empty, policy, Entry("11002"), UploadArtifact.Replay));
    }

    [Fact]
    public void OnceAMapExists_TheOtherCellApplies()
    {
        var policy = UploadPolicyTable.Defaults();
        // 11002 (Wondrous Tag) is not in the map ⇒ genuinely `other` ⇒ off.
        Assert.Equal(UploadPolicyState.Off,
            Plugin.EffectivePolicy(Map(), policy, Entry("11002"), UploadArtifact.Stats));
        // A classified kind keeps its own cell.
        Assert.Equal(UploadPolicyState.Auto,
            Plugin.EffectivePolicy(Map(), policy, Entry("13001"), UploadArtifact.Stats));
        Assert.Equal(UploadPolicyState.Auto,
            Plugin.EffectivePolicy(Map(), policy, Entry("32130"), UploadArtifact.Stats));
    }

    [Fact]
    public void FailOpenDoesNotOverrideADeliberateSetting_OnceTheMapIsKnown()
    {
        // Fail-open is about UNRESOLVED content only. With a map present, an explicit off is honoured.
        var policy = UploadPolicyTable.Defaults();
        policy[ContentKind.Dungeon, UploadArtifact.Stats] = UploadPolicyState.Off;
        Assert.Equal(UploadPolicyState.Off,
            Plugin.EffectivePolicy(Map(), policy, Entry("1151"), UploadArtifact.Stats));
    }
}
