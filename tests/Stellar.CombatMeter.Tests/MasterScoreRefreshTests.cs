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
    public async Task Poll_returns_first_changed_positive_snapshot_within_budget()
    {
        int calls = 0;
        // returns 0 twice (not yet credited), then 686
        int Read() => ++calls >= 3 ? 686 : 0;
        var score = await MasterScoreRefresh.PollForChangedScore(Read, before: 0, attempts: 4, delayMs: 1);
        Assert.Equal(686, score);
    }

    [Fact]
    public async Task Poll_returns_zero_when_never_populated()
    {
        var score = await MasterScoreRefresh.PollForChangedScore(() => 0, before: 0, attempts: 3, delayMs: 1);
        Assert.Equal(0, score);
    }

    [Fact]
    public async Task Poll_returns_zero_when_score_never_changes_from_before()
    {
        // previously known score is 4070; refresh keeps returning the same 4070 (no season improvement)
        var score = await MasterScoreRefresh.PollForChangedScore(() => 4070, before: 4070, attempts: 3, delayMs: 1);
        Assert.Equal(0, score);
    }

    [Fact]
    public async Task Poll_returns_new_value_when_score_changes_from_before()
    {
        int calls = 0;
        int Read() => ++calls >= 3 ? 4270 : 4070;
        var score = await MasterScoreRefresh.PollForChangedScore(Read, before: 4070, attempts: 4, delayMs: 1);
        Assert.Equal(4270, score);
    }

    [Fact]
    public async Task Poll_treats_first_ever_fetch_as_changed_when_before_is_zero()
    {
        int calls = 0;
        int Read() => ++calls >= 2 ? 686 : 0;
        var score = await MasterScoreRefresh.PollForChangedScore(Read, before: 0, attempts: 3, delayMs: 1);
        Assert.Equal(686, score);
    }
}
