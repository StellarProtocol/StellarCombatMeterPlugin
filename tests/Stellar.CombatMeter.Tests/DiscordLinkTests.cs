using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;
using Xunit;

public class DiscordLinkTests
{
    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/abcDEF", true)]
    [InlineData("https://discordapp.com/api/webhooks/123/abcDEF", true)]
    [InlineData("https://ptb.discord.com/api/webhooks/1/x", false)]   // only discord.com / discordapp.com hosts
    [InlineData("http://discord.com/api/webhooks/1/x", false)]         // must be https
    [InlineData("https://evil.com/api/webhooks/1/x", false)]
    [InlineData("https://discord.com/channels/1/2", false)]           // not a webhook path
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidWebhookUrl_matches_only_discord_webhook_https(string? url, bool expected)
        => Assert.Equal(expected, DiscordLink.IsValidWebhookUrl(url));

    [Theory]
    [InlineData("https://revette.io/run/sea/CvCyokazcx", true)]       // base62 shortId (has a letter)
    [InlineData("/run/sea/CvCyokazcx", true)]                          // relative form
    [InlineData("https://revette.io/run/sea/643789110607085568", false)] // all-digits levelUuid = NOT shareable
    [InlineData("https://revette.io/run/sea/", false)]
    [InlineData(null, false)]
    public void IsShareable_true_only_for_non_numeric_shortId(string? url, bool expected)
        => Assert.Equal(expected, DiscordLink.IsShareable(url));

    [Fact]
    public void PickShareable_returns_most_recent_done_shortId()
    {
        var cands = new List<(long, bool, string?)>
        {
            (100, true,  "https://revette.io/run/sea/AAA111"),   // done + shortId, older
            (300, true,  "https://revette.io/run/sea/999888"),   // done but numeric -> not shareable
            (200, true,  "https://revette.io/run/sea/BBB222"),   // done + shortId, newer than 100
            (400, false, "https://revette.io/run/sea/CCC333"),   // shortId but not done
        };
        Assert.Equal("https://revette.io/run/sea/BBB222", DiscordLink.PickShareable(cands));
    }

    [Fact]
    public void PickShareable_null_when_none_qualify()
        => Assert.Null(DiscordLink.PickShareable(new List<(long, bool, string?)> { (1, false, "x"), (2, true, "/run/sea/123") }));

    [Fact]
    public void MaskWebhook_keeps_host_and_id_but_hides_the_token()
    {
        var m = DiscordLink.MaskWebhook("https://discord.com/api/webhooks/1538054088229396582/j8AryfEXQ0Le2Gv8xG3");
        Assert.Contains("discord.com/api/webhooks/1538054088229396582/", m);
        Assert.DoesNotContain("j8AryfEXQ0Le2Gv8xG3", m);   // full token never shown
        Assert.Contains("j8Ar", m);                         // first 4 of the token, for recognition
        Assert.DoesNotContain("https://", m);               // scheme stripped for brevity
    }

    [Fact]
    public void MaskWebhook_passthrough_for_non_webhook_and_empty()
    {
        Assert.Equal("", DiscordLink.MaskWebhook(""));
        Assert.Equal("https://evil.com/x", DiscordLink.MaskWebhook("https://evil.com/x"));
    }
}
