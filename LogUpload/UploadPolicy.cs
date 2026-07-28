namespace Stellar.CombatMeter.LogUpload;

/// <summary>Site content taxonomy. MUST stay identical to <c>FEED_KINDS</c> in
/// <c>services/stellar-logs/src/worker/routes/site.ts</c> and to <c>ContentKind</c> in
/// <c>src/rankedContent.ts</c> — the plugin configures the same buckets the site's feed tabs show.</summary>
internal enum ContentKind { Dungeon, Raid, WorldBoss, Other }

/// <summary>The two independently configurable upload artifacts.</summary>
internal enum UploadArtifact { Stats, Replay }

/// <summary>Per-cell upload policy (spec § 2.1). Ordered so <c>Auto</c> is <c>default</c>.</summary>
internal enum UploadPolicyState
{
    /// <summary>Uploads automatically; a manual push also works.</summary>
    Auto,
    /// <summary>Never automatic; uploads only on an explicit user action.</summary>
    Manual,
    /// <summary>Never uploads, not even manually.</summary>
    Off,
}

/// <summary>What is asking to upload.</summary>
internal enum UploadTrigger { Auto, Manual }

/// <summary>
/// Pure policy resolution + wire/pref vocabulary for the per-content upload grid (spec
/// docs/superpowers/specs/2026-07-26-combatmeter-per-content-upload-config-design.md).
/// No dependencies — the whole matrix unit-tests headless.
/// </summary>
internal static class UploadPolicy
{
    /// <summary>Spec § 2.1: <c>auto</c> allows both triggers, <c>manual</c> only an explicit push,
    /// <c>off</c> neither.</summary>
    internal static bool Allows(UploadPolicyState state, UploadTrigger trigger) => state switch
    {
        UploadPolicyState.Auto   => true,
        UploadPolicyState.Manual => trigger == UploadTrigger.Manual,
        _                        => false,
    };

    internal static string Format(UploadPolicyState state) => state switch
    {
        UploadPolicyState.Manual => "manual",
        UploadPolicyState.Off    => "off",
        _                        => "auto",
    };

    /// <summary>Absent / unrecognised ⇒ <c>Auto</c>, so a hand-edited or truncated config degrades to
    /// today's behaviour rather than silently withholding a run.</summary>
    internal static UploadPolicyState Parse(string? raw) => raw switch
    {
        "manual" => UploadPolicyState.Manual,
        "off"    => UploadPolicyState.Off,
        _        => UploadPolicyState.Auto,
    };

    internal static string KindKey(ContentKind kind) => kind switch
    {
        ContentKind.Dungeon   => "dungeon",
        ContentKind.Raid      => "raid",
        ContentKind.WorldBoss => "worldboss",
        _                     => "other",
    };

    internal static string ArtifactKey(UploadArtifact artifact)
        => artifact == UploadArtifact.Replay ? "replay" : "stats";

    /// <summary>Spec § 2.2: <c>logUpload.policy.&lt;kind&gt;.&lt;artifact&gt;</c>.</summary>
    internal static string PrefKey(ContentKind kind, UploadArtifact artifact)
        => "logUpload.policy." + KindKey(kind) + "." + ArtifactKey(artifact);

    /// <summary>Settings-pane row label (matches the site's feed tab names).</summary>
    internal static string Label(ContentKind kind) => kind switch
    {
        ContentKind.Dungeon   => "Dungeons",
        ContentKind.Raid      => "Raids",
        ContentKind.WorldBoss => "World Boss",
        _                     => "Other",
    };
}
