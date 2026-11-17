using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

// Main-window per-mode geometry (List vs Party-focus rects). Split out of Plugin.cs (500-LoC cap); behaviour unchanged.
public sealed partial class Plugin
{
    // Party-focus minimum width (addendum 2026-11-17): first sized for line 1 with the four leader tools; since the "LEADER
    // line" addendum those tools sit on line 2 (Plugin.LeaderLine.cs), whose pill widths are budgeted to fit 560 too.
    // The window spec's MinWidth stays 500 for the List view (IWindowControl has no runtime min-size), so the Party view enforces its own floor here and at load (Plugin.cs).
    private const float PartyFocusMinW = 560f;

    // Capture the live window rect into the active view's remembered geometry (so a mode switch can restore it)
    // + track the current width for the List collapse breakpoints. Runs at ~10 Hz while shown (Plugin.Tick.cs), so it
    // also snaps a Party-focus window the user resized below PartyFocusMinW back to the floor (one SetRect, only then).
    private void CaptureModeGeometry()
    {
        var r = _mainWindow.Rect;
        if (r.Width <= 0f) return;
        if (_viewMode == ViewMode.PartyFocus && r.Width < PartyFocusMinW)
        {
            r = new WindowRect(r.X, r.Y, PartyFocusMinW, r.Height);
            _mainWindow.SetRect(r);
        }
        _listWidthNow = r.Width;
        if (_viewMode == ViewMode.PartyFocus) { _partyX = r.X; _partyY = r.Y; _partyW = r.Width; }
        else { _listW = r.Width; _listH = r.Height; _listX = r.X; _listY = r.Y; }
    }

    // Move/resize the window to the active view's remembered geometry (on mode switch). Party-focus is a fixed
    // structure → fixed size; List restores the user's resized size.
    private void ApplyModeSize()
    {
        var rect = _viewMode == ViewMode.PartyFocus
            ? new WindowRect(_partyX, _partyY, System.Math.Max(_partyW, PartyFocusMinW), PartyFocusHeight())
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
    private float PartyFocusHeight()
    {
        int rows = IsRaid20View ? MeterAggregator.SlotsPerGroup * 2 : MeterAggregator.SlotsPerGroup;
        float tilePx = MeterElementToggles.TileSizePx((IsRaid20View ? Party20Toggles : Party5Toggles).TileSize);
        float extraPerRow = tilePx * 2f + 8f - 48f;   // 0 at Small (48px rows), +12 Medium, +24 Large
        if (extraPerRow < 0f) extraPerRow = 0f;
        // No menu term any more: the old ▾ inline menu grew the HUD by 72 px; its contents now live in the ⋯ popover, so
        // the HUD height depends only on the party size and tile size (approved redesign §1).
        // + the LEADER line (28 + the 6 px Column gap) while it is latched on: the window grows by exactly the line, so the
        // group/rows container below keeps its height (Plugin.LeaderLine.cs).
        return (IsRaid20View ? PartyFocusH : PartyFocus5H) + rows * extraPerRow + (_leaderLine ? LeaderLineH : 0f);
    }

    // Re-apply the party-focus window height in place (keep current pos + width) when the party size changes.
    // Uses the LIVE rect, not the remembered _partyX/_partyY, so toggling a menu never teleports a
    // window the user has dragged. No-op outside party-focus (List mode reflows via its Fill scroll).
    private void RefreshPartyFocusHeight()
    {
        if (_viewMode != ViewMode.PartyFocus || _mainWindow is null) return;
        var r = _mainWindow.Rect;
        if (r.Width <= 0f) return;
        _mainWindow.SetRect(new WindowRect(r.X, r.Y, r.Width, PartyFocusHeight()));
    }
}
