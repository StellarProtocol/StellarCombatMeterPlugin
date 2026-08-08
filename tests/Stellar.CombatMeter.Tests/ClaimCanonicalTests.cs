using Stellar.CombatMeter.LogUpload;
using Xunit;

public class ClaimCanonicalTests
{
    [Fact]
    public void BuildClaim_MatchesWorkerCanonicalClaimPayload()
    {
        // verify.ts canonicalClaimPayload: `claim|${localUid}|${code}|${nonce}` — must byte-match.
        Assert.Equal("claim|1248014|K7-42QX|abc", CanonicalPayload.BuildClaim(1248014, "K7-42QX", "abc"));
    }
}
