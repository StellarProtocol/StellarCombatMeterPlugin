using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Difficulty tiers a run can carry — ONE vocabulary, the one the site actually serves.
///
/// <para>The game shows two NAMES for the same rungs: a dungeon's Normal/Hard is a raid's
/// <c>Clash!</c>/<c>Brutal!</c>, which is the owner's taxonomy verbatim — <i>"clash = normal &lt; brutal =
/// hard &lt; purge = nightmare"</i>. Those are SYNONYMS, not extra tiers. Modelling them as separate enum
/// members (the shape before 2026-07-30) produced two chips that could never match a real run, because
/// <c>GET /api/site/content-kinds</c> tags raid 13002 <c>"hard"</c>, never <c>"brutal"</c>. The names now
/// live in <see cref="UploadTierFilter.TierLabel"/>; the enum holds tiers only.</para>
///
/// <para>Tiers are deliberately NOT an ordinal scale — <see cref="Backtrack"/> is an SS3 tier that REPLACED
/// normal/hard/nightmare on the two older raids (owner ruling 2026-07-29), so it has no position on a
/// "minimum difficulty" ladder. That is why the filter is a per-kind SET rather than a threshold.</para>
/// </summary>
internal enum ContentTier
{
    /// <summary>Tier could not be resolved — treated as allowed (fail-open).</summary>
    Unknown = 0,
    Normal,
    Hard,
    Master,
    /// <summary>The third raid tier. Served as <c>"nightmare"</c>, displayed as <c>Purge!</c>.</summary>
    Purge,
    Backtrack,
}

/// <summary>
/// Which difficulty tiers of a given <see cref="ContentKind"/> may upload, plus the master-level floor.
///
/// <para><b>Upload-only, never archive.</b> A tier being disabled must never stop a local archive: the run
/// still lands in history and can be pushed by hand. Gating the archive would destroy data the owner can
/// still choose to send.</para>
///
/// <para><b>Fail-open everywhere.</b> An unresolved tier (<see cref="ContentTier.Unknown"/> — no tier map
/// fetched yet, unlisted map id) and an unknown master level (<c>0</c>) both ALLOW the upload, matching
/// Spec B § 2.3's rule that an unreachable classifier must degrade to today's behaviour rather than
/// silently withhold runs.</para>
///
/// <para>Defaults enable every tier and set the master floor to 1, so a fresh install and an upgrade both
/// behave exactly as they do today.</para>
///
/// <para>Pure state — no prefs, no IO — so the whole matrix unit-tests headless.</para>
/// </summary>
internal sealed class UploadTierFilter
{
    /// <summary>Tiers offered per kind, in display order. Kinds absent here have no difficulty axis
    /// (world boss, vaults, other) and always upload regardless of tier.</summary>
    internal static readonly IReadOnlyDictionary<ContentKind, ContentTier[]> TiersFor =
        new Dictionary<ContentKind, ContentTier[]>
        {
            [ContentKind.Dungeon] = new[] { ContentTier.Normal, ContentTier.Hard, ContentTier.Master },
            // The tiers raids are actually SERVED as. Measured 2026-07-30 against the live payload:
            // 13021→normal, 13002→hard, 13003→nightmare(=Purge), 13001→backtrack. Listing Clash/Brutal
            // here instead gave two chips that matched nothing and left raid normal/hard unfilterable.
            [ContentKind.Raid] = new[] { ContentTier.Normal, ContentTier.Hard, ContentTier.Purge, ContentTier.Backtrack },
        };

    /// <summary>Lowest master level that may upload. 1 = every master run (today's behaviour).</summary>
    internal const int MinMasterLevelFloor = 1;

    /// <summary>Highest selectable master level — the game's Master 1..20 ladder.</summary>
    internal const int MaxMasterLevel = 20;

    // Keyed by (kind, tier), NOT tier alone. A flat tier-keyed set made `Hard` one shared key for a
    // dungeon's Hard and a raid's Brutal!, so disabling the dungeon chip silently blocked Brutal! raid
    // runs — measured 2026-07-30 (mapId 13002 autoSendAllowed=False with only the dungeon chip off).
    private readonly HashSet<(ContentKind Kind, ContentTier Tier)> _disabled = new();
    private int _minMasterLevel = MinMasterLevelFloor;

    /// <summary>Master-level floor, clamped to [1,20]. Only consulted for <see cref="ContentTier.Master"/>.</summary>
    internal int MinMasterLevel
    {
        get => _minMasterLevel;
        set => _minMasterLevel = value < MinMasterLevelFloor ? MinMasterLevelFloor
             : value > MaxMasterLevel ? MaxMasterLevel
             : value;
    }

    internal bool IsTierEnabled(ContentKind kind, ContentTier tier) => !_disabled.Contains((kind, tier));

    internal void SetTierEnabled(ContentKind kind, ContentTier tier, bool enabled)
    {
        // Unknown is the fail-open sentinel; it is never a user-facing chip and must stay allowed.
        if (tier == ContentTier.Unknown) return;
        if (enabled) _disabled.Remove((kind, tier));
        else _disabled.Add((kind, tier));
    }

    /// <summary>
    /// True when a run of <paramref name="kind"/> at <paramref name="tier"/> may upload.
    /// <paramref name="masterLevel"/> is the run's captured challenge level
    /// (<c>entry.DifficultyLevel</c>); <c>0</c> means unknown and never blocks.
    /// </summary>
    internal bool Allows(ContentKind kind, ContentTier tier, int masterLevel)
    {
        // Kinds with no difficulty axis are unaffected by tier state entirely — so a stray disabled
        // dungeon tier can never suppress a world-boss or vault run.
        if (!TiersFor.ContainsKey(kind)) return true;

        // Fail-open: no tier map yet, or a map id the site does not classify.
        if (tier == ContentTier.Unknown) return true;

        if (!IsTierEnabled(kind, tier)) return false;

        // The level floor applies ONLY to master runs, and only when the level is actually known.
        if (tier == ContentTier.Master && masterLevel > 0 && masterLevel < MinMasterLevel) return false;

        return true;
    }

    /// <summary>Wire/pref vocabulary. <see cref="ContentTier.Unknown"/> has no key — it is never stored.</summary>
    internal static string TierKey(ContentTier tier) => tier switch
    {
        ContentTier.Normal => "normal",
        ContentTier.Hard => "hard",
        ContentTier.Master => "master",
        ContentTier.Purge => "purge",
        ContentTier.Backtrack => "backtrack",
        _ => "",
    };

    /// <summary>Parses a tier tag as served by the site's <c>sceneDifficulty</c> master data. Anything
    /// unrecognised — including a future tier name — is <see cref="ContentTier.Unknown"/>, i.e. allowed,
    /// so a content patch cannot start silently withholding runs.</summary>
    /// <remarks>Synonyms NORMALIZE onto one tier, so the two naming schemes cannot drift apart: the raid
    /// names <c>clash</c>/<c>brutal</c> land on the same tiers as <c>normal</c>/<c>hard</c>, and the third
    /// raid tier accepts both <c>nightmare</c> (what the site serves) and <c>purge</c> (the game's name).
    /// Before this, <c>"brutal"</c> resolved to a tier no chip could reach.</remarks>
    internal static ContentTier ParseTier(string? raw) => raw switch
    {
        "normal" or "clash" => ContentTier.Normal,
        "hard" or "brutal" => ContentTier.Hard,
        "master" => ContentTier.Master,
        "nightmare" or "purge" => ContentTier.Purge,
        "backtrack" => ContentTier.Backtrack,
        _ => ContentTier.Unknown,
    };

    /// <summary>Settings-pane chip label. PER KIND, because the game shows one rung under two names: a
    /// dungeon's Normal/Hard is a raid's <c>Clash!</c>/<c>Brutal!</c>. Same stored tier either way — only
    /// the wording changes, so the owner reads the name the game shows them.</summary>
    internal static string TierLabel(ContentKind kind, ContentTier tier) =>
        kind == ContentKind.Raid
            ? tier switch
            {
                ContentTier.Normal => "Clash!",
                ContentTier.Hard => "Brutal!",
                ContentTier.Purge => "Purge!",
                ContentTier.Backtrack => "Backtrack!",
                _ => TierKey(tier),
            }
            : TierKey(tier);

    /// <summary>Spec § 2.2 shape, now KIND-scoped: <c>logUpload.tier.&lt;kind&gt;.&lt;tier&gt;</c>. The
    /// kind segment is what keeps a dungeon chip from writing the raid chip's pref (and vice versa);
    /// nothing shipped with the old un-scoped key, so there is no migration to carry.</summary>
    internal static string TierPrefKey(ContentKind kind, ContentTier tier)
        => "logUpload.tier." + UploadPolicy.KindKey(kind) + "." + TierKey(tier);

    internal const string MasterLevelPrefKey = "logUpload.masterLevelMin";
}
