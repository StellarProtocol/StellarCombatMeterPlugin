// src/samples/Stellar.CombatMeter/MeterElementToggles.cs
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>What the vertical HP spine shows (or whether it is hidden).</summary>
public enum VerticalBarMode
{
    /// <summary>Spine hidden.</summary>
    Off = 0,
    /// <summary>Spine height driven by HP fraction (default look).</summary>
    Hp  = 1,
    /// <summary>Spine height driven by DPS bar fraction.</summary>
    Dps = 2,
}

/// <summary>
/// What hue a player's DPS gauge is painted with. <c>Role</c> is the long-standing behaviour (the theme's
/// tank / healer / dps colour slots) and the default — nobody's meter changes until they opt in.
/// <c>Class</c> paints it with the game's own class crest colour (<see cref="ClassPalette"/>), falling back
/// to the role colour whenever the class is unknown.
/// </summary>
public enum BarColorMode
{
    /// <summary>Theme role slots: tank blue / healer green / dps red (default).</summary>
    Role  = 0,
    /// <summary>The game's class crest colour, role colour when the class is unknown.</summary>
    Class = 1,
}

/// <summary>
/// Per-mode element-visibility configuration for the meter row. One instance per mode (List, Party-focus).
/// Unity-free so it is unit-testable. Resolve combines the user toggles with the existing width-driven
/// collapse (List only) into the final per-element visibility.
/// </summary>
/// <summary>Player-tile size for the party-focus rows. Bigger sizes grow EVERY row uniformly (occupied,
/// debuff-less, and empty/absent slots alike) and scale the 2×2 debuff block to fit (owner 2026-09-24).</summary>
public enum TileSize { Small = 0, Medium = 1, Large = 2 }

public sealed class MeterElementToggles
{
    public bool Rank, Crest, Spec, Primary, Total, Share, Imagine, ImagineCooldown, LeaderFlag;
    public bool ClassName, AbilityScore, IllusionBreak, VoiceIcon, Debuffs;
    public VerticalBarMode VerticalBar;
    public BarColorMode BarColor;
    public MeterLabelStyle BarLabelStyle;
    public bool MainBarIsHp;
    public float SpineWidth;
    public ImagineSize ImagineSize;
    public TileSize TileSize;
    public int DebuffMax;   // max buff/debuff cells shown (4/6/8 → 2/3/4 columns × 2 rows)
    public ImaginePosition ImaginePosition;

    private const float SpecTotalMinW = 230f;
    private const float ShareMinW     = 180f;

    public static MeterElementToggles Defaults() => new()
    {
        Rank = true, Crest = true, Spec = true, VerticalBar = VerticalBarMode.Hp, MainBarIsHp = false, SpineWidth = 3f,
        BarColor = BarColorMode.Role, BarLabelStyle = MeterLabelStyle.Plain,
        Primary = true, Total = true, Share = true, Imagine = true, ImagineCooldown = true, LeaderFlag = true,
        ClassName = false, AbilityScore = false, IllusionBreak = false, VoiceIcon = true, Debuffs = true,
        ImagineSize = ImagineSize.Small, ImaginePosition = ImaginePosition.TopRight, TileSize = TileSize.Small,
        DebuffMax = 4,
    };

    // List-mode defaults: the buffs & debuffs block is OFF by default in the DPS list (it competes with the bar
    // width there); the user opts in per the List tab. Party modes default it ON via Defaults().
    public static MeterElementToggles ListDefaults()
    {
        var d = Defaults();
        d.Debuffs = false;
        return d;
    }

    // Leaner defaults for the dense 20-player raid grid: the tiny cells can't fit the full set, so spec /
    // total / imagine start off (rank · crest · name · HP · per-second · share · leader stay on).
    public static MeterElementToggles Raid20Defaults()
    {
        var d = Defaults();
        d.Spec = false; d.Total = false; d.Imagine = false; d.ImagineCooldown = false;
        return d;
    }

    /// <summary>Resolved per-element visibility for one row.</summary>
    public readonly record struct Resolved(
        bool Rank, bool Crest, bool Spec, bool ClassName, bool AbilityScore, bool IllusionBreak,
        bool Primary, bool Total, bool Share, bool Imagine, bool ImagineCooldown,
        bool LeaderFlag, bool VoiceIcon, bool Debuffs);

    /// <summary>Final visibility = user toggle AND (List only) the width-collapse guard.</summary>
    // Note: VerticalBar/MainBarIsHp/ImagineSize/ImaginePosition are NOT in Resolved — callers read them
    // directly off the toggle instance (e.g. AssembleRow reads toggles.VerticalBar).
    public Resolved Resolve(bool collapse, float widthNow)
    {
        bool wideEnoughSpec  = !collapse || widthNow >= SpecTotalMinW;
        bool wideEnoughShare = !collapse || widthNow >= ShareMinW;
        return new Resolved(
            Rank:            Rank,
            Crest:           Crest,
            Spec:            Spec  && wideEnoughSpec,
            ClassName:       ClassName && wideEnoughSpec,
            AbilityScore:    AbilityScore,
            IllusionBreak:   IllusionBreak,
            Primary:         Primary,
            Total:           Total && wideEnoughSpec,
            Share:           Share && wideEnoughShare,
            Imagine:         Imagine,
            ImagineCooldown: Imagine && ImagineCooldown,
            LeaderFlag:      LeaderFlag,
            VoiceIcon:       VoiceIcon,
            Debuffs:         Debuffs);   // supported in List too now (owner 2026-09-24); List defaults it OFF (ListDefaults)
    }

    /// <summary>
    /// Load from a config section using the per-mode key prefix ("list" | "party5" | "party20"). Each key
    /// falls back to the matching field on <paramref name="defaults"/>, so different modes can start from
    /// different baselines (e.g. Raid20Defaults for "party20").
    /// </summary>
    public static MeterElementToggles Load(IConfigSection cfg, string prefix, MeterElementToggles defaults)
    {
        var d = defaults;
        d.Rank            = cfg.Get($"{prefix}.show.rank",            defaults.Rank);
        d.Crest           = cfg.Get($"{prefix}.show.crest",           defaults.Crest);
        d.Spec            = cfg.Get($"{prefix}.show.spec",            defaults.Spec);
        d.VerticalBar     = (VerticalBarMode)cfg.Get($"{prefix}.bar.vertical",  (int)defaults.VerticalBar);
        // Absent key → the default (Role), so an install upgrading from ≤ 2.9.1 keeps its current look.
        // Older builds simply never read "bar.color" — leaving it unknown to them is what makes a rollback safe.
        d.BarColor        = (BarColorMode)cfg.Get($"{prefix}.bar.color",        (int)defaults.BarColor);
        // Absent key → Plain, so an install upgrading from ≤ 2.10.0-pre keeps today's look; those builds
        // never read "bar.labelStyle" either, so the key's presence is rollback-safe in both directions.
        d.BarLabelStyle    = (MeterLabelStyle)cfg.Get($"{prefix}.bar.labelStyle", (int)defaults.BarLabelStyle);
        d.MainBarIsHp     = cfg.Get($"{prefix}.bar.mainIsHp",                   defaults.MainBarIsHp);
        d.SpineWidth      = cfg.Get($"{prefix}.bar.spineWidth",                 defaults.SpineWidth);
        d.Primary         = cfg.Get($"{prefix}.show.primary",         defaults.Primary);
        d.Total           = cfg.Get($"{prefix}.show.total",           defaults.Total);
        d.Share           = cfg.Get($"{prefix}.show.share",           defaults.Share);
        d.Imagine         = cfg.Get($"{prefix}.show.imagine",         defaults.Imagine);
        d.ImagineCooldown = cfg.Get($"{prefix}.show.imagineCooldown", defaults.ImagineCooldown);
        d.LeaderFlag      = cfg.Get($"{prefix}.show.leaderFlag",      defaults.LeaderFlag);
        d.ClassName       = cfg.Get($"{prefix}.show.className",       defaults.ClassName);
        d.AbilityScore    = cfg.Get($"{prefix}.show.abilityScore",    defaults.AbilityScore);
        d.IllusionBreak   = cfg.Get($"{prefix}.show.illusionBreak",   defaults.IllusionBreak);
        d.VoiceIcon       = cfg.Get($"{prefix}.show.voiceIcon",       defaults.VoiceIcon);
        d.Debuffs         = cfg.Get($"{prefix}.show.debuffs",         defaults.Debuffs);
        d.ImagineSize     = (ImagineSize)cfg.Get($"{prefix}.imagine.size",     (int)defaults.ImagineSize);
        // Config key stays "debuff.size" (was the debuff-icon-size control before the 2026-09-24 rename to
        // "Player tile size") so an install keeps whatever size it had picked across this build.
        d.TileSize        = (TileSize)cfg.Get($"{prefix}.debuff.size",          (int)defaults.TileSize);
        d.DebuffMax       = cfg.Get($"{prefix}.debuff.max",                     defaults.DebuffMax);
        d.ImaginePosition = (ImaginePosition)cfg.Get($"{prefix}.imagine.position", (int)defaults.ImaginePosition);
        return d;
    }

    /// <summary>Player-tile size in px for a chosen size (Small=20 default → 48px row, Medium=26 → 58px,
    /// Large=32 → 72px). Drives both the debuff cell edge and, via the framework, the row height so every
    /// party row is the same size.</summary>
    public static float TileSizePx(TileSize s) => s switch { TileSize.Medium => 26f, TileSize.Large => 32f, _ => 20f };

    /// <summary>Clamp a stored buff/debuff max-cell count to a supported even value 4..12 (2 rows × 2..6 columns).</summary>
    public static int DebuffMaxCells(int m) => m <= 4 ? 4 : (m >= 12 ? 12 : (m % 2 == 0 ? m : m + 1));

    /// <summary>Persist back to the config section under the per-mode prefix.</summary>
    public void Save(IConfigSection cfg, string prefix)
    {
        cfg.Set($"{prefix}.show.rank",            Rank);
        cfg.Set($"{prefix}.show.crest",           Crest);
        cfg.Set($"{prefix}.show.spec",            Spec);
        cfg.Set($"{prefix}.bar.vertical",         (int)VerticalBar);
        cfg.Set($"{prefix}.bar.color",            (int)BarColor);
        cfg.Set($"{prefix}.bar.labelStyle",       (int)BarLabelStyle);
        cfg.Set($"{prefix}.bar.mainIsHp",         MainBarIsHp);
        cfg.Set($"{prefix}.bar.spineWidth",       SpineWidth);
        cfg.Set($"{prefix}.show.primary",         Primary);
        cfg.Set($"{prefix}.show.total",           Total);
        cfg.Set($"{prefix}.show.share",           Share);
        cfg.Set($"{prefix}.show.imagine",         Imagine);
        cfg.Set($"{prefix}.show.imagineCooldown", ImagineCooldown);
        cfg.Set($"{prefix}.show.leaderFlag",      LeaderFlag);
        cfg.Set($"{prefix}.show.className",       ClassName);
        cfg.Set($"{prefix}.show.abilityScore",    AbilityScore);
        cfg.Set($"{prefix}.show.illusionBreak",   IllusionBreak);
        cfg.Set($"{prefix}.show.voiceIcon",       VoiceIcon);
        cfg.Set($"{prefix}.show.debuffs",         Debuffs);
        cfg.Set($"{prefix}.imagine.size",         (int)ImagineSize);
        cfg.Set($"{prefix}.debuff.size",          (int)TileSize);   // key kept for continuity — now the player-tile size
        cfg.Set($"{prefix}.debuff.max",           DebuffMax);
        cfg.Set($"{prefix}.imagine.position",     (int)ImaginePosition);
    }
}
