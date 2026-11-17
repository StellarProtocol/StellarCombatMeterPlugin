using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
// Disambiguate from UnityEngine.Resolution (both namespaces are imported for the uGUI menu draw).
using Resolution = Stellar.Abstractions.Domain.Resolution;

namespace Stellar.CombatMeter;

// Right-click row context menu — a cursor-positioned house popover (approved redesign 2026-11-17 §2): 200 wide, caption =
// class crest + bold name, then 28 px rounded item rows (hover = Tile fill) with a 16 px icon in the label's colour; destructive
// items (Kick from Party, Leave Party) in red #ff8a8b. Items other plugins contribute via IEntityContextMenu render in the
// same style. Dismisses on click-outside or Escape (no Close button); clicking an item runs it and closes.
public sealed partial class Plugin
{
    private const int   MaxRowMenuItems = 8;
    private const float RowMenuW      = 200f;   // the visible popover (shell bubble); the window adds PopoverMargin each side
    private const float RowMenuItemH  = 28f;
    private const float RowMenuItemGap = 2f;
    private const float RowMenuPad    = 8f;
    private const float RowMenuCaptionH = 24f;
    private const float RowMenuCrest  = 18f;

    private IWindowControl _rowMenuWindow = null!;
    private readonly List<EntityMenuItem> _rowMenuItems = new(MaxRowMenuItems);
    private readonly bool[] _rowMenuDanger = new bool[MaxRowMenuItems];      // red label (Kick / Leave)
    private readonly byte[]?[] _rowMenuIconPng = new byte[]?[MaxRowMenuItems];   // cached PNG per slot (reference-stable)
    private readonly List<EntityMenuItem> _rowMenuDangerScratch = new(MaxRowMenuItems);   // reused by the destructive-last sort
    private readonly HoverFlag[] _rowMenuHover = NewHoverFlags(MaxRowMenuItems);
    private string _rowMenuName = "";
    private object? _rowMenuCrest;
    private UvRect _rowMenuCrestUv = new(0f, 0f, 1f, 1f);
    // Open/placement state. Dismiss itself is handled by the framework per render frame (DismissOnOutsideClick →
    // OnClose), so we don't poll input here; the only per-tick job left is re-asserting the cursor position once
    // the (destroy-on-hide) window remounts, since SetRect no-ops on the still-null token at open time.
    private bool _rowMenuOpen;
    private bool _rowMenuPlaced;
    private WindowRect _rowMenuRect;

    private static HoverFlag[] NewHoverFlags(int n) { var a = new HoverFlag[n]; for (int i = 0; i < n; i++) a[i] = new HoverFlag(); return a; }

    private float RowMenuWindowW => RowMenuW + 2f * PopoverMargin;

    private IWindowControl RegisterRowMenuWindow()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.rowmenu",
                Title:       "",
                DefaultRect: new WindowRect(100f, 100f, RowMenuWindowW, 0f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = false, DismissOnOutsideClick = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            BuildRowMenuRoot(),
            OnClose: CloseRowMenu));   // framework invokes this on Escape / click-outside (per-frame ticker)

    private void OpenRowMenu(EntityId entity)
    {
        if (!entity.IsPlayer) { CloseRowMenu(); return; }
        _rowMenuItems.Clear();
        foreach (var item in _services.EntityContextMenu.ItemsFor(entity))
        {
            if (_rowMenuItems.Count >= MaxRowMenuItems) break;
            _rowMenuItems.Add(item);
        }
        if (_rowMenuItems.Count == 0) { CloseRowMenu(); return; }

        _rowMenuName = EntityLabel.Resolve(entity, _services.CombatSnapshot.LocalEntityId,
            _services.PlayerState, _services.CombatLookup, _services.PartyRoster.Members);
        _rowMenuCrest = LoadCrest(entity, out _rowMenuCrestUv);
        ClassifyRowMenuItems();
        ShowRowMenuAtCursor();
    }

    // Empty raid slot right-clicked by the leader: single "Invite to Party" item that opens the game's
    // native invite picker (4-tab: Nearby / Friends / Guild / Recent).
    private void OpenInviteMenu()
    {
        _rowMenuItems.Clear();
        _rowMenuItems.Add(new EntityMenuItem(_loc.T("menu.inviteToParty"),
            () => _services.Lua.DoString("pcall(function() (Z.UIMgr):OpenView('team_invite_popup') end)")));
        _rowMenuName = _loc.T("menu.party");
        _rowMenuCrest = null;
        ClassifyRowMenuItems();
        ShowRowMenuAtCursor();
    }

    // Once per open (never per refresh): move the destructive items (Kick / Leave) to the end, keeping the order of the
    // rest, then give every slot its red flag and icon. The meter's own items get their pre-drawn glyph at the exact size
    // for the UI scale (Plugin.RowMenuIcons.cs); another plugin's item uses the IconPng it registered, else the generic dot.
    // PNG glyphs, not text — the prototype's ✉ ♛ 🔇 ⚑ are code points the game font can't be trusted to draw (SDL §6).
    private void ClassifyRowMenuItems()
    {
        _rowMenuDangerScratch.Clear();
        int keep = 0;
        for (int r = 0; r < _rowMenuItems.Count; r++)
        {
            var item = _rowMenuItems[r];
            OwnRowMenuIconStem(item.Label, out bool danger);
            if (danger) _rowMenuDangerScratch.Add(item); else _rowMenuItems[keep++] = item;
        }
        _rowMenuItems.RemoveRange(keep, _rowMenuItems.Count - keep);
        _rowMenuItems.AddRange(_rowMenuDangerScratch);

        int sizeIdx = RowMenuIconSizeIndex();
        for (int i = 0; i < MaxRowMenuItems; i++)
        {
            _rowMenuHover[i].On = false;   // a slot's hover never carries over to the next open
            if (i >= _rowMenuItems.Count) { _rowMenuDanger[i] = false; _rowMenuIconPng[i] = null; continue; }
            var stem = OwnRowMenuIconStem(_rowMenuItems[i].Label, out bool danger);
            _rowMenuDanger[i] = danger;
            _rowMenuIconPng[i] = stem != null ? RowMenuIconPng(stem, sizeIdx)
                               : _rowMenuItems[i].IconPng ?? RowMenuIconPng("generic", sizeIdx);
        }
    }

    // Anchor a corner of the menu at the cursor. Input is bottom-left origin; window rects are top-left.
    // Open down-right by default; flip to up / left only when the menu would overflow that screen edge.
    private void ShowRowMenuAtCursor()
    {
        float h = 2f * (PopoverMargin + RowMenuPad) + RowMenuCaptionH + RowMenuItemGap
                  + _rowMenuItems.Count * (RowMenuItemH + RowMenuItemGap);
        var fw = _services.Framework;
        var mp = Input.mousePosition;
        // Input.mousePosition is SCREEN PIXELS (bottom-left origin); WindowRect/SetRect speak CANVAS UNITS. The
        // overlay canvas carries a CanvasScaler whose scaleFactor is 1.0 ONLY at the 2560x1440 reference — below
        // "2K" screen px != canvas units, so the raw cursor coords land the menu offset from the click point.
        var (cx, top) = ScreenPointToCanvas(mp.x, mp.y,
            new Resolution(fw.ScreenWidth, fw.ScreenHeight), new Resolution(fw.CanvasWidth, fw.CanvasHeight));
        // The Borderless window's 12 px margin sits around the visible shell: start the WINDOW one margin up-left, so
        // the shell's corner lands on the cursor.
        var r = RowMenuAtCursor(new WindowRect(cx - PopoverMargin, top - PopoverMargin, RowMenuWindowW, h), fw.CanvasWidth, centerX: false);
        if (r.Y + h > fw.CanvasHeight) r = r with { Y = Math.Max(0f, fw.CanvasHeight - h) };
        SetRowMenuRect(r.X, r.Y, h);
    }

    // ---- pure placement math (canvas units), unit-tested in RowMenuPlacementTests ----

    // Convert a SCREEN-pixel point (bottom-left origin, as Input.mousePosition reports) to CANVAS UNITS
    // (top-left origin, the space WindowRect/SetRect use). canvas.W/H = screen.W/H / CanvasScaler.scaleFactor,
    // so canvas.W/screen.W is canvas-units-per-screen-px (1/scaleFactor). Falls back to raw (scale 1) when the
    // framework has not published a screen size yet.
    internal static (float X, float Top) ScreenPointToCanvas(float mouseX, float mouseY,
        Resolution screen, Resolution canvas)
    {
        float sfx = screen.Width  > 0 ? (float)canvas.Width  / screen.Width  : 1f;
        float sfy = screen.Height > 0 ? (float)canvas.Height / screen.Height : 1f;
        return (mouseX * sfx, canvas.Height - mouseY * sfy);
    }

    // Convert a SCREEN-pixel rect (top-left origin, as IWindowElement.OnClickWithRect delivers it) to CANVAS UNITS.
    internal static WindowRect ScreenRectToCanvas(WindowRect s, int screenW, int canvasW)
    {
        float sf = screenW > 0 ? (float)canvasW / screenW : 1f;   // canvas units per screen px
        return new WindowRect(s.X * sf, s.Y * sf, s.Width * sf, s.Height * sf);
    }

    // Given the menu rect placed with its top-left AT the cursor (canvas units), adjust X so it opens down-right,
    // flipping LEFT only when it would overflow the right canvas edge (or centring on the cursor when centerX).
    // Y is left at the cursor.
    internal static WindowRect RowMenuAtCursor(WindowRect atCursor, float canvasW, bool centerX)
    {
        float menuW = atCursor.Width, cursorX = atCursor.X;
        float x = centerX
            ? Math.Clamp(cursorX - menuW / 2f, 0f, Math.Max(0f, canvasW - menuW))
            : (cursorX + menuW <= canvasW) ? cursorX : Math.Max(0f, cursorX - menuW);
        return atCursor with { X = x };
    }

    // Centre the menu under an anchor button (canvas units) and place it directly below; flip ABOVE if it would
    // overflow the bottom canvas edge, and clamp X to the canvas.
    internal static WindowRect RowMenuBelow(WindowRect anchor, float menuW, float menuH, Resolution canvas)
    {
        float x = Math.Clamp(anchor.X + anchor.Width / 2f - menuW / 2f, 0f, Math.Max(0f, canvas.Width - menuW));
        float anchorBottom = anchor.Y + anchor.Height;
        float y = (anchorBottom + menuH <= canvas.Height) ? anchorBottom : Math.Max(0f, anchor.Y - menuH);
        return new WindowRect(x, y, menuW, menuH);
    }

    private void SetRowMenuRect(float x, float y, float h)
    {
        _rowMenuRect = new WindowRect(x, y, RowMenuWindowW, 0f);   // height 0: the window fits its content
        _rowMenuPlaced = false;
        _rowMenuOpen = true;
        _rowMenuWindow.SetRect(_rowMenuRect);   // no-op if the window is still unmounted; re-asserted in the tick
        _rowMenuWindow.SetVisible(true);
    }

    private HudElement BuildRowMenuRoot()
    {
        var rows = new HudElement[1 + MaxRowMenuItems];
        // Caption: crest (when the row has a class) + bold name, inset to line up with the item labels.
        rows[0] = new RowElement(new HudElement[]
        {
            new SpacerElement(4f, RowMenuCaptionH),
            new ConditionalElement(() => _rowMenuCrest != null,
                new GameTextureElement(() => _rowMenuCrest, (int)RowMenuCrest, (int)RowMenuCrest, () => _rowMenuCrestUv)
                    { CornerRadius = 5, Fill = true }),
            new CellElement(new TextElement(() => _rowMenuName, Color: () => TextColor(), NoWrap: true, FontSize: 13)
                { Bold = true, Ellipsize = true }, Weight: 1f),
        }, Gap: 6f);
        for (var i = 0; i < MaxRowMenuItems; i++)
        {
            var idx = i;   // a for-loop variable is shared by every closure — capture a copy
            rows[1 + i] = new ConditionalElement(() => idx < _rowMenuItems.Count, RowMenuItem(idx));
        }
        return PopoverShell(RowMenuPad, RowMenuItemGap, rows);
    }

    // One 28 px item: a rounded row whose fill shows only on hover (Tile), 10 px pad, a 16 px icon, a 9 px gap, then the
    // label (red for a destructive item). Icon + label are read from the snapshot taken at open — no per-refresh
    // formatting. The icon is Live (reference compare per refresh, the arrays are cached) because one pooled slot shows a
    // different item's glyph on each open. White glyphs (ours, the generic dot, and other plugins' IconPng — the
    // IEntityContextMenu contract is a flat white glyph) are tinted the LABEL's Text colour, so the icon is as bright as
    // its label and tracks the theme; the destructive ones are drawn red already, so no tint.
    // Gap MUST be 0.01f, not 0: RowElement Gap 0 means the DEFAULT 8 px RowGap, which put 8 px of extra left inset and
    // 8 + 9 + 8 = 25 px between icon and label. The 9 px gap spacer also carries the 28 px row height (no separate strut).
    private HudElement RowMenuItem(int idx)
    {
        var hover = _rowMenuHover[idx];
        ColorRgba Danger() => Readable(ProblemText, Menu.MenuBackground);
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new ImageElement(() => _rowMenuIconPng[idx], (int)RowMenuIconUnits, (int)RowMenuIconUnits)
                    { Live = true, Tint = () => _rowMenuDanger[idx] ? null : TextColor() },
                new SpacerElement(9f, RowMenuItemH),   // the icon→label gap + the row's 28 px height strut
                new CellElement(new TextElement(() => idx < _rowMenuItems.Count ? _rowMenuItems[idx].Label : "",
                    Color: () => _rowMenuDanger[idx] ? Danger() : TextColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
            }, Gap: 0.01f),
            OnClick: () => InvokeRowMenuItem(idx)) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => hover.On ? Fade(TileColor()) : Transparent,
            CornerRadius = 6f, PaddingX = 10f, PaddingY = 0f,
        };
    }

    private void InvokeRowMenuItem(int idx)
    {
        if (idx < 0 || idx >= _rowMenuItems.Count) return;
        var item = _rowMenuItems[idx];
        CloseRowMenu();
        item.OnClick();
    }

    // The only per-tick job: the window is destroyed while hidden and remounts a frame later, so SetRect in
    // OpenRowMenu no-ops on the still-null token. Re-assert the cursor rect once the mount lands so the menu
    // snaps to the click point instead of the remount's saved/default rect. (Dismiss is the framework's job.)
    private void TickRowMenuPlace()
    {
        if (!_rowMenuOpen || _rowMenuPlaced) return;
        if (_rowMenuWindow.IsShown) { _rowMenuWindow.SetRect(_rowMenuRect); _rowMenuPlaced = true; }
    }

    private void CloseRowMenu()
    {
        _rowMenuOpen = false;
        _rowMenuPlaced = false;
        _rowMenuWindow.SetVisible(false);
    }
}
