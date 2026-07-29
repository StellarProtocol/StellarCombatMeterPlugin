using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Difficulty tiers a run can carry. Two vocabularies, because the game uses two: dungeons run
/// normal/hard/master, raids run clash/brutal/purge/backtrack. They are deliberately NOT collapsed onto a
/// single ordinal scale — <see cref="Backtrack"/> is an SS3 tier that REPLACED normal/hard/nightmare on the
/// two older raids (owner ruling 2026-07-29), so it has no position on a "minimum difficulty" ladder. That
/// is exactly why the filter is a per-kind SET rather than a threshold.
/// </summary>
internal enum ContentTier
{
    /// <summary>Tier could not be resolved — treated as allowed (fail-open).</summary>
    Unknown = 0,
    Normal,
    Hard,
    Master,
    Clash,
    Brutal,
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
            [ContentKind.Raid] = new[] { ContentTier.Clash, ContentTier.Brutal, ContentTier.Purge, ContentTier.Backtrack },
        };

    /// <summary>Lowest master level that may upload. 1 = every master run (today's behaviour).</summary>
    internal const int MinMasterLevelFloor = 1;

    /// <summary>Highest selectable master level — the game's Master 1..20 ladder.</summary>
    internal const int MaxMasterLevel = 20;

    private readonly HashSet<ContentTier> _disabled = new();
    private int _minMasterLevel = MinMasterLevelFloor;

    /// <summary>Master-level floor, clamped to [1,20]. Only consulted for <see cref="ContentTier.Master"/>.</summary>
    internal int MinMasterLevel
    {
        get => _minMasterLevel;
        set => _minMasterLevel = value < MinMasterLevelFloor ? MinMasterLevelFloor
             : value > MaxMasterLevel ? MaxMasterLevel
             : value;
    }

    internal bool IsTierEnabled(ContentTier tier) => !_disabled.Contains(tier);

    internal void SetTierEnabled(ContentTier tier, bool enabled)
    {
        // Unknown is the fail-open sentinel; it is never a user-facing chip and must stay allowed.
        if (tier == ContentTier.Unknown) return;
        if (enabled) _disabled.Remove(tier);
        else _disabled.Add(tier);
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

        if (!IsTierEnabled(tier)) return false;

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
        ContentTier.Clash => "clash",
        ContentTier.Brutal => "brutal",
        ContentTier.Purge => "purge",
        ContentTier.Backtrack => "backtrack",
        _ => "",
    };

    /// <summary>Parses a tier tag as served by the site's <c>sceneDifficulty</c> master data. Anything
    /// unrecognised — including a future tier name — is <see cref="ContentTier.Unknown"/>, i.e. allowed,
    /// so a content patch cannot start silently withholding runs.</summary>
    internal static ContentTier ParseTier(string? raw) => raw switch
    {
        "normal" => ContentTier.Normal,
        "hard" => ContentTier.Hard,
        "master" => ContentTier.Master,
        "clash" => ContentTier.Clash,
        "brutal" => ContentTier.Brutal,
        "purge" => ContentTier.Purge,
        "backtrack" => ContentTier.Backtrack,
        // The site labels the third raid tier "nightmare" in prose while the map data tags it "purge";
        // accept both so the two vocabularies cannot drift into a silent all-block.
        "nightmare" => ContentTier.Purge,
        _ => ContentTier.Unknown,
    };

    /// <summary>Settings-pane chip label.</summary>
    internal static string TierLabel(ContentTier tier) => TierKey(tier);

    /// <summary>Spec § 2.2 shape: <c>logUpload.tier.&lt;tier&gt;</c> / <c>logUpload.masterLevelMin</c>.</summary>
    internal static string TierPrefKey(ContentTier tier) => "logUpload.tier." + TierKey(tier);

    internal const string MasterLevelPrefKey = "logUpload.masterLevelMin";
}
