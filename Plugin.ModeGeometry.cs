using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Main-window per-mode geometry (List vs Party-focus rects). Split out of Plugin.cs (500-LoC cap); behaviour unchanged.
public sealed partial class Plugin
{
    // Party-focus minimum width (addendum 2026-11-17): first sized for line 1 with the four leader tools; since the "LEADER
    // line" addendum those tools sit on line 2 (Plugin.LeaderLine.cs), whose pill widths are budgeted to fit 560 too.
    private const float PartyFocusMinW = 560f;

    // Resize limits per view (user 2026-11-17: "height should not be resizable in party mode, only for list mode").
    // List: free resize in both axes. Party: width only — the height is computed from the party layout (PartyFocusHeight),
    // so it is locked by min == max. The spec carries the List limits; fw 2.33.0 IWindowSizeLimits swaps them at runtime
    // for the grip (normal + layout edit), every SetRect and the saved-layout restore, so the Party width floor needs no
    // 10 Hz snap-back any more and a stale saved Party height is corrected on load. A size outside new limits is clamped
    // in place (top-left fixed). Idempotent in the framework, but only called on a view switch / party-height change.
    private const float ListMinW = 500f, ListMinH = 160f, ListMaxH = 1000f, MeterMaxW = 760f;

    private void ApplyViewSizeLimits()
    {
        if (_viewMode == ViewMode.PartyFocus)
        {
            var h = PartyFocusHeight();
            _mainWindow.SetSizeLimits(PartyFocusMinW, h, MeterMaxW, h);
        }
        else _mainWindow.SetSizeLimits(ListMinW, ListMinH, MeterMaxW, ListMaxH);
    }

    // Capture the live window rect into the active view's remembered geometry (so a mode switch can restore it)
    // + track the current width for the List collapse breakpoints. Runs at ~10 Hz while shown (Plugin.Tick.cs).
    private void CaptureModeGeometry()
    {
        if (!_geomSettled) return;   // pre-restore rect = the framework's (20,20) mount placeholder — never remember it
        var r = _mainWindow.Rect;
        if (r.Width <= 0f) return;
        _listWidthNow = r.Width;
        if (_viewMode == ViewMode.PartyFocus) { _partyX = r.X; _partyY = r.Y; _partyW = r.Width; }
        else { _listW = r.Width; _listH = r.Height; _listX = r.X; _listY = r.Y; }
    }

    // Move/resize the window to the active view's remembered geometry (on mode switch). Party-focus is a fixed
    // structure → fixed size; List restores the user's resized size.
    private void ApplyModeSize()
    {
        ApplyViewSizeLimits();   // FIRST: the leaving view's limits would clamp this SetRect (Party's locked height vs List's)
        var rect = _viewMode == ViewMode.PartyFocus
            ? new WindowRect(_partyX, _partyY, Math.Max(_partyW, PartyFocusMinW), PartyFocusHeight())
            : new WindowRect(_listX, _listY, _listW > 0 ? _listW : ListWidth, _listH > 0 ? _listH : ListHeight);
        _mainWindow.SetRect(rect);
    }

    // Party-focus window height: base grid height (20-player 4×5 vs 5-player single group), SCALED for the
    // current player-tile size (so the grid is never squeezed). Follows the live party
    // size; RefreshPartyFocusHeight re-applies it. The per-row growth mirrors the framework row-height formula
    // (max(MeterRowHeight 48, DebuffBlockPx + 6); DebuffBlockPx = tilePx*2 + gap 2) so a Medium/Large tile
    // auto-fits — owner 2026-09-24: a resized party window "wasn't remembered" because this height was a fixed
    // constant that ignored the tile size, so every mode switch / menu toggle snapped it back and clipped the
    // enlarged grid. Vertical rows: 20-player stacks two 5-slot quadrants (10), 5-player is one group (5).
    // Row heights of fw MeterRowElement (Normal 48, Density Compact 24 / Mini 17 — fixed in the framework).
    private const float NormalRowH = 48f, CompactRowH = 24f, MiniRowH = 17f;

    private float PartyFocusHeight()
    {
        int rows = IsRaid20View ? MeterAggregator.SlotsPerGroup * 2 : MeterAggregator.SlotsPerGroup;
        var toggles = IsRaid20View ? Party20Toggles : Party5Toggles;
        // Row size (fw MeterRowElement.Density): Compact 24 / Mini 17 px rows with a 2 px gap instead of 48 + 3. Every group
        // column has 5 gaps (caption→row 1 + 4 between rows), so a stacked group also saves 5 × (3 − 2). The player-tile
        // size grows only Normal rows (Compact / Mini have a fixed height).
        float extraPerRow;
        if (toggles.RowSize != MeterRowDensity.Normal)
        {
            float rowH = toggles.RowSize == MeterRowDensity.Compact ? CompactRowH : MiniRowH;
            int stackedGroups = rows / MeterAggregator.SlotsPerGroup;
            extraPerRow = rowH - NormalRowH - stackedGroups * MeterAggregator.SlotsPerGroup * (MeterRowGap - CompactRowGap) / rows;
        }
        else
        {
            float tilePx = MeterElementToggles.TileSizePx(toggles.TileSize);
            extraPerRow = tilePx * 2f + 8f - NormalRowH;   // 0 at Small (48px rows), +12 Medium, +24 Large
            if (extraPerRow < 0f) extraPerRow = 0f;
        }
        // No menu term any more: the old ▾ inline menu grew the HUD by 72 px; its contents now live in the ⋯ popover, so
        // the HUD height depends only on the party size and tile size (approved redesign §1).
        // + the RESERVED LEADER-line slot (28 + the 6 px Column gap), always — leader or not (Plugin.LeaderLine.cs), so the
        // group/rows container below keeps its pre-line height and leadership never resizes the window.
        // 5-player Small/Medium/Large = 368/428/488, 20-player = 646/766/886.
        return (IsRaid20View ? PartyFocusH : PartyFocus5H) + rows * extraPerRow + LeaderLineH;
    }

    // First SETTLED Party tick of the session re-fits the height once: the framework restores the rect it saved last, which
    // may predate the reserved slot (34 px short) or a tile-size change. Only after the settle gate below — a re-fit before
    // it persisted the (20,20) mount placeholder (2026-11-17 top-left bug). (The locked-height limits already clamp the
    // restore; this SetRect is what persists the corrected rect.)
    private bool _partyHeightSynced;
    private float _syncedPartyH;   // the PartyFocusHeight() last applied (limits + rect)

    // ~10 Hz from RebuildSnapshots (Party view, geometry settled): limits + ONE height-only SetRect when the first sync is
    // owed or the computed height changed (5↔20 switch, tile size). Held while the user is in layout edit-mode, so a
    // resize never lands under a drag; the owed re-fit applies on the first tick after they leave it.
    private void SyncPartyFocusHeight()
    {
        float h = PartyFocusHeight();
        if (_partyHeightSynced && Math.Abs(h - _syncedPartyH) < 0.5f) return;
        if (_services.Windows.IsLayoutEditing) return;
        _partyHeightSynced = true;
        _syncedPartyH = h;
        RefreshPartyFocusHeight();
    }

    // Re-apply the party-focus window height in place (keep current pos + width) when the party size changes.
    // Uses the LIVE rect, not the remembered _partyX/_partyY, so toggling a menu never teleports a
    // window the user has dragged. No-op outside party-focus (List mode reflows via its Fill scroll).
    private void RefreshPartyFocusHeight()
    {
        if (_viewMode != ViewMode.PartyFocus || _mainWindow is null || !_geomSettled) return;
        ApplyViewSizeLimits();   // the new locked height first, or the SetRect below is clamped to the old one
        var r = _mainWindow.Rect;
        if (r.Width <= 0f) return;
        _mainWindow.SetRect(new WindowRect(r.X, r.Y, r.Width, PartyFocusHeight()));   // height only; live X/Y/W kept
    }

    // ---- Layout-settle gate (bug 2026-11-17: the meter jumped to the top-left corner after e501e99) ----
    // The framework mounts a window at a (20,20) PLACEHOLDER (UGuiPrimitives: "default until SetRect restores") and, on a
    // fresh canvas (login / scene change), PARKS the saved-layout apply until the CanvasScaler settles (WindowService
    // ApplySavedRect → ApplyPending; on the session's first mount there is no last-good rect to hold either). IsShown is
    // already true in that gap, so Rect reads (20,20). e501e99's "first Party tick re-fits the height once" SetRect'd that
    // rect — and Entry.SetRect AUTO-PERSISTS, so (20,20) became the saved layout and the deferred apply then restored it.
    // IWindowControl has no "layout applied" signal, so: touch the rect (read OR write) only once the window has been shown
    // for GeometrySettleTicks consecutive ~10 Hz ticks with an unchanged rect (the parked apply lands one frame after the
    // canvas settles, so a placeholder never survives that). Reset on every hide, so each re-mount waits again.
    private const int GeometrySettleTicks = 3;
    private int _geomStableTicks;
    private WindowRect _geomLastRect;
    private bool _geomSettled;
    private bool _geomRecoveryChecked;   // once per session: undo a (20,20) the bug persisted (see RecoverPlaceholderPosition)

    // ~10 Hz while shown (Plugin.Tick.cs). True once the rect is trustworthy; the caller then captures/re-fits as before.
    private bool MainGeometrySettled()
    {
        if (_geomSettled) return true;
        var r = _mainWindow.Rect;
        if (r.Width <= 0f) { _geomStableTicks = 0; return false; }
        _geomStableTicks = _geomStableTicks > 0 && SameRect(r, _geomLastRect) ? _geomStableTicks + 1 : 1;
        _geomLastRect = r;
        if (_geomStableTicks < GeometrySettleTicks) return false;
        _geomSettled = true;
        if (!_geomRecoveryChecked) { _geomRecoveryChecked = true; RecoverPlaceholderPosition(r); }
        return true;
    }

    private void ResetGeometrySettle() { _geomSettled = false; _geomStableTicks = 0; }

    // One-time recovery for users the bug already hit: the framework layout now SAYS (20,20), so it restores there on every
    // launch. If the settled live position is exactly that placeholder AND the plugin's own prefs (loaded at boot, never
    // overwritten before settle — CaptureModeGeometry is gated) hold a different spot, move back there (position only; the
    // size stays the live one). Self-limiting: a user who really keeps it at (20,20) gets that captured into prefs on the
    // next save, after which prefs match and nothing is moved.
    private void RecoverPlaceholderPosition(WindowRect r)
    {
        if (!IsMountPlaceholder(r)) return;
        bool party = _viewMode == ViewMode.PartyFocus;
        float px = party ? _partyX : _listX, py = party ? _partyY : _listY;
        if (IsMountPlaceholder(new WindowRect(px, py, 0f, 0f))) return;
        _mainWindow.SetRect(new WindowRect(px, py, r.Width, r.Height));   // persists the recovered spot (Entry.SetRect)
        _services.Log.Info($"[CombatMeter] Restored the meter position ({px:0},{py:0}) from a top-left placeholder layout.");
    }

    private static bool IsMountPlaceholder(WindowRect r) => Math.Abs(r.X - 20f) < 0.5f && Math.Abs(r.Y - 20f) < 0.5f;

    private static bool SameRect(WindowRect a, WindowRect b)
        => Math.Abs(a.X - b.X) < 0.5f && Math.Abs(a.Y - b.Y) < 0.5f
        && Math.Abs(a.Width - b.Width) < 0.5f && Math.Abs(a.Height - b.Height) < 0.5f;
}
