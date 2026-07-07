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
    /// <paramref name="delayMs"/> between reads; return the first value that is &gt; 0 (i.e. the
    /// social-snapshot cache has been populated by the refresh), else 0. The send decision itself
    /// is made separately by <see cref="ShouldSend"/> against the last-SENT baseline — this poll's
    /// only job is to wait out the refresh RPC's latency.</summary>
    internal static async Task<int> PollForScore(Func<int> readScore, int attempts, int delayMs)
    {
        for (int i = 0; i < attempts; i++)
        {
            var v = readScore();
            if (v > 0) return v;
            if (i < attempts - 1) await Task.Delay(delayMs).ConfigureAwait(false);
        }
        return 0;
    }

    /// <summary>True when <paramref name="fetched"/> is a real score and differs from
    /// <paramref name="lastSent"/> — the persisted baseline of what we last actually pushed to the
    /// server (NOT the volatile in-memory cache, which can be warm from an unrelated ID-card open
    /// and mask a genuine change, or cold and mask a genuine no-op). <paramref name="lastSent"/>
    /// should default to a sentinel such as -1 ("never sent") so the first real score always
    /// qualifies.</summary>
    internal static bool ShouldSend(int fetched, int lastSent) => fetched > 0 && fetched != lastSent;
}
