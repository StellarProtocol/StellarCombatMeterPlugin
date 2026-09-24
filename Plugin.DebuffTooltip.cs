using System;
using System.Text.RegularExpressions;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.CombatMeter;

// Click-to-tooltip for the party-focus 2×2 debuff cells (owner 2026-09-24). Left-clicking a debuff cell shows
// that debuff's name + description (like the CooldownBar plugin); clicking the "+N" overflow cell lists ALL of
// that member's shown debuffs with descriptions. A borderless PanelElement popup positioned at the cursor,
// dismissed on an outside click (framework DismissOnOutsideClick) or by clicking the same cell again. The
// framework routes the click via MeterRowData.OnDebuffClick (wired once per row in WindowBuilder). Display only.
public sealed partial class Plugin
{
    private const int   TipMax = 16;     // entries the popup can list (a member's shown-debuff set is small)
    private const float TipW   = 360f;
    private const float TipH   = 320f;

    // Cooldown-tile accents — match CooldownBar / the meter tiles (red debuff, green buff).
    private static readonly ColorRgba TipDebuffAccent = new(1.00f, 0.35f, 0.35f, 1f);
    private static readonly ColorRgba TipBuffAccent   = new(0.35f, 1.00f, 0.50f, 1f);

    private struct DebuffTipEntry
    {
        public object? Tex; public UvRect Uv; public string Name; public string Desc;
        public bool IsBuff; public long ExpireMs; public int DurationMs;   // live-recomputed countdown source
    }

    // Live remaining fraction (elapsed → Fill01 completion) for a tip entry, recomputed each frame from now.
    private float TipFill01(int i)
    {
        var e = _tipEntries[i];
        if (e.DurationMs <= 0) return 0f;   // permanent — no fill
        long rem = e.ExpireMs - _services.CombatSnapshot.ServerNowMs;
        return 1f - Math.Clamp(rem / (float)e.DurationMs, 0f, 1f);
    }

    // Live countdown label ("48s"), recomputed each frame; empty for a permanent effect or once expired.
    private string TipSeconds(int i)
    {
        var e = _tipEntries[i];
        if (e.DurationMs <= 0) return "";
        long rem = e.ExpireMs - _services.CombatSnapshot.ServerNowMs;
        return rem <= 0 ? "" : $"{(rem + 999) / 1000}s";
    }

    private IWindowControl _debuffTip = null!;
    private readonly DebuffTipEntry[] _tipEntries = new DebuffTipEntry[TipMax];
    private int  _tipCount;
    private bool _tipOpen, _tipPlaced;
    private long _tipKey = -1;            // (entityId, cellIndex) currently shown — clicking it again toggles closed
    private WindowRect _tipRect;
    private Action<EntityId, int>? _onDebuffClick;

    private static readonly Regex TipTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static string TipStrip(string? s) => s == null ? "" : TipTagPattern.Replace(s, "");

    // Cached delegate handed to every row's MeterRowData.OnDebuffClick (no per-refresh allocation).
    private Action<EntityId, int> DebuffClickHandler => _onDebuffClick ??= OnDebuffCellClick;

    private IWindowControl BuildAndRegisterDebuffTip()
    {
        var rows = new HudElement[TipMax];
        for (var i = 0; i < TipMax; i++)
        {
            var idx = i;
            rows[i] = new ConditionalElement(() => idx < _tipCount, DebuffTipEntryElement(idx));
        }
        return _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.debuffTip",
                Title:       "",
                DefaultRect: new WindowRect(900f, 300f, TipW, TipH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = false, DismissOnOutsideClick = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            new PanelElement(
                new ScrollElement(new ColumnElement(rows, Gap: 10f), Height: TipH - 20f),
                Padding: 8f),
            OnClose: CloseDebuffTip));
    }

    // One tooltip row: the real cooldown TILE (red/green border + icon + countdown seconds + fill) — the same
    // CooldownTileElement CooldownBar and the meter use — then the name + description beside it (owner 2026-09-24).
    private HudElement DebuffTipEntryElement(int i)
        => new RowElement(new HudElement[]
        {
            new CooldownTileElement(
                Icon:        () => _tipEntries[i].Tex,
                Uv:          () => _tipEntries[i].Uv,
                Fill01:      () => TipFill01(i),
                Seconds:     () => TipSeconds(i),
                Accent:      () => _tipEntries[i].IsBuff ? TipBuffAccent : TipDebuffAccent,
                IsImagine:   () => false,
                ChargeCount: () => 0),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _tipEntries[i].Name, Emphasis: true),
                new TextElement(() => _tipEntries[i].Desc),
            }, Gap: 2f), Weight: 1f),
        }, Gap: 8f);

    // MeterRowData.OnDebuffClick target. Clicking ANY tile lists ALL of that member's shown buffs & debuffs
    // (owner 2026-09-24) — not just the clicked one. The icons match the meter cells exactly (both go through
    // ResolveDebuffIcon). Clicking the same member's block again closes it. cellIndex is unused.
    private void OnDebuffCellClick(EntityId id, int cellIndex)
    {
        long key = id.Value;   // per member — any cell shows that member's full list
        if (_tipOpen && _tipKey == key && _debuffTip.IsShown) { CloseDebuffTip(); return; }

        _getBuffFn ??= _services.GameData.Combat.GetBuff;
        var all = DebuffStrip.BuildAll(_services.CombatLookup.BuffsFor(id), _getBuffFn,
                                       _debuffSelection, _buffSelection, _services.CombatSnapshot.ServerNowMs);
        _tipCount = 0;
        for (var k = 0; k < all.Count && _tipCount < TipMax; k++) FillTipEntry(_tipCount++, all[k]);
        if (_tipCount == 0) { CloseDebuffTip(); return; }   // nothing shown for this member

        _tipKey = key;
        ShowDebuffTipAtCursor();
    }

    private void FillTipEntry(int slot, in DebuffEntry e)
    {
        var info = (_getBuffFn ??= _services.GameData.Combat.GetBuff)(e.BaseId);
        _tipEntries[slot] = new DebuffTipEntry
        {
            Tex     = ResolveDebuffIcon(e.BaseId, e.SourceSkillId, out var uv),
            Uv      = uv,
            Name    = TipStrip(info?.Name),
            Desc    = TipStrip(info?.Description),
            IsBuff    = e.IsBuff,
            ExpireMs  = e.ExpireMs,     // Fill01 + Seconds recompute live each frame (TipFill01/TipSeconds)
            DurationMs = e.DurationMs,
        };
    }

    private void ShowDebuffTipAtCursor()
    {
        var mx = Input.mousePosition.x + 8f;
        var my = Screen.height - Input.mousePosition.y - TipH - 8f;
        mx = Math.Clamp(mx, 0f, Screen.width  - TipW);
        my = Math.Clamp(my, 0f, Screen.height - TipH);
        _tipRect   = new WindowRect(mx, my, TipW, TipH);
        _tipPlaced = false;
        _tipOpen   = true;
        _debuffTip.SetRect(_tipRect);   // may no-op until the destroy-on-hide window remounts; TickDebuffTipPlace re-asserts
        _debuffTip.SetVisible(true);
    }

    // Re-assert the cursor rect after a destroy-on-hide remount (mirrors CooldownBar.TickTooltipPlace). Called from OnUpdate.
    internal void TickDebuffTipPlace()
    {
        if (!_tipOpen || _tipPlaced) return;
        if (_debuffTip.IsShown) { _debuffTip.SetRect(_tipRect); _tipPlaced = true; }
    }

    private void CloseDebuffTip()
    {
        _tipOpen   = false;
        _tipPlaced = false;
        _tipKey    = -1;
        _debuffTip.SetVisible(false);
    }
}
