using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.CombatMeter;

// Right-click row context menu — a SEPARATE popup window (not inline in the meter), positioned at the cursor,
// styled like the in-game party menu. A row's OnRightClick snapshots the registered IEntityContextMenu items
// for that entity and shows the popup there; clicking an item invokes it and dismisses. The meter owns the
// menu; plugins (EntityInspector) contribute items via IEntityContextMenu without the meter knowing them.
public sealed partial class Plugin
{
    private const int MaxRowMenuItems = 8;
    private const float RowMenuW = 200f;
    private const float RowMenuRowH = 28f;

    private IWindowControl _rowMenuWindow = null!;
    private readonly List<EntityMenuItem> _rowMenuItems = new(MaxRowMenuItems);

    private IWindowControl RegisterRowMenuWindow()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.rowmenu",
                Title:       "",
                DefaultRect: new WindowRect(100f, 100f, RowMenuW, 120f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = false, HideUntilInWorld = true },
            BuildRowMenuRoot()));

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

        // Position at the cursor. UnityEngine.Input is bottom-left origin; window rects are top-left origin.
        var mp = Input.mousePosition;
        float h = (_rowMenuItems.Count + 1) * RowMenuRowH + 12f;
        _rowMenuWindow.SetRect(new WindowRect(mp.x, Screen.height - mp.y, RowMenuW, h));
        _rowMenuWindow.SetVisible(true);
    }

    // Built once at registration; the item buttons index the snapshot, so the popup re-skins per open. Just the
    // action items + Close (no name header — matches the in-game party menu, which is a clean list of actions).
    private HudElement BuildRowMenuRoot()
    {
        var rows = new HudElement[1 + MaxRowMenuItems];
        for (var i = 0; i < MaxRowMenuItems; i++)
        {
            var idx = i;
            rows[i] = new ConditionalElement(() => idx < _rowMenuItems.Count,
                new ButtonElement(() => idx < _rowMenuItems.Count ? _rowMenuItems[idx].Label : "",
                                  () => InvokeRowMenuItem(idx)));
        }
        rows[^1] = new ButtonElement(() => "Close", CloseRowMenu);
        return new ColumnElement(rows, Gap: 2f);
    }

    private void InvokeRowMenuItem(int idx)
    {
        if (idx < 0 || idx >= _rowMenuItems.Count) return;
        var item = _rowMenuItems[idx];
        CloseRowMenu();
        item.OnClick();
    }

    private void CloseRowMenu() => _rowMenuWindow.SetVisible(false);
}
