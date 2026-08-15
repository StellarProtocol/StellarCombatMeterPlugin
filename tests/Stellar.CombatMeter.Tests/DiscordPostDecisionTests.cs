using Stellar.CombatMeter.LogUpload;
using Xunit;

public class DiscordPostDecisionTests
{
    [Fact] public void Skips_when_disabled()      => Assert.Equal(DiscordPostAction.Skip,    DiscordPostDecision.Decide(false, true, false, false, false));
    [Fact] public void Skips_when_no_rows()        => Assert.Equal(DiscordPostAction.Skip,    DiscordPostDecision.Decide(true, false, false, true, true));
    [Fact] public void Skips_when_already_posted() => Assert.Equal(DiscordPostAction.Skip,    DiscordPostDecision.Decide(true, true, true, true, true));
    [Fact] public void Posts_now_when_link_ready()  => Assert.Equal(DiscordPostAction.PostNow, DiscordPostDecision.Decide(true, true, false, true, false));
    [Fact] public void Posts_now_when_uploads_done_no_link() => Assert.Equal(DiscordPostAction.PostNow, DiscordPostDecision.Decide(true, true, false, false, true));
    [Fact] public void Enqueues_when_link_pending() => Assert.Equal(DiscordPostAction.Enqueue, DiscordPostDecision.Decide(true, true, false, false, false));
}
