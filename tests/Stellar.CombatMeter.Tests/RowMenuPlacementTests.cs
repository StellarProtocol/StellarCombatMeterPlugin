using System;
using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Row context-menu placement math (Plugin.RowMenu.cs). The window overlay canvas carries a CanvasScaler whose
// scaleFactor is 1.0 ONLY at the 2560x1440 reference resolution; below "2K" screen px != canvas units. Input
// (Input.mousePosition) and IWindowElement.OnClickWithRect both report SCREEN PIXELS, but WindowRect / SetRect
// speak CANVAS UNITS — so the menu must be converted before it is positioned, or it lands offset from the click
// point at every non-2K resolution (owner report 2026-09-24: "if resolution below 2k the context menu won't
// show at click point"). These pin the conversion + the (unchanged) flip/clamp placement.
public class RowMenuPlacementTests
{
    private static void Close(float expected, float actual)
        => Assert.True(Math.Abs(expected - actual) < 0.05f, $"expected {expected}, got {actual}");

    // ---- screen px -> canvas units (THE bug) ----

    [Fact]
    public void ScreenPointToCanvas_AtReferenceResolution_IsIdentityWithYFlip()
    {
        // 2560x1440 screen == 2560x1440 canvas: scaleFactor 1.0. Only the Y axis flips (bottom-left -> top-left).
        var (x, top) = Plugin.ScreenPointToCanvas(100f, 200f, new Resolution(2560, 1440), new Resolution(2560, 1440));
        Close(100f, x);
        Close(1240f, top);   // 1440 - 200
    }

    [Fact]
    public void ScreenPointToCanvas_BelowReference_ScalesUpToCanvasUnits()
    {
        // 1920x1080 screen, 2560x1440 canvas (scaleFactor 0.75). A dead-centre click maps to canvas centre —
        // NOT the raw pixel coords the buggy code fed straight into SetRect.
        var (x, top) = Plugin.ScreenPointToCanvas(960f, 540f, new Resolution(1920, 1080), new Resolution(2560, 1440));
        Close(1280f, x);     // 960 * 2560/1920
        Close(720f, top);    // 1440 - 540 * 1440/1080
    }

    [Fact]
    public void ScreenPointToCanvas_ZeroScreen_FallsBackToRawWithYFlip()
    {
        // Defensive: before the framework has published metrics, don't divide by zero.
        var (x, top) = Plugin.ScreenPointToCanvas(50f, 60f, new Resolution(0, 0), new Resolution(2560, 1440));
        Close(50f, x);
        Close(1380f, top);   // 1440 - 60 (sfy falls back to 1)
    }

    // ---- screen-px anchor rect (OnClickWithRect) -> canvas units ----

    [Fact]
    public void ScreenRectToCanvas_AtReferenceResolution_IsIdentity()
    {
        var r = Plugin.ScreenRectToCanvas(new WindowRect(480f, 100f, 90f, 24f), 2560, 2560);
        Close(480f, r.X); Close(100f, r.Y); Close(90f, r.Width); Close(24f, r.Height);
    }

    [Fact]
    public void ScreenRectToCanvas_BelowReference_ScalesUp()
    {
        // 1920 screen wide -> 2560 canvas wide: every screen-px component grows by 2560/1920.
        var r = Plugin.ScreenRectToCanvas(new WindowRect(480f, 90f, 60f, 30f), 1920, 2560);
        Close(640f, r.X);      // 480 * 2560/1920
        Close(120f, r.Y);      // 90  * 2560/1920
        Close(80f, r.Width);   // 60  * 2560/1920
        Close(40f, r.Height);  // 30  * 2560/1920
    }

    // ---- cursor placement (canvas units) — flip/clamp preserved ----

    [Fact]
    public void RowMenuAtCursor_OpensDownRight_WhenThereIsRoom()
    {
        var r = Plugin.RowMenuAtCursor(new WindowRect(100f, 200f, 168f, 300f), 2560f, centerX: false);
        Close(100f, r.X);
        Close(200f, r.Y);
        Close(168f, r.Width);
        Close(300f, r.Height);
    }

    [Fact]
    public void RowMenuAtCursor_FlipsLeft_AtRightEdge()
    {
        var r = Plugin.RowMenuAtCursor(new WindowRect(2550f, 200f, 168f, 300f), 2560f, centerX: false);
        Close(2382f, r.X);   // 2550 + 168 > 2560 -> open left: 2550 - 168
    }

    [Fact]
    public void RowMenuAtCursor_CentersOnCursor_WhenRequested()
    {
        var r = Plugin.RowMenuAtCursor(new WindowRect(1280f, 200f, 168f, 300f), 2560f, centerX: true);
        Close(1196f, r.X);   // 1280 - 84, within [0, 2560-168]
    }

    // ---- below-anchor placement (canvas units) — flip/clamp preserved ----

    [Fact]
    public void RowMenuBelow_PlacesBelowAndCentered_WhenThereIsRoom()
    {
        var r = Plugin.RowMenuBelow(new WindowRect(500f, 40f, 60f, 20f), 168f, 100f, new Resolution(2560, 1440));
        Close(446f, r.X);   // clamp(500 + 30 - 84)
        Close(60f, r.Y);    // anchor bottom (40 + 20); 60 + 100 <= 1440
    }

    [Fact]
    public void RowMenuBelow_FlipsAbove_AtBottomEdge()
    {
        var r = Plugin.RowMenuBelow(new WindowRect(500f, 1400f, 60f, 20f), 168f, 100f, new Resolution(2560, 1440));
        Close(1300f, r.Y);   // 1420 + 100 > 1440 -> open up: 1400 - 100
    }

    // ---- end-to-end: the reported bug (1920x1080, right-click centre) ----

    [Fact]
    public void CursorMenu_BelowReference_LandsUnderTheClick_NotOffset()
    {
        // The exact scenario from the report: right-click the meter's centre on a sub-2K screen. The menu's
        // top-left must sit at the CANVAS-space cursor (1280, 720), not the raw screen pixel (960, 540).
        var (cx, top) = Plugin.ScreenPointToCanvas(960f, 540f, new Resolution(1920, 1080), new Resolution(2560, 1440));
        var r = Plugin.RowMenuAtCursor(new WindowRect(cx, top, 168f, 300f), 2560f, centerX: false);
        Close(1280f, r.X);
        Close(720f, r.Y);
    }
}
