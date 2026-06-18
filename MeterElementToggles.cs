// src/samples/Stellar.CombatMeter/MeterElementToggles.cs
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// Per-mode element-visibility configuration for the meter row. One instance per mode (List, Party-focus).
/// Unity-free so it is unit-testable. Resolve combines the user toggles with the existing width-driven
/// collapse (List only) into the final per-element visibility.
/// </summary>
public sealed class MeterElementToggles
{
    public bool Rank, Crest, Spec, HpBar, Primary, Total, Share, Imagine, ImagineCooldown, LeaderFlag;
    public bool ClassName, AbilityScore;
    public ImagineSize ImagineSize;
    public ImaginePosition ImaginePosition;

    private const float SpecTotalMinW = 230f;
    private const float ShareMinW     = 180f;

    public static MeterElementToggles Defaults() => new()
    {
        Rank = true, Crest = true, Spec = true, HpBar = true, Primary = true, Total = true,
        Share = true, Imagine = true, ImagineCooldown = true, LeaderFlag = true,
        ClassName = false, AbilityScore = false,
        ImagineSize = ImagineSize.Small, ImaginePosition = ImaginePosition.TopRight,
    };

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
        bool Rank, bool Crest, bool Spec, bool ClassName, bool AbilityScore, bool HpBar,
        bool Primary, bool Total, bool Share, bool Imagine, bool ImagineCooldown,
        bool LeaderFlag);

    /// <summary>Final visibility = user toggle AND (List only) the width-collapse guard.</summary>
    // Note: ImagineSize/ImaginePosition are NOT in Resolved — they aren't width-gated, so callers read them
    // directly off the toggle (e.g. BuildRowData sets MeterRowData.ImagineSize = toggles.ImagineSize).
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
            HpBar:           HpBar,
            Primary:         Primary,
            Total:           Total && wideEnoughSpec,
            Share:           Share && wideEnoughShare,
            Imagine:         Imagine,
            ImagineCooldown: Imagine && ImagineCooldown,
            LeaderFlag:      LeaderFlag);
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
        d.HpBar           = cfg.Get($"{prefix}.show.hp",              defaults.HpBar);
        d.Primary         = cfg.Get($"{prefix}.show.primary",         defaults.Primary);
        d.Total           = cfg.Get($"{prefix}.show.total",           defaults.Total);
        d.Share           = cfg.Get($"{prefix}.show.share",           defaults.Share);
        d.Imagine         = cfg.Get($"{prefix}.show.imagine",         defaults.Imagine);
        d.ImagineCooldown = cfg.Get($"{prefix}.show.imagineCooldown", defaults.ImagineCooldown);
        d.LeaderFlag      = cfg.Get($"{prefix}.show.leaderFlag",      defaults.LeaderFlag);
        d.ClassName       = cfg.Get($"{prefix}.show.className",       defaults.ClassName);
        d.AbilityScore    = cfg.Get($"{prefix}.show.abilityScore",    defaults.AbilityScore);
        d.ImagineSize     = (ImagineSize)cfg.Get($"{prefix}.imagine.size", (int)defaults.ImagineSize);
        d.ImaginePosition = (ImaginePosition)cfg.Get($"{prefix}.imagine.position", (int)defaults.ImaginePosition);
        return d;
    }

    /// <summary>Persist back to the config section under the per-mode prefix.</summary>
    public void Save(IConfigSection cfg, string prefix)
    {
        cfg.Set($"{prefix}.show.rank",            Rank);
        cfg.Set($"{prefix}.show.crest",           Crest);
        cfg.Set($"{prefix}.show.spec",            Spec);
        cfg.Set($"{prefix}.show.hp",              HpBar);
        cfg.Set($"{prefix}.show.primary",         Primary);
        cfg.Set($"{prefix}.show.total",           Total);
        cfg.Set($"{prefix}.show.share",           Share);
        cfg.Set($"{prefix}.show.imagine",         Imagine);
        cfg.Set($"{prefix}.show.imagineCooldown", ImagineCooldown);
        cfg.Set($"{prefix}.show.leaderFlag",      LeaderFlag);
        cfg.Set($"{prefix}.show.className",       ClassName);
        cfg.Set($"{prefix}.show.abilityScore",    AbilityScore);
        cfg.Set($"{prefix}.imagine.size",         (int)ImagineSize);
        cfg.Set($"{prefix}.imagine.position",     (int)ImaginePosition);
    }
}
