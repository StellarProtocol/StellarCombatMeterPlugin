using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.CombatMeter;

// ONE icon chain for every place an effect is drawn — the party-row tiles (ToSlot), the click tooltip
// (Plugin.DebuffTooltip) and Settings › Effects (DcIcon) — approved 2026-11-17 (CooldownBar ResolveTileIcon parity + more):
//   0. the source skill's Battle-Imagine card (live source skill, else BuffTable.SkillId), then the curated lockout map
//      (LockoutArcaneSkill) — checked BEFORE tier 1, because a lockout's wire source is a summon-variant skill whose own
//      skill icon would otherwise win;
//   1. that source skill's OWN skill icon;
//   2. the effect's own icon (BuffTable.Icon);
//   3. a placeholder tile by category (EffectCategoryClassifier) — never a blank tile.
// Cost: ResolveDebuffs runs ~200×/s at raid-20, so the resolved art is cached per (base id, source skill) and the chain
// re-runs only on a 200 ms → 1 s retry schedule until the best tier this effect can reach has loaded (game icons load
// asynchronously: null = "not yet"). While a better tier is still expected (first IconLoadWindowMs) the cell shows the
// dark loading body as before; after that the placeholder. The category is computed once per base id and cached; the
// placeholder textures are decoded once per (glyph, kind, size). The steady-state hit path is one dictionary lookup.
public sealed partial class Plugin
{
    private const int  TierImagine = 0, TierSkill = 1, TierBuff = 2, TierPlaceholder = 3;
    private const long IconFastRetryMs = 200, IconFastWindowMs = 2000, IconSlowRetryMs = 1000;
    private const long IconLoadWindowMs = 1500;   // real art expected: keep the loading body this long before the placeholder

    private sealed class EffectIconEntry
    {
        public object? Tex;
        public UvRect  Uv;
        public int     Tier = int.MaxValue;     // tier that produced Tex (lower = preferred)
        public int     Best = TierPlaceholder;  // best tier this effect can reach (re-derived on retry: tables load late)
        public long    FirstTry, RetryAt;
    }

    private readonly Dictionary<long, EffectIconEntry> _effIconCache = new();
    private readonly Dictionary<int, EffectCategory> _effCategory = new();
    private static readonly UvRect FullUv = new(0f, 0f, 1f, 1f);

    /// <summary>The art for effect <paramref name="baseId"/> applied by <paramref name="sourceSkillId"/> (0 = unknown)
    /// in a slot <paramref name="units"/> canvas units wide; <paramref name="isBuff"/> picks the green / red placeholder.
    /// Null only while real art is still loading.</summary>
    internal object? ResolveEffectIcon(int baseId, int sourceSkillId, bool isBuff, float units, out UvRect uv)
    {
        long now = Environment.TickCount64;
        long key = ((long)baseId << 32) | (uint)sourceSkillId;
        if (!_effIconCache.TryGetValue(key, out var e))
        {
            e = new EffectIconEntry { FirstTry = now };
            _effIconCache[key] = e;
        }
        // A cached texture can be destroyed under us (Unity fake-null) — treat it as a miss and reload.
        if (e.Tex is UnityEngine.Object uo && uo == null) { e.Tex = null; e.Tier = int.MaxValue; e.RetryAt = 0; }

        if (e.Tier > e.Best && now >= e.RetryAt)
        {
            var tex = ResolveEffectArt(baseId, sourceSkillId, out var artUv, out int tier, out e.Best);
            if (tex != null && tier <= e.Tier) { e.Tex = tex; e.Uv = artUv; e.Tier = tier; }
            e.RetryAt = now + (now - e.FirstTry < IconFastWindowMs ? IconFastRetryMs : IconSlowRetryMs);
        }
        if (e.Tex != null) { uv = e.Uv; return e.Tex; }
        uv = FullUv;
        if (e.Best < TierPlaceholder && now - e.FirstTry < IconLoadWindowMs) return null;   // still loading
        return EffectPlaceholder(EffectCategoryOf(baseId), isBuff, units);
    }

    // Tiers 0-2, reporting the tier that hit and the best tier this effect could reach with its data.
    private object? ResolveEffectArt(int baseId, int sourceSkillId, out UvRect uv, out int tier, out int best)
    {
        var ga = _services.GameAssets;
        var bi = (_getBuffFn ??= _services.GameData.Combat.GetBuff)(baseId);
        int skillId = sourceSkillId != 0 ? sourceSkillId : (bi?.SkillId ?? 0);
        bool hasSkillIcon = skillId > 0 && !string.IsNullOrEmpty(_services.GameData.Combat.GetSkill(skillId)?.IconPath);
        bool hasBuffIcon = !string.IsNullOrEmpty(bi?.IconPath);

        int imagineSkill = 0;
        if (skillId > 0 && _services.ResonanceData.GetImagineForSkill(skillId) is { } m) imagineSkill = m.SkillId;
        // Curated fallback for known player-arcane lockouts (Mechanical Failure / Element Stasis): their BuffTable
        // carries SkillId 0 and the generic abnormal icon, and the wire source resolves to a slot-[0] summon variant
        // GetImagineForSkill can't map — so point at the base [7,8] arcane the debuff locks out (owner 2026-09-24).
        else if (LockoutArcaneSkill(baseId) is { } lk && _services.ResonanceData.GetImagineForSkill(lk) is { } lm) imagineSkill = lm.SkillId;

        best = imagineSkill > 0 ? TierImagine : hasSkillIcon ? TierSkill : hasBuffIcon ? TierBuff : TierPlaceholder;
        object? tex;
        if (imagineSkill > 0) { tier = TierImagine; tex = ga.LoadImagineIcon(imagineSkill, out uv); if (tex != null) return tex; }
        if (hasSkillIcon)     { tier = TierSkill;   tex = ga.LoadSkillIcon(skillId, out uv);        if (tex != null) return tex; }
        if (hasBuffIcon)      { tier = TierBuff;    tex = ga.LoadBuffIcon(baseId, out uv);          if (tex != null) return tex; }
        tier = TierPlaceholder; uv = FullUv;
        return null;
    }

    // Once per base id. Signal = the design label: the game name when it is a real one (a Chinese client's fallback is
    // the raw Chinese NameDesign) + the framework's English design label. Only cached once IGameText is ready — a
    // classification made during its background load would pin "Default" on effects it knows nothing about yet.
    private EffectCategory EffectCategoryOf(int baseId)
    {
        if (_effCategory.TryGetValue(baseId, out var cat)) return cat;
        var text = _services.GameText;
        EnsureGameTextHooked();   // starts the load; OnGameTextReady → RefreshEffectLabels clears this cache
        string? name = (_getBuffFn ??= _services.GameData.Combat.GetBuff)(baseId)?.Name;
        if (text.IsPlaceholderName(name)) name = null;
        cat = EffectCategoryClassifier.Classify(name, text.GetDesignName(GameTextKind.Buff, baseId));
        if (text.IsReady || cat != EffectCategory.Default) _effCategory[baseId] = cat;
        return cat;
    }

    // ── placeholder tiles (tools/icons/gen-effect-placeholders.py) ───────────────────────────────────────────────
    // Ascending; must match the generator's SIZES. Art covers the cell body (meter tile art = cell − 2: 18/24/30 units),
    // tooltip 28, Settings › Effects 20 — each at 0.75 / 1 / 1.5 px per unit, plus 16 / 32 / 64.
    private static readonly int[] PlaceholderSizes = { 14, 15, 16, 18, 20, 21, 23, 24, 27, 28, 30, 32, 36, 42, 45, 64 };
    // Index = (int)EffectCategory; must match the generator's GLYPHS stems.
    private static readonly string[] PlaceholderStems = { "default", "cc", "immunity", "shield", "heal", "dot", "up", "down" };

    // [category * 2 + (buff ? 0 : 1)][size index] — decoded on first use. Missing resource = stays null.
    private readonly Texture2D?[][] _placeholderTex = new Texture2D?[PlaceholderStems.Length * 2][];

    private Texture2D? EffectPlaceholder(EffectCategory cat, bool isBuff, float units)
    {
        var fw = _services.Framework;
        float pxPerUnit = fw.CanvasWidth > 0 && fw.ScreenWidth > 0 ? (float)fw.ScreenWidth / fw.CanvasWidth : 1f;
        int sizeIdx = PickIconSizeIndex(units * pxPerUnit, PlaceholderSizes);
        int set = (int)cat * 2 + (isBuff ? 0 : 1);
        var bySize = _placeholderTex[set] ??= new Texture2D?[PlaceholderSizes.Length];
        var tex = bySize[sizeIdx];
        if (tex != null) return tex;   // Unity null-check also catches a destroyed texture → re-decode
        tex = bySize[sizeIdx] = LoadPlaceholder($"{PlaceholderStems[(int)cat]}-{(isBuff ? "buff" : "debuff")}_{PlaceholderSizes[sizeIdx]}.png");
        return tex;
    }

    // A pixel-hinted tile picked for its exact on-screen size is drawn 1:1: no mip chain + Bilinear (the framework's
    // WindowBuilder.LoadIcon rule for exact-size glyphs).
    private static Texture2D? LoadPlaceholder(string file)
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.CombatMeter.effects." + file);
            if (s is null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = UnityEngine.FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
            };
            if (!ImageConversion.LoadImage(tex, ms.ToArray())) { UnityEngine.Object.Destroy(tex); return null; }
            return tex;
        }
        catch { return null; }
    }

    // Dispose: free the decoded placeholder textures.
    private void DestroyEffectPlaceholders()
    {
        foreach (var bySize in _placeholderTex)
            if (bySize != null)
                for (int i = 0; i < bySize.Length; i++)
                    if (bySize[i] != null) { UnityEngine.Object.Destroy(bySize[i]); bySize[i] = null; }
    }

    // IGameText became ready (RefreshEffectLabels): categories resolved during its load were guesses without the
    // English design label.
    private void ClearEffectCategories() => _effCategory.Clear();
}
