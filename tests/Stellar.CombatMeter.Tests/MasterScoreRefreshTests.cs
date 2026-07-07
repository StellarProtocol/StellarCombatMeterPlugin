using System.Threading.Tasks;
using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class MasterScoreRefreshTests
{
    [Theory]
    [InlineData(700, 0, true)]   // masterModeScore>0 -> master run
    [InlineData(0, 6, true)]     // difficultyLevel>0 -> master run
    [InlineData(0, 0, false)]    // neither -> skip
    public void IsMasterModeRun_gates_on_master_signals(int mms, int diffLevel, bool expected)
        => Assert.Equal(expected, MasterScoreRefresh.IsMasterModeRun(mms, diffLevel));

    [Fact]
    public async Task Poll_returns_first_positive_snapshot_within_budget()
    {
        int calls = 0;
        // returns 0 twice (not yet credited), then 686
        int Read() => ++calls >= 3 ? 686 : 0;
        var score = await MasterScoreRefresh.PollForScore(Read, attempts: 4, delayMs: 1);
        Assert.Equal(686, score);
    }

    [Fact]
    public async Task Poll_returns_zero_when_never_populated()
    {
        var score = await MasterScoreRefresh.PollForScore(() => 0, attempts: 3, delayMs: 1);
        Assert.Equal(0, score);
    }
}
