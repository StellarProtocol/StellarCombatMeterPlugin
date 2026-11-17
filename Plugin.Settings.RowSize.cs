using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Settings › Look › ROW › "Row size" (approved 2026-11-17, "Settings layout APPROVED"): chips Normal / Compact / Mini, per
// view (LookT.RowSize → fw MeterRowElement.Density). A Look row the chosen size doesn't draw is RECESSED (rule 22: its
// controls disabled + not clickable, the stored value kept) and its label carries a muted inline hint — "Normal only"
// (Compact and Mini don't draw it) or "Normal and Compact only" (only Mini doesn't). Live: every predicate reads LookT,
// so switching the EDITING view or the size re-greys the rows on the next refresh, with no rebuild and no height change
// (the hint sits beside the label in the row's own 28 px; the weighted label cell gives it the room).
public sealed partial class Plugin
{
    /// <summary>Which Row sizes draw a Look setting.</summary>
    private enum SizeNeed { Any = 0, NormalOnly = 1, NotMini = 2 }

    private const SizeNeed DrawnNormalOnly = SizeNeed.NormalOnly, DrawnNotMini = SizeNeed.NotMini;

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

    // SettingRow + Row-size recess: [label (weighted, ellipsized; muted while recessed)] [hint, only while recessed]
    // [controls…] [?]. The controls get their own `enabled` from the caller (NeedFn) so they draw the recessed fill. The hint
    // is a Conditional without Else, so while hidden its container leaves the row (no gap). Any = a plain SettingRow.
    private HudElement SizeRow(Func<string> label, bool child, SizeNeed need, params HudElement[] controls)
    {
        if (need == SizeNeed.Any) return SettingRow(label, child, controls);
        var drawn = NeedFn(need)!;
        string hintKey = need == SizeNeed.NormalOnly ? "cfg.look.normalOnly" : "cfg.look.normalCompactOnly";
        var items = new List<HudElement>(controls.Length + 4) { new SpacerElement(0f, SettingRowH) };
        if (child) items.Add(new SpacerElement(ChildIndent - 6f, 1f));
        items.Add(new CellElement(new TextElement(label, Color: () => child || !drawn() ? MutedColor() : TextColor(), NoWrap: true)
            { Ellipsize = true }, Weight: 1f));
        items.Add(new ConditionalElement(() => !drawn(),
            new TextElement(() => _loc.T(hintKey), Color: () => MutedColor(), NoWrap: true, FontSize: 11)));
        items.AddRange(controls);
        return new RowElement(items, Gap: 6f);
    }
}
