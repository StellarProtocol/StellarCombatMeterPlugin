using System;
using System.IO;

namespace Stellar.CombatMeter;

// Crash-bisect switches (2026-11-17: native crash in UnityEventBase.DirtyPersistentCalls ~1 s into the first world load,
// only with the redesigned meter — KB Crash-Triage-Minidumps.md). Read ONCE at plugin construction from either
//   • the file  <game dir>\stellar.combatmeter.diag.txt   (preferred: the launcher/relay may not forward env vars), or
//   • the env var CM_DIAG,
// as a comma/space/newline-separated token list (case-insensitive). No file + no var = normal behaviour.
//   histprebuild       re-enable WindowSpec.Prebuild on Combat History (the suspected trigger: its LineChart's injected
//                      ChartGraphic then exists from the title screen on — see RegisterHistoryWindow)
//   nohistchart        build History WITHOUT the LineChart (a fixed spacer instead) — isolates the chart itself
//   nosettingsprebuild no WindowSpec.Prebuild / tab Prebuild on the merged settings window (combatmeter.config)
//   nonewicons         skip the new drawn header glyphs (↺ ⋯ ‖ ▶ archive divider) — header buttons draw blank
//   histperf           log the Combat History metric-switch recompute time ([HistPerf], Plugin.HistoryMetric.cs)
// Static and read-only after the first read: zero per-frame cost.
internal static class CmDiag
{
    public const string FileName = "stellar.combatmeter.diag.txt";

    public static readonly bool HistPrebuild, NoHistChart, NoSettingsPrebuild, NoNewIcons, HistPerf;
    public static readonly string Summary;

    static CmDiag()
    {
        string raw = "";
        try
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), FileName);
            if (File.Exists(path)) raw = File.ReadAllText(path);
        }
        catch { /* unreadable → no switches */ }
        try { raw += "," + (Environment.GetEnvironmentVariable("CM_DIAG") ?? ""); } catch { }

        foreach (var t in raw.Split(new[] { ',', ' ', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (t.Trim().ToLowerInvariant())
            {
                case "histprebuild":       HistPrebuild = true; break;
                case "nohistchart":        NoHistChart = true; break;
                case "nosettingsprebuild": NoSettingsPrebuild = true; break;
                case "nonewicons":         NoNewIcons = true; break;
                case "histperf":           HistPerf = true; break;
            }
        }
        Summary = $"histprebuild={HistPrebuild} nohistchart={NoHistChart} nosettingsprebuild={NoSettingsPrebuild} "
                + $"nonewicons={NoNewIcons} histperf={HistPerf}";
    }
}
