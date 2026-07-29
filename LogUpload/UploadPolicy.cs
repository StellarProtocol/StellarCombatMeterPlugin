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

    /// <summary>Settings-pane row label (matches the site's feed tab names).</summary>
    internal static string Label(ContentKind kind) => kind switch
    {
        ContentKind.Dungeon   => "Dungeons",
        ContentKind.Raid      => "Raids",
        ContentKind.WorldBoss => "World Boss",
        // Master-data spelling is "Stimen" (the owner wrote "Stiment") — keep it aligned with the site.
        ContentKind.Vault     => "Stimen Vaults",
        _                     => "Other",
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
    /// Shipping defaults: every cell <c>auto</c> EXCEPT <see cref="ContentKind.Other"/>'s STATS cell,
    /// which is <c>off</c>. Stats-off reverses § 2.1's all-Auto default — the owner accepted that it
    /// changes behaviour on upgrade, to stop the activity flood (Wondrous Tag, Guild Hall, Unstable
    /// Space) filling the site feed and evicting real runs from the 40-row retention bucket.
    ///
    /// <c>Other</c>'s REPLAY cell stays <c>auto</c> — see the inline note below for why turning it off
    /// contradicted a standing ruling and cost a real run. (This sentence used to read "off on both
    /// artifacts"; it was stale from 2026-07-29 until corrected.)
    /// </summary>
    internal static UploadPolicyTable Defaults()
    {
        var t = new UploadPolicyTable();
        t[ContentKind.Other, UploadArtifact.Stats] = UploadPolicyState.Off;
        // REPLAY stays AUTO for `other`. Corrected 2026-07-29 after the owner's Giant Golem Crusade run
        // came back with no replay: "I thought I have already made decision that replays always keep
        // except open field right?" They had — 2026-07-01 replay-R1 spec § 1: "In the field / open world
        // it stays fully off", i.e. everything INSTANCED keeps its replay.
        //
        // Turning replay off for `other` over-reached, because "except open field" is already enforced
        // STRUCTURALLY and independently: PrepareReplayDoc bails on `entry.LevelUuid == 0`, and a field
        // fight has no run id. The kind policy was never what kept open-world replays out.
        //
        // And the flood this default exists to stop is a STATS problem — activity runs filling the site
        // feed and evicting real runs from the 40-row retention bucket. Replay docs are per-run detail;
        // they never enter the feed. So `other` = stats off + replay auto satisfies both rulings at once.
        return t;
    }

    /// <summary>
    /// Spec § 2.2 one-shot migration, run on the first load where no new keys exist so an existing
    /// install keeps its behaviour. <c>autoUpload=false</c> seeds <c>manual</c> (not <c>off</c>) because
    /// today that user can still push a run by hand and <c>off</c> would take that away;
    /// <c>uploadReplay=false</c> seeds <c>off</c> because there is no separate manual replay action.
    /// </summary>
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
                // Stats forced off (spec § 8.2, owner "yes"); REPLAY follows the legacy pref like every
                // other kind — see Defaults() for why `other` must keep its replay.
                table[kind, UploadArtifact.Stats]  = UploadPolicyState.Off;
                table[kind, UploadArtifact.Replay] = replay;
                continue;
            }
            table[kind, UploadArtifact.Stats]  = stats;
            table[kind, UploadArtifact.Replay] = replay;
        }
        return table;
    }
}
