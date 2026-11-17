using System;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Settings › Look › ROW › "Row size" (approved 2026-11-17, "Settings layout APPROVED"): chips Normal / Compact / Mini, per
// view (LookT.RowSize → fw MeterRowElement.Density). A Look row the chosen size doesn't draw is RECESSED (rule 22: its
// controls disabled + not clickable, the stored value kept) and its label goes muted. Live: every predicate reads LookT,
// so switching the EDITING view or the size re-greys the rows on the next refresh, with no rebuild and no height change.
// Why the greyed rows are greyed is said ONCE, in a muted note line under "Row size" (RowSizeNote) — the first build put
// a "Normal only" / "Normal and Compact only" hint beside every greyed label, which squeezed the labels to "Clas…" / "Sp…"
// in the 323 px column (user, 2026-11-17). The Effects tab's PARTY EFFECTS panel uses the same recess + note.
public sealed partial class Plugin
{
    /// <summary>Which Row sizes draw a Look setting.</summary>
    private enum SizeNeed { Any = 0, NormalOnly = 1, NotMini = 2 }

    private const SizeNeed DrawnNormalOnly = SizeNeed.NormalOnly, DrawnNotMini = SizeNeed.NotMini;

    // The note line: 12 px regular text (SmallMuted) on a FIXED-height line, ALWAYS present — empty on Normal — so switching
    // the size never moves the rows below it or changes the window height (rule 14). Counted in Plugin.SettingsWindow.cs.
    private const float RowSizeNoteH = 16f;

    private bool IsNormalSize() => LookT.RowSize == MeterRowDensity.Normal;
    private bool IsNotMini() => LookT.RowSize != MeterRowDensity.Mini;

    private Func<bool>? NeedFn(SizeNeed need) => need switch
    {
        SizeNeed.NormalOnly => IsNormalSize,
        SizeNeed.NotMini    => IsNotMini,
        _                   => null,
    };

    private HudElement LookRowSizeRow() => SettingRow(() => _loc.T("cfg.look.rowSize"), false,
        LookChoice("cfg.look.rowSize.normal",  () => LookT.RowSize == MeterRowDensity.Normal,  () => LookT.RowSize = MeterRowDensity.Normal),
        LookChoice("cfg.look.rowSize.compact", () => LookT.RowSize == MeterRowDensity.Compact, () => LookT.RowSize = MeterRowDensity.Compact),
        LookChoice("cfg.look.rowSize.mini",    () => LookT.RowSize == MeterRowDensity.Mini,    () => LookT.RowSize = MeterRowDensity.Mini),
        LookHelp("rowSize", "cfg.look.rowSize"));

    // "Greyed settings aren't shown on Compact rows." / "…on Mini rows." / "" (Normal) for the EDITED view's size. _loc.T is
    // a catalog lookup (no string building), so polling it per refresh allocates nothing.
    private HudElement RowSizeNote(string compactKey, string miniKey) => FixedLine(SmallMuted(() => LookT.RowSize switch
    {
        MeterRowDensity.Compact => _loc.T(compactKey),
        MeterRowDensity.Mini    => _loc.T(miniKey),
        _                       => "",
    }, () => MutedColor()), RowSizeNoteH);

    // SettingRow + Row-size recess: the label goes muted while the chosen size doesn't draw the setting; the controls get
    // their own `enabled` from the caller (NeedFn) so they draw the recessed fill. Any = a plain SettingRow.
    private HudElement SizeRow(Func<string> label, bool child, SizeNeed need, params HudElement[] controls)
    {
        if (need == SizeNeed.Any) return SettingRow(label, child, controls);
        var drawn = NeedFn(need)!;
        return SettingRow(label, child, () => child || !drawn(), controls);
    }
}
