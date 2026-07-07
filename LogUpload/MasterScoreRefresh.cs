// Pure, game-free helpers for the post-run account-master-score refresh: the master-mode
// gate and the bounded poll for the refreshed snapshot value. Kept free of any game/framework
// dependency so it can be unit-tested without a running game.

using System;
using System.Threading.Tasks;

namespace Stellar.CombatMeter;

/// <summary>Pure, game-free helpers for the post-run account-master-score refresh:
/// the master-mode gate and the bounded poll for the refreshed snapshot value.</summary>
internal static class MasterScoreRefresh
{
    /// <summary>True when the archived run carries a master-mode signal (a positive settlement
    /// master-mode score, or a positive latched difficulty level) — the gate for firing the
    /// account-score refresh + off-throttle self send.</summary>
    internal static bool IsMasterModeRun(int masterModeScore, int difficultyLevel)
        => masterModeScore > 0 || difficultyLevel > 0;

    /// <summary>Poll <paramref name="readScore"/> up to <paramref name="attempts"/> times, waiting
    /// <paramref name="delayMs"/> between reads; return the first value that is both &gt; 0 and
    /// different from <paramref name="before"/> (the pre-refresh baseline), else 0. This guards
    /// against both a stale first read (still equal to <paramref name="before"/>) and a genuinely
    /// unchanged master score — a master clear that doesn't improve the season score must not be
    /// (re)sent.</summary>
    internal static async Task<int> PollForChangedScore(Func<int> readScore, int before, int attempts, int delayMs)
    {
        for (int i = 0; i < attempts; i++)
        {
            var v = readScore();
            if (v > 0 && v != before) return v;
            if (i < attempts - 1) await Task.Delay(delayMs).ConfigureAwait(false);
        }
        return 0;
    }
}
