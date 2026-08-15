using Stellar.CombatMeter.LogUpload;
using Xunit;

public class DiscordWebhookPosterTests
{
    [Fact]
    public void Invalid_url_calls_back_false_synchronously_without_network()
    {
        bool? ok = null; int status = -1; string? err = null;
        DiscordWebhookPoster.PostFireAndForget("https://evil.com/x", "{}", (o, s, e) => { ok = o; status = s; err = e; });
        Assert.False(ok);         // synchronous — assertion runs before any Task.Run could
        Assert.Equal(0, status);
        Assert.Equal("invalid webhook url", err);
    }

    [Fact]
    public void Empty_url_calls_back_false()
    {
        bool? ok = null;
        DiscordWebhookPoster.PostFireAndForget("", "{}", (o, _, _) => ok = o);
        Assert.False(ok);
    }
}
