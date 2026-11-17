using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// House "open centred" (Stellar Design Language §5; port of StellarMultiToolPlugin WindowCenterer.cs). OpenCentred():
// show → front → re-centre on a target point (the opener's current rect, or the screen centre) for a short bounded window
// of plugin ticks, then stop by itself.
//
// Manual centre, NOT WindowAnchor.Center: the anchor centres on the DECLARED DefaultRect height. Centre on the window's
// actual laid-out Rect instead, in CANVAS units (IFramework.CanvasWidth/Height — ScreenWidth is physical px and is off by
// the UI scaleFactor away from native res). WindowBuilder-Patterns.md §"Centering a plugin window correctly".
//
// Why a short per-tick window instead of one SetRect: right after SetVisible(true) the rect can be 0 / stale (not laid
// out yet; on the FIRST open IsShown stays false until the framework mounts it, a band later). So keep re-centring while
// the target moves and stop once it has held StableTicks (or the budget runs out). Bounded on purpose: SetRect
// auto-persists, so it must not run for the window's lifetime. It also stops at the first move it didn't make (a user
// drag), or it would fight that drag (SKILL "Behaviour").
internal sealed class WindowCenterer
{
    private const int Budget      = 30;   // give up after this many ticks (layout normally settles in 1–3)
    private const int StableTicks = 3;    // done once the centred rect has held this many ticks

    private readonly IFramework     _fw;
    private readonly IWindowControl _win;
    private Func<float, float, (float X, float Y)?> _target = CanvasCentre;

    private int   _ticks, _held;
    private float _lastX = float.NaN, _lastY = float.NaN, _lastW, _lastH;
    private bool  _subscribed;
    private bool  _seenShown;   // IsShown has been true at least once since Begin() (i.e. the window has mounted)

    public WindowCenterer(IFramework fw, IWindowControl win) { _fw = fw; _win = win; }

    // Screen (canvas) centre — the default target.
    public static (float X, float Y)? CanvasCentre(float cw, float ch) => (cw / 2f, ch / 2f);

    // Centre of `parent`'s CURRENT rect (canvas units), or null while it isn't shown / laid out so the centerer keeps
    // trying. For a child window that opens centred on the window it was opened from.
    public static Func<float, float, (float X, float Y)?> CentreOf(IWindowControl parent) => (_, _) =>
    {
        if (!parent.IsShown) return null;
        var r = parent.Rect;
        if (r.Width <= 1f || r.Height <= 1f) return null;
        return (r.X + r.Width / 2f, r.Y + r.Height / 2f);
    };

    // THE way a CombatMeter child window opens: show it, front it, then re-centre on `opener` (null = screen centre).
    // Fronting every open is the house habit (harmless on fw ≥2.30 non-sticky stacking; CLAUDE.md Rule 5 exception for a
    // child re-opened while already visible).
    public void OpenCentred(IWindowControl? opener)
    {
        _target = opener != null && opener.IsShown ? CentreOf(opener) : CanvasCentre;
        _win.SetVisible(true);
        _win.BringToFront();
        _ticks = Budget;
        _held  = 0;
        _lastX = _lastY = float.NaN;   // force a centre even if the rect matches the previous open
        _seenShown = false;
        if (!_subscribed) { _fw.Update += OnTick; _subscribed = true; }
    }

    public void Stop()
    {
        if (_subscribed) { try { _fw.Update -= OnTick; } catch { /* never throw from a stop path */ } }
        _subscribed = false;
        _ticks = 0;
    }

    private void OnTick(float _)
    {
        if (--_ticks < 0) { Stop(); return; }
        // First open: not mounted yet → wait (within the budget); once seen shown, "not shown" = closed mid-settle.
        if (!_win.IsShown) { if (_seenShown) Stop(); return; }
        _seenShown = true;

        var r = _win.Rect;
        if (r.Width <= 1f || r.Height <= 1f) return;                     // not laid out yet
        // A position we did not set = the user dragged it → leave it alone (1 unit of slack for the renderer's snapping).
        if (!float.IsNaN(_lastX) && (MathF.Abs(r.X - _lastX) > 1f || MathF.Abs(r.Y - _lastY) > 1f)) { Stop(); return; }
        float cw = _fw.CanvasWidth, ch = _fw.CanvasHeight;
        if (cw <= 1f || ch <= 1f) return;                                // canvas metrics not ready
        if (_target(cw, ch) is not { } c) return;                        // target not resolvable yet

        // Clamp to the canvas: low bound last so a window larger than the canvas pins to the top-left.
        float x = MathF.Max(0f, MathF.Min(c.X - r.Width  / 2f, cw - r.Width));
        float y = MathF.Max(0f, MathF.Min(c.Y - r.Height / 2f, ch - r.Height));

        if (x == _lastX && y == _lastY && r.Width == _lastW && r.Height == _lastH)
        {
            if (++_held >= StableTicks) Stop();
            return;
        }
        _win.SetRect(new WindowRect(x, y, r.Width, r.Height));
        _lastX = x; _lastY = y; _lastW = r.Width; _lastH = r.Height;
        _held = 0;
    }
}
