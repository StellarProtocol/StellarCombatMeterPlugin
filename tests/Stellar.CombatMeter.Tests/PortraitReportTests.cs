// Task 12: portrait batch body carries the install's region as a top-level field, OUTSIDE the
// signed entries array — CanonicalPayload hashes entriesJson only and takes no region parameter,
// so this field cannot affect the signature (see the cross-repo invariant comment in
// PortraitReport.cs).

using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class PortraitReportTests
{
    [Fact]
    public void PortraitBody_CarriesRegion_OutsideSignedEntries()
    {
        var body = PortraitReport.WriteBody(7, "nonce1", "sig1", "[]", "jp");
        Assert.Contains("\"region\":\"jp\"", body);
        // Region must ride OUTSIDE the signed entries array (the canonical payload
        // hashes entriesJson only, and CanonicalPayload takes no region parameter —
        // the cross-repo invariant comment in PortraitReport.cs stays true).
        Assert.Contains("\"entries\":[]", body);
    }
}
