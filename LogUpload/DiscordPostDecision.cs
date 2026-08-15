namespace Stellar.CombatMeter.LogUpload;

internal enum DiscordPostAction { Skip, PostNow, Enqueue }

/// <summary>Pure: the observer's branch. Post now if the link is ready or the run's uploads are all
/// terminal (link will never come); otherwise wait a bounded window (Enqueue). Skip if gated off /
/// nothing to say / already posted.</summary>
internal static class DiscordPostDecision
{
    internal static DiscordPostAction Decide(bool contentEnabled, bool hasRows, bool alreadyPosted, bool linkReady, bool uploadsTerminal)
    {
        if (!contentEnabled || !hasRows || alreadyPosted) return DiscordPostAction.Skip;
        if (linkReady || uploadsTerminal) return DiscordPostAction.PostNow;
        return DiscordPostAction.Enqueue;
    }
}
