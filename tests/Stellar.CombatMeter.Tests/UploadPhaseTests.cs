using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// <see cref="UploadPhase"/> is persisted as its INT value (HistoryStore.UploadState "up"), so the values
/// are APPEND-ONLY. Reordering or reusing one silently relabels every already-saved history row.
/// </summary>
public class UploadPhaseTests
{
    // PINNED: the wire numbers. A reorder would make old sidecars read as the wrong phase.
    [Theory]
    [InlineData(0, "Idle")]
    [InlineData(1, "InFlight")]
    [InlineData(2, "Done")]
    [InlineData(3, "Failed")]
    [InlineData(4, "Skipped")]
    public void PersistedIntValuesAreStable(int wire, string name)
        => Assert.Equal(name, ((UploadPhase)wire).ToString());

    // Skipped was appended LAST on purpose: an older build casting an unknown int still lands outside
    // its label switch and falls through to the neutral default, rather than being mistaken for Failed.
    [Fact]
    public void SkippedIsDistinctFromFailed()
    {
        Assert.NotEqual(UploadPhase.Failed, UploadPhase.Skipped);
        Assert.True((int)UploadPhase.Skipped > (int)UploadPhase.Failed);
    }
}
