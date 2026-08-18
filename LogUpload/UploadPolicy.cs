namespace Stellar.CombatMeter.LogUpload;

/// <summary>Site content taxonomy. MUST stay identical to <c>FEED_KINDS</c> in
/// <c>services/stellar-logs/src/worker/routes/site.ts</c> and to <c>ContentKind</c> in
/// <c>src/rankedContent.ts</c> — the plugin configures the same buckets the site's feed tabs show.</summary>
internal enum ContentKind { Dungeon, Raid, WorldBoss, Vault, Other }

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
        ContentKind.Vault     => "vault",
        _                     => "other",
    };

    internal static string ArtifactKey(UploadArtifact artifact)
        => artifact == UploadArtifact.Replay ? "replay" : "stats";

    /// <summary>Spec § 2.2: <c>logUpload.policy.&lt;kind&gt;.&lt;artifact&gt;</c>.</summary>
    internal static string PrefKey(ContentKind kind, UploadArtifact artifact)
        => "logUpload.policy." + KindKey(kind) + "." + ArtifactKey(artifact);

    /// <summary>Catalog KEY for the settings-pane row label (en.json values match the site's feed tab names).</summary>
    internal static string Label(ContentKind kind) => kind switch
    {
        ContentKind.Dungeon   => "upload.kind.dungeon",
        ContentKind.Raid      => "upload.kind.raid",
        ContentKind.WorldBoss => "upload.kind.worldBoss",
        // Value is a catalog KEY now; en.json carries the site-aligned "Stimen Vaults" spelling.
        ContentKind.Vault     => "upload.kind.vault",
        _                     => "upload.kind.other",
    };
}

/// <summary>
/// The eight tri-state cells (4 content kinds × {stats, replay}) backing the per-content upload grid.
/// Pure in-memory state — prefs load/save lives in <c>Plugin.UploadPolicy.cs</c>. All cells default to
/// <see cref="UploadPolicyState.Auto"/>, which is today's behaviour exactly, so nothing shifts under
/// the owner on upgrade (spec § 2.1).
/// </summary>
internal sealed class UploadPolicyTable
{
    internal static readonly ContentKind[] Kinds =
        { ContentKind.Dungeon, ContentKind.Raid, ContentKind.WorldBoss, ContentKind.Vault, ContentKind.Other };

    internal static readonly UploadArtifact[] Artifacts =
        { UploadArtifact.Stats, UploadArtifact.Replay };

    // Auto is the zero value, so a fresh array is already all-Auto.
    private readonly UploadPolicyState[] _cells = new UploadPolicyState[Kinds.Length * Artifacts.Length];

    private static int Index(ContentKind kind, UploadArtifact artifact)
        => ((int)kind * Artifacts.Length) + (int)artifact;

    internal UploadPolicyState this[ContentKind kind, UploadArtifact artifact]
    {
        get => _cells[Index(kind, artifact)];
        set => _cells[Index(kind, artifact)] = value;
    }

    internal static UploadPolicyTable AllAuto() => new();

    /// <summary>
    /// <summary>
    /// FIRST-RUN defaults. Every cell <c>auto</c> EXCEPT <see cref="ContentKind.Other"/>, which is
    /// <c>off</c> on BOTH artifacts: a new player should not push unclassified content anywhere. This
    /// reverses § 2.1's all-Auto default — the owner accepted the upgrade behaviour change to stop the
    /// activity flood (Wondrous Tag, Guild Hall, Unstable Space) filling the site feed and evicting real
    /// runs from the 40-row retention bucket.
    ///
    /// <para><b>`off` costs nothing now.</b> An earlier revision flipped `other`'s REPLAY cell to
    /// <c>auto</c> to honour the owner's "replays always keep except open field" ruling. That misread the
    /// ruling: it is about STORING the replay, not uploading it. Storage is now unconditional — capture
    /// never consults the policy and a withheld upload RETAINS its record
    /// (<c>Plugin.UploadPolicy.RecomputeUploadPolicyCache</c>, <c>Plugin.LogUpload.RetainWithoutUpload</c>,
    /// <c>Plugin.Replay.PrepareReplayDoc</c>) — so an `off` cell withholds the SEND and loses nothing.
    /// A run that is later reclassified, or whose cell the owner turns on, can still be pushed with its
    /// true events. Keep both cells off here.</para>
    /// </summary>
    internal static UploadPolicyTable Defaults()
    {
        var t = new UploadPolicyTable();
        t[ContentKind.Other, UploadArtifact.Stats]  = UploadPolicyState.Off;
        t[ContentKind.Other, UploadArtifact.Replay] = UploadPolicyState.Off;
        return t;
    }

    internal static UploadPolicyTable Migrate(bool legacyAutoUpload, bool legacyUploadReplay)
    {
        var stats  = legacyAutoUpload   ? UploadPolicyState.Auto : UploadPolicyState.Manual;
        var replay = legacyUploadReplay ? UploadPolicyState.Auto : UploadPolicyState.Off;
        var table  = new UploadPolicyTable();
        foreach (var kind in Kinds)
        {
            // Spec § 8.2: `other` is forced OFF even on an upgrade (owner: "yes"), so the legacy prefs
            // seed only the four real content kinds.
            if (kind == ContentKind.Other)
            {
                // Both off on upgrade too (spec § 8.2, owner "yes"). Safe because storage is
                // unconditional — see Defaults().
                table[kind, UploadArtifact.Stats]  = UploadPolicyState.Off;
                table[kind, UploadArtifact.Replay] = UploadPolicyState.Off;
                continue;
            }
            table[kind, UploadArtifact.Stats]  = stats;
            table[kind, UploadArtifact.Replay] = replay;
        }
        return table;
    }
}
