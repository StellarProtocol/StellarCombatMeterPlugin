using System;

namespace Stellar.CombatMeter;

/// <summary>The placeholder glyph an icon-less effect is drawn with (approved option B, 2026-11-17). Values index the
/// pre-drawn PNG stems in Plugin.EffectIcons.cs — append only.</summary>
public enum EffectCategory
{
    Default = 0,          // ring + centre dot: no keyword matched (most internal markers / counters)
    CrowdControl = 1,     // spiral
    Immunity = 2,         // shield + check
    Shield = 3,           // shield outline
    Heal = 4,             // plus
    DamageOverTime = 5,   // droplet
    StatUp = 6,           // up arrow
    StatDown = 7,         // down arrow
}

/// <summary>
/// Guesses an effect's category from its design label. The game has NO category field for the ~8.6k icon-less buffs
/// (KB Buff-Tracking.md, release_3.7_2 recount: BuffType is 0 on most, BuffAbilityType is set on ~10 rows), so the
/// only signal is the designer's <c>NameDesign</c> label — a keyword heuristic, first match wins, in the approved
/// order: crowd control → immunity → shield → heal → damage over time → stat down → stat up → default.
/// <para>Two keyword sets because two forms of the label reach the plugin: the raw Chinese one (a Chinese client's
/// game name falls back to it) and the framework's English translation (<c>IGameText.GetDesignName</c>, the only
/// design-name API — the raw Chinese <c>NameDesign</c> is not exposed). The English set was tuned against the
/// framework's GameText table so it agrees with the Chinese rules on ~93% of icon-less rows.</para>
/// Pure and allocation-light (one lower-cased copy per call); the plugin calls it once per buff id and caches.
/// </summary>
public static class EffectCategoryClassifier
{
    // Chinese keywords, matched as plain substrings. Bare 盾 is deliberately absent (剑盾 = "sword and shield" class).
    private static readonly (EffectCategory Cat, string[] Keys)[] Zh =
    {
        (EffectCategory.CrowdControl, new[] { "眩晕", "晕", "冰冻", "冻结", "定身", "禁锢", "沉默", "减速", "击飞", "击退", "浮空", "控制", "嘲讽", "恐惧", "麻痹", "石化", "束缚", "倒地" }),
        (EffectCategory.Immunity, new[] { "免疫", "无敌", "霸体" }),
        (EffectCategory.Shield, new[] { "护盾", "减伤", "免伤", "伤害减免" }),
        (EffectCategory.Heal, new[] { "治疗", "回血", "恢复", "回复", "吸血" }),
        (EffectCategory.DamageOverTime, new[] { "灼烧", "燃烧", "中毒", "毒", "流血", "出血", "DOT", "持续伤害" }),
        (EffectCategory.StatDown, new[] { "降低", "减少", "削弱", "易伤", "破防", "虚弱", "减防", "减攻" }),
        (EffectCategory.StatUp, new[] { "增伤", "提升", "提高", "增加", "加成", "强化", "攻击力", "暴击", "急速", "幸运", "精通", "加速", "攻速" }),
    };

    // English keywords against the normalised label " word word " (lower case, every non-alphanumeric run → one space,
    // padded with spaces): " stun" = a word STARTING with "stun"; " slow " = the whole word only.
    private static readonly (EffectCategory Cat, string[] Keys)[] En =
    {
        (EffectCategory.CrowdControl, new[] { " stun", " freez", " frozen ", " root ", " rooted ", " immobil", " imprison", " silenc",
            " slow ", " slowed ", " slows ", " slowing ", " slowdown ", " knock up", " knockup", " knockback", " knock back",
            " knockdown", " knock down", " airborne ", " taunt", " fear ", " feared ", " paraly", " petrif", " bind ", " bound ",
            " shackle", " daze", " downed ", " crowd control", " cc " }),
        (EffectCategory.Immunity, new[] { " immun", " invincib", " invulnerab", " super armor", " superarmor", " unstoppable " }),
        (EffectCategory.Shield, new[] { " shield", " barrier", " damage reduction", " dmg reduction", " damage taken down",
            " reduces damage taken", " reduced damage taken", " damage mitigation" }),
        (EffectCategory.Heal, new[] { " heal", " hp regen", " hp recovery", " hp restor", " lifesteal", " life steal", " regenerat" }),
        (EffectCategory.DamageOverTime, new[] { " burn", " ignit", " poison", " toxic ", " bleed", " dot ", " damage over time" }),
        (EffectCategory.StatDown, new[] { " reduc", " decreas", " lower", " weaken", " vulnerab", " armor break", " def down",
            " defense down", " atk down", " attack down", " weakness ", " down " }),
        (EffectCategory.StatUp, new[] { " increas", " boost", " enhanc", " amplif", " bonus ", " attack up", " atk up", " damage up",
            " dmg up", " crit", " haste ", " luck", " mastery ", " accelerat", " attack speed", " speed up", " empower", " up " }),
    };

    // Phrases blanked out before the English match: a weapon class and energy (not HP) regen read as false hits.
    private static readonly string[] EnNoise = { " sword and shield ", " energy recovery", " energy regen", " slow fall" };

    /// <summary>Category for an effect. <paramref name="label"/> = a raw (possibly Chinese) name/design label;
    /// <paramref name="englishDesign"/> = the English design label. Either may be null. For each category in rule
    /// order, a Chinese hit in either string or an English hit in <paramref name="englishDesign"/> wins.</summary>
    public static EffectCategory Classify(string? label, string? englishDesign)
    {
        string en = Normalise(englishDesign) + Normalise(label);
        for (int r = 0; r < Zh.Length; r++)
        {
            if (ContainsAny(label, Zh[r].Keys) || ContainsAny(englishDesign, Zh[r].Keys)) return Zh[r].Cat;
            if (en.Length > 0 && ContainsAny(en, En[r].Keys)) return En[r].Cat;
        }
        return EffectCategory.Default;
    }

    private static bool ContainsAny(string? s, string[] keys)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var k in keys) if (s.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    // " word word " — lower case, non-alphanumerics collapsed to single spaces, noise phrases blanked.
    internal static string Normalise(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var buf = new char[s.Length + 2];
        int n = 0;
        buf[n++] = ' ';
        foreach (var ch in s)
        {
            if (ch < 128 && char.IsLetterOrDigit(ch)) buf[n++] = char.ToLowerInvariant(ch);
            else if (buf[n - 1] != ' ') buf[n++] = ' ';
        }
        if (buf[n - 1] != ' ') buf[n++] = ' ';
        var t = new string(buf, 0, n);
        foreach (var noise in EnNoise) if (t.Contains(noise, StringComparison.Ordinal)) t = t.Replace(noise, " ", StringComparison.Ordinal);
        return t;
    }
}
