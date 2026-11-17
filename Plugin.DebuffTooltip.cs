using System;
using System.Text.RegularExpressions;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using Resolution = Stellar.Abstractions.Domain.Resolution;

namespace Stellar.CombatMeter;

// Click-to-tooltip for a meter row's buff/debuff cells (owner 2026-09-24; house popover per the approved redesign
// 2026-11-17 §2, prototype section 3). Clicking ANY cell of a member lists ALL of that member's shown effects:
//   header (32 px): class crest · bold name / muted "EFFECTS · N debuffs · M buffs" · round ✕
//   a panel of effect tiles: 28 px icon with a red (debuff) / green (buff) ring · bold name · countdown in the kind
//   colour on the right · muted description. Debuffs first.
// A Borderless popover at the cursor, dismissed on an outside click / Esc (framework DismissOnOutsideClick), the ✕, or
// clicking the same member's block again. The framework routes the click via MeterRowData.OnDebuffClick. Display only.
public sealed partial class Plugin
{
    private const int   TipMax       = 16;     // entries the popup can list (a member's shown-effect set is small)
    private const int   TipInlineMax = 6;      // more than this → the tiles scroll inside a fixed-height panel
    private const float TipW         = 340f;   // the visible popover (shell); the window adds PopoverMargin each side
    private const float TipScrollH   = 340f;
    private const float TipIcon      = 28f;
    private const float TipCrest     = 32f;
    private const float TipSecondsW  = 48f;

    // Kind colours — match CooldownBar / the meter cells (red debuff #ff5959, green buff #59ff80).
    private static readonly ColorRgba TipDebuffAccent = new(1.00f, 0.35f, 0.35f, 1f);
    private static readonly ColorRgba TipBuffAccent   = new(0.35f, 1.00f, 0.50f, 1f);

    private struct DebuffTipEntry
    {
        public object? Tex; public UvRect Uv; public string Name; public string Desc;
        public bool IsBuff; public long ExpireMs; public int DurationMs;   // live-recomputed countdown source
    }

    private IWindowControl _debuffTip = null!;
    private readonly DebuffTipEntry[] _tipEntries = new DebuffTipEntry[TipMax];
    private readonly long[] _tipSecKey = new long[TipMax];
    private readonly string[] _tipSecText = NewTexts(TipMax);
    private int  _tipCount;
    private bool _tipOpen, _tipPlaced;
    private long _tipKey = -1;            // member currently shown — clicking their block again toggles closed
    private WindowRect _tipRect;
    private string _tipName = "", _tipSubtitle = "";
    private object? _tipCrest;
    private UvRect _tipCrestUv = new(0f, 0f, 1f, 1f);
    private Action<EntityId, int>? _onDebuffClick;

    private static readonly Regex TipTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static string TipStrip(string? s) => s == null ? "" : TipTagPattern.Replace(s, "");

    // Cached delegate handed to every row's MeterRowData.OnDebuffClick (no per-refresh allocation).
    private Action<EntityId, int> DebuffClickHandler => _onDebuffClick ??= OnDebuffCellClick;

    // Live countdown ("25m" / "48s" / "9.4s" — CooldownBar's format, CountdownLabel), re-formatted only when the SHOWN
    // value changes (tenths under 10 s, seconds under ~17 min, then minutes); "" for a permanent effect or once expired.
    private string TipSeconds(int i)
    {
        var e = _tipEntries[i];
        if (e.DurationMs <= 0) return "";
        long rem = e.ExpireMs - _services.CombatSnapshot.ServerNowMs;
        long key = rem <= 0 ? -1 : rem < 10_000 ? rem / 100 : rem <= 999_000 ? 1_000_000 + rem / 1000 : 2_000_000 + rem / 60_000;
        if (key != _tipSecKey[i]) { _tipSecKey[i] = key; _tipSecText[i] = key < 0 ? "" : CountdownLabel.Format(rem); }
        return _tipSecText[i];
    }

    private IWindowControl BuildAndRegisterDebuffTip()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.debuffTip",
                Title:       "",
                DefaultRect: new WindowRect(900f, 300f, TipW + 2f * PopoverMargin, 0f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = false, DismissOnOutsideClick = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            PopoverShell(10f, 8f, new HudElement[]
            {
                BuildTipHeader(),
                // Up to TipInlineMax tiles sit straight in the panel; more scroll inside a fixed height (two copies of
                // the 16-slot list, built once — the popover is small and never rebuilt per refresh).
                PanelBox(new HudElement[]
                {
                    new ConditionalElement(() => _tipCount <= TipInlineMax,
                        TipList(),
                        new ScrollElement(TipList(), TipScrollH) { FixedHeight = true }),
                }),
            }),
            OnClose: CloseDebuffTip));

    private HudElement BuildTipHeader() => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, TipCrest),
        new ConditionalElement(() => _tipCrest != null,
            new GameTextureElement(() => _tipCrest, (int)TipCrest, (int)TipCrest, () => _tipCrestUv) { CornerRadius = 7, Fill = true }),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => _tipName, Color: () => TextColor(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
            SmallMuted(() => _tipSubtitle, () => MutedColor()),
        }, Gap: 1f), Weight: 1f),
        IconButton(() => "✕", CloseDebuffTip),
    }, Gap: 10f);

    private HudElement TipList()
    {
        var slots = new HudElement[TipMax];
        for (var i = 0; i < TipMax; i++) slots[i] = TipTile(i);
        return new ListElement(() => _tipCount, slots) { Gap = 4f };
    }

    // One effect tile (prototype `.lr`): Tile fill r6; the icon inside a transparent bubble whose 1 px outline is the kind
    // ring; name + countdown on the first line, the description (wrapping, muted) under it. The countdown sits in its
    // own fixed-width transparent bubble so its 10 Hz changes re-measure only that tiny bubble, never the tile.
    private HudElement TipTile(int i)
    {
        ColorRgba Kind() => _tipEntries[i].IsBuff ? TipBuffAccent : TipDebuffAccent;
        return new BubbleElement(new RowElement(new HudElement[]
        {
            new CellElement(new BubbleElement(
                new GameTextureElement(() => _tipEntries[i].Tex, (int)TipIcon, (int)TipIcon, () => _tipEntries[i].Uv)
                    { CornerRadius = 4, Fill = true })
            {
                FillColor = Transparent, OutlineColorFn = () => Kind(), CornerRadius = 4f, PaddingX = 0f, PaddingY = 0f,
            }, Width: TipIcon),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new CellElement(new TextElement(() => _tipEntries[i].Name, Color: () => TextColor(), NoWrap: true, FontSize: 13)
                        { Bold = true, Ellipsize = true }, Weight: 1f),
                    new BubbleElement(new TextElement(() => TipSeconds(i), Color: () => Kind(), Width: TipSecondsW,
                        Align: TextAlign.Right, NoWrap: true, FontSize: 13) { Bold = true })
                    {
                        FillColor = Transparent, PaddingX = 0f, PaddingY = 0f, MaxWidth = TipSecondsW,
                    },
                }, Gap: 6f),
                SmallMuted(() => _tipEntries[i].Desc, () => MutedColor(), wrap: true),
            }, Gap: 2f), Weight: 1f),
        }, Gap: 8f))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = 6f, PaddingX = 8f, PaddingY = 6f,
        };
    }

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
        // Debuffs first, then buffs (each in the strip's own order).
        _tipCount = 0;
        int debuffs = 0, buffs = 0;
        for (var k = 0; k < all.Count && _tipCount < TipMax; k++) if (!all[k].IsBuff) { FillTipEntry(_tipCount++, all[k]); debuffs++; }
        for (var k = 0; k < all.Count && _tipCount < TipMax; k++) if (all[k].IsBuff)  { FillTipEntry(_tipCount++, all[k]); buffs++; }
        if (_tipCount == 0) { CloseDebuffTip(); return; }   // nothing shown for this member

        _tipKey = key;
        _tipName = EntityLabel.Resolve(id, _services.CombatSnapshot.LocalEntityId,
            _services.PlayerState, _services.CombatLookup, _services.PartyRoster.Members);
        _tipCrest = LoadCrest(id, out _tipCrestUv);
        // Each count is its own singular/plural piece ("1 debuff · 3 buffs"); the subtitle key just joins them.
        _tipSubtitle = _loc.TFormat("hud.tip.subtitle",                     // once per open
            _loc.TFormat(PluralKey(debuffs, "hud.tip.debuffsOne", "hud.tip.debuffs"), debuffs),
            _loc.TFormat(PluralKey(buffs, "hud.tip.buffsOne", "hud.tip.buffs"), buffs));
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
            ExpireMs  = e.ExpireMs,     // TipSeconds recomputes live
            DurationMs = e.DurationMs,
        };
        _tipSecKey[slot] = long.MinValue;   // force a fresh countdown string for the new entry
    }

    // Down-right of the cursor (flipped to stay on the canvas). Input.mousePosition is SCREEN pixels, bottom-left
    // origin; SetRect takes CANVAS units — convert first (the row menu's helper), or it drifts off the cursor whenever
    // the UI scale isn't 1.
    private void ShowDebuffTipAtCursor()
    {
        var fw = _services.Framework;
        var mp = Input.mousePosition;
        var (cx, top) = ScreenPointToCanvas(mp.x, mp.y,
            new Resolution(fw.ScreenWidth, fw.ScreenHeight), new Resolution(fw.CanvasWidth, fw.CanvasHeight));
        float w = TipW + 2f * PopoverMargin;
        float h = 2f * (PopoverMargin + 10f) + TipCrest + 8f + 2f * PanelPadY
                  + (_tipCount <= TipInlineMax ? _tipCount * 60f : TipScrollH);   // rough: only for edge clamping
        float x = cx + 8f - PopoverMargin, y = top + 8f - PopoverMargin;
        if (x + w > fw.CanvasWidth) x = cx - 8f - w + PopoverMargin;
        if (y + h > fw.CanvasHeight) y = fw.CanvasHeight - h;
        _tipRect   = new WindowRect(Math.Max(0f, x), Math.Max(0f, y), w, 0f);
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
