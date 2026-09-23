using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
// Disambiguate from UnityEngine.Resolution (both namespaces are imported for the uGUI menu draw).
using Resolution = Stellar.Abstractions.Domain.Resolution;

namespace Stellar.CombatMeter;

// Right-click row context menu — a cursor-positioned popup drawn as a themed PanelElement (background + border)
// with a member-name header. Dismisses on click-outside or Escape (no Close button); clicking an item runs it
// and closes. The meter owns the menu; plugins contribute items via IEntityContextMenu without it knowing them.
public sealed partial class Plugin
{
    private const int   MaxRowMenuItems = 8;
    private const float RowMenuW   = 168f;
    private const float RowMenuItemH = 30f;
    private const float RowMenuPad = 8f;

    private IWindowControl _rowMenuWindow = null!;
    private readonly List<EntityMenuItem> _rowMenuItems = new(MaxRowMenuItems);
    private string _rowMenuName = "";
    // Open/placement state. Dismiss itself is handled by the framework per render frame (DismissOnOutsideClick →
    // OnClose), so we don't poll input here; the only per-tick job left is re-asserting the cursor position once
    // the (destroy-on-hide) window remounts, since SetRect no-ops on the still-null token at open time.
    private bool _rowMenuOpen;
    private bool _rowMenuPlaced;
    private WindowRect _rowMenuRect;

    private IWindowControl RegisterRowMenuWindow()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.rowmenu",
                Title:       "",
                DefaultRect: new WindowRect(100f, 100f, RowMenuW, 120f),
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
        ShowRowMenuAtCursor();
    }

    // Anchor a corner of the menu at the cursor. Input is bottom-left origin; window rects are top-left.
    // Open down-right by default; flip to up / left only when the menu would overflow that screen edge.
    private void ShowRowMenuAtCursor() => ShowRowMenu(centerOnCursorX: false);

    // Horizontally centre the menu on the cursor X — used when the menu is triggered by a header button
    // so it appears centred under the button rather than left-aligned at the click point.
    private void ShowRowMenuCenteredAtCursor() => ShowRowMenu(centerOnCursorX: true);

    // Position the menu centred on the anchor button and directly below it (preferred), or above it if
    // it would overflow the bottom of the screen. Falls back to cursor position if anchor has no area yet
    // (zero-width, first click before the button's RectTransform has been read).
    // NOTE: OnClickWithRect delivers the anchor in SCREEN PIXELS (WindowBuilder.FireOnClickWithRect); SetRect
    // speaks CANVAS UNITS, so the anchor is converted before placement (see the CanvasScaler note on ShowRowMenu).
    private void ShowRowMenuBelow(WindowRect anchorScreen)
    {
        if (anchorScreen.Width <= 0f) { ShowRowMenuCenteredAtCursor(); return; }
        float h = 4f + RowMenuPad * 2f + 24f + 14f + _rowMenuItems.Count * RowMenuItemH;
        var fw = _services.Framework;
        var anchor = ScreenRectToCanvas(anchorScreen, fw.ScreenWidth, fw.CanvasWidth);
        var r = RowMenuBelow(anchor, RowMenuW, h, new Resolution(fw.CanvasWidth, fw.CanvasHeight));
        SetRowMenuRect(r.X, r.Y, h);
    }

    private void ShowRowMenu(bool centerOnCursorX)
    {
        float h = 4f + RowMenuPad * 2f + 24f + 14f + _rowMenuItems.Count * RowMenuItemH;
        var fw = _services.Framework;
        var mp = Input.mousePosition;
        // Input.mousePosition is SCREEN PIXELS (bottom-left origin); WindowRect/SetRect speak CANVAS UNITS. The
        // overlay canvas carries a CanvasScaler whose scaleFactor is 1.0 ONLY at the 2560x1440 reference — below
        // "2K" screen px != canvas units, so the raw cursor coords land the menu offset from the click point.
        var (cx, top) = ScreenPointToCanvas(mp.x, mp.y,
            new Resolution(fw.ScreenWidth, fw.ScreenHeight), new Resolution(fw.CanvasWidth, fw.CanvasHeight));
        var r = RowMenuAtCursor(new WindowRect(cx, top, RowMenuW, h), fw.CanvasWidth, centerOnCursorX);
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
        _rowMenuRect = new WindowRect(x, y, RowMenuW, h);
        _rowMenuPlaced = false;
        _rowMenuOpen = true;
        _rowMenuWindow.SetRect(_rowMenuRect);   // no-op if the window is still unmounted; re-asserted in the tick
        _rowMenuWindow.SetVisible(true);
    }

    private HudElement BuildRowMenuRoot()
    {
        var rows = new HudElement[2 + MaxRowMenuItems];
        rows[0] = new TextElement(() => _rowMenuName, Emphasis: true);
        rows[1] = new SeparatorElement();
        for (var i = 0; i < MaxRowMenuItems; i++)
        {
            var idx = i;
            // SelectableElement = a proper menu row: full-width, left-aligned label, transparent at rest,
            // accent highlight on hover (no button chrome).
            rows[2 + i] = new ConditionalElement(() => idx < _rowMenuItems.Count,
                new SelectableElement(
                    new TextElement(() => idx < _rowMenuItems.Count ? _rowMenuItems[idx].Label : ""),
                    () => InvokeRowMenuItem(idx)));
        }
        return new PanelElement(new ColumnElement(rows, Gap: 2f), Padding: RowMenuPad);
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
