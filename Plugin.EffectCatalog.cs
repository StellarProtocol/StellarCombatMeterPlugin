using System.Collections.Generic;
using System.Diagnostics;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.CombatMeter;

// Settings › Effects catalog: the WHOLE game BuffTable, materialised once per session as a time-sliced job (owner
// 2026-11-17, "Whole table"). The source is the framework's IGameDataCombat.AllBuffs() — the framework already walks
// Bokura.BuffTableBase in its own sliced eager load, so nothing here reflects into the game; this only projects those
// ~10k managed rows into the pick-list catalog (label resolution, placeholder test, kind split).
// Why sliced anyway: hidden rows resolve a translated design-name label each (EffectLabel → SourceNameResolver), and a
// one-shot pass over the whole table is a multi-ms single-tick spike (Plugin-Performance-Rules §1, the CooldownBar
// world-entry hitch). So the job advances within EffCatBudgetMs per frame and the list reads "Loading effects…" until
// it completes; the filter/sort runs once on completion, never on a half-built catalog.
// Lazy: nothing starts until the Effects tab is opened (OnEffectsTabShown) — never at login, never in the hidden
// Settings prebuild (§7: lists backed by loaded data report 0 rows while hidden), never on the meter's hot path.
public sealed partial class Plugin
{
    private const double EffCatBudgetMs = 2.0;   // per-frame slice; the clock is read every 64 rows (each is ~µs)

    private IEnumerator<BuffInfo>? _effCatEnum;  // in-flight walk over the framework's (immutable, swapped-whole) table
    private bool _effCatWanted;                  // the Effects tab asked for the catalog and it isn't complete yet
    private bool _effCatReady;                   // catalog complete → the list filters it (else "Loading effects…")
    private int  _effCatSlices;
    private long _effCatTicks;

    // Effects tab opened: start the build if it isn't done (no-op once ready — built once per session).
    private void RequestEffectCatalog()
    {
        if (!_effCatReady) _effCatWanted = true;
    }

    // Labels changed under the catalog (IGameText.Ready, Plugin.EffectNames.cs): drop it and rebuild with the real
    // labels — only if it was ever requested (an unopened Effects tab builds fresh on its first open anyway).
    private void RestartEffectCatalog()
    {
        if (!_effCatReady && !_effCatWanted) return;
        _effCatEnum?.Dispose();
        _effCatEnum = null;
        _effCatReady = false;
        _effCatWanted = true;
        _effectCatalog.Clear();
        ApplyDebuffCfgFilter(_dcFilter);   // not ready → empty list + "Loading effects…" until the rebuild lands
    }

    // Per frame (Plugin.Tick.cs OnUpdate), O(1) when idle: one bool test. While the framework's own table load is still
    // running AllBuffs() is an empty array (no allocation) and this just waits.
    private void PumpEffectCatalog()
    {
        if (!_effCatWanted) return;
        if (_effCatEnum == null)
        {
            var all = _services.GameData.Combat.AllBuffs();
            if (all.Count == 0) return;
            _effectCatalog.Clear();
            _effCatSlices = 0; _effCatTicks = 0;
            _effCatEnum = all.GetEnumerator();   // the one allocation per build (boxed enumerator)
        }
        long start = Stopwatch.GetTimestamp();
        long deadline = start + (long)(EffCatBudgetMs * Stopwatch.Frequency / 1000.0);
        _effCatSlices++;
        bool done = false;
        for (int n = 1; ; n++)
        {
            if (!_effCatEnum.MoveNext()) { done = true; break; }
            AddEffectCatalogRow(_effCatEnum.Current);
            if ((n & 63) == 0 && Stopwatch.GetTimestamp() >= deadline) break;
        }
        _effCatTicks += Stopwatch.GetTimestamp() - start;
        if (!done) return;

        _effCatEnum.Dispose();
        _effCatEnum = null;
        _effCatWanted = false;
        _effCatReady = true;
        _services.Log.Info($"[Effects] catalog: {_effectCatalog.Count} effects in {_effCatSlices} slices, "
            + $"{_effCatTicks * 1000.0 / Stopwatch.Frequency:F1} ms");
        ApplyDebuffCfgFilter(_dcFilter);   // first (sorted) fill of the list
    }

    // One table row → catalog entry. Iconed rows only: the meter strip can never draw an icon-less effect
    // (DebuffSelection.ShouldShow), so listing one would offer a toggle that does nothing. Game-hidden (placeholder
    // name) rows are kept with their readable label and listed under "Hidden effects".
    private void AddEffectCatalogRow(in BuffInfo b)
    {
        if (b.Id <= 0 || string.IsNullOrEmpty(b.IconPath)) return;
        var hidden = _services.GameText.IsPlaceholderName(b.Name);
        _effectCatalog[b.Id] = (hidden ? EffectLabel(b.Id, b.Name) : b.Name!, true, !b.IsDebuff, hidden);
    }
}
