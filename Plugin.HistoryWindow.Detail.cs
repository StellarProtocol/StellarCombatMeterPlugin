using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;   // UploadPhase, UploadPolicy

namespace Stellar.CombatMeter;

// Combat History — the right column (approved redesign §4): three SectionPanels, top to bottom:
//   • run panel: map title + muted "date · duration · players" caption; Primary upload pill (FIXED width across all its
//     labels) · Neutral "Upload all (N)" · muted run id / why-not status · Neutral "Copy link" · "Delete" (inline confirm);
//     then the SEGMENTS chips (readable reason names);
//   • chart panel: DPS/HPS/Taken segmented control + muted hint, the LineChart + brush;
//   • player table: muted UPPERCASE header, 34 px tiles (class-colour share wash, crest, bold "#1 Name" + muted spec,
//     numbers, round ⌕ inspect, round › drill-in; a charted row = accent ring).
// Every string a Func reads is cached (rebuilt on selection / metric / language change, Plugin.HistoryWindow.Runs.cs).
public sealed partial class Plugin
{
    private bool _deleteRunConfirm;
    private string _runTitle = "", _runMeta = "";
    private readonly string[] _segLabels = new string[MaxSegmentChips];

    private HudElement BuildHistoryDetail() => new ColumnElement(new HudElement[]
    {
        BuildRunPanel(),
        BuildChartPanel(),
        BuildTablePanel(),
    }, Gap: HistGap);

    // ── run panel ─────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildRunPanel() => PanelBox(new HudElement[]
    {
        // Custom caption: the map name (bold 14, not uppercased — it's a name) + the muted run meta on the right.
        new RowElement(new HudElement[]
        {
            new SpacerElement(1f, CaptionHeight),
            new CellElement(new TextElement(() => _runTitle, Color: () => TextColor(), NoWrap: true, FontSize: 14)
                { Bold = true, Ellipsize = true }, Weight: 1f),
            new TextElement(() => _runMeta, Color: () => MutedColor(), NoWrap: true),
        }, Gap: 6f),
        // The pill row and its delete confirm are the same 28 px row, so confirming never moves the panels below.
        new ConditionalElement(() => _deleteRunConfirm, DeleteRunConfirmRow(), UploadPillRow()),
        BuildSegmentPicker(),
    });

    // Selection-dependent header strings, the segment chip labels and the upload block — on selection / metric /
    // language change only (called from RebuildSessionRows).
    private void RebuildRunHeader()
    {
        Array.Fill(_segLabels, "");
        RefreshUploadBlock();
        if (_selectedSession is not { } h)
        {
            _runTitle = _history.Count == 0 ? "" : _loc.T("history.pickRun");
            _runMeta = "";
            return;
        }
        _runTitle = ResolveSceneName(h.SceneName);
        var when = h.ArchivedAtMs <= 0 ? "—"
            : DateTimeOffset.FromUnixTimeMilliseconds(h.ArchivedAtMs).LocalDateTime
                .ToString(_loc.T("history.date.format"), CultureInfo.InvariantCulture);
        var dur = FormatRowDuration(RealDurationMs(h.EnteredAtMs, h.ArchivedAtMs), h.CombatDurationMs, _loc.T("history.duration.detail"));
        _runMeta = when + " · " + dur + " · " + _loc.TFormat("history.players", h.MemberCount);
        for (int i = 0; i < _selectedSegments.Length && i < MaxSegmentChips; i++)
        {
            int idx = _selectedSegments[i];
            _segLabels[i] = idx >= 0 && idx < _history.Count ? SegmentLabel(_history[idx].Trigger) : "";
        }
    }

    // ── upload ────────────────────────────────────────────────────────────────────────────────────────────────────
    // Why the selected archive can't be uploaded right now → the Primary pill is RECESSED (rule 22) and the muted status
    // says why. Re-evaluated on the ~10 Hz snapshot tick (the policy can change in Settings while History is open).
    private enum UploadBlock { None, NoRunId, OldRun, Update, Off }
    private UploadBlock _uploadBlock;

    private void RefreshUploadBlock()
    {
        if (_selectedSession is not { } s) { _uploadBlock = UploadBlock.None; return; }
        // Same gates, same order, as UploadHistoryEntry: below the server floor, an Outdated run (its baked-in old
        // pluginVer is 426'd forever), the content's stats cell off, a pre-run-id archive (/run/0 would collide).
        _uploadBlock = UploadBelowFloor ? UploadBlock.Update
            : UploadStateFor(s) == UploadPhase.Outdated ? UploadBlock.OldRun
            : !UploadPolicy.Allows(EffectivePolicyFor(s, UploadArtifact.Stats), UploadTrigger.Manual) ? UploadBlock.Off
            : s.LevelUuid == 0 ? UploadBlock.NoRunId
            : UploadBlock.None;
    }

    private bool CanUploadSelected() => _selectedSession != null && _uploadBlock == UploadBlock.None;

    private HudElement UploadPillRow()
    {
        // Fixed widths = the longest label each pill can show in this language (rebuilt on a language change), so a label
        // change never moves anything (R10).
        float upW = MaxBoldLabelW(13, _loc.T("history.upload.segment"), _loc.T("history.upload.thisRun"),
            _loc.T("history.upload.uploading"), _loc.T("history.upload.done"), _loc.T("history.upload.retry"));
        float allW = MaxBoldLabelW(13, _loc.TFormat("history.upload.all", MaxSegmentChips),
            _loc.TFormat("upload.uploadingLeft", MaxSegmentChips));
        float copyW = MaxBoldLabelW(13, _loc.T("history.copyLink"), _loc.T("history.copied"));
        float delW = MaxBoldLabelW(13, _loc.T("history.delete"));
        return new RowElement(new HudElement[]
        {
            new SpacerElement(1f, PillHeight),
            PrimaryButton(UploadPillLabel, UploadSelectedClicked, upW, CanUploadSelected),
            new CellElement(new ConditionalElement(() => _selectedSegments.Length > 1,
                NeutralButton(UploadAllLabel, QueueRunUpload, allW,
                    () => _runUploadQueue.Count == 0 && !UploadBelowFloor && _uploadBlock != UploadBlock.Off))),
            new CellElement(new TextElement(UploadStatusText, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true },
                Weight: 1f),
            NeutralButton(CopyLinkLabel, CopyUploadLink, copyW, HasUploadLink),
            PillButton(() => _loc.T("history.delete"), NeutralFill, () => Readable(ProblemText, NeutralFill()),
                () => _deleteRunConfirm = true, delW, () => _selectedSession != null),
        }, Gap: 6f);
    }

    private string UploadPillLabel()
    {
        if (_selectedSession is not { } s || _uploadBlock != UploadBlock.None) return SegmentUploadVerb();
        return UploadStateFor(s) switch
        {
            UploadPhase.InFlight => _loc.T("history.upload.uploading"),
            UploadPhase.Done     => _loc.T("history.upload.done"),
            UploadPhase.Failed   => _loc.T("history.upload.retry"),
            _                    => SegmentUploadVerb(),
        };
    }

    private readonly FmtCache _uploadAllFmt = new();

    private string UploadAllLabel() => _runUploadQueue.Count > 0
        ? _uploadAllFmt.Get(_loc.T("upload.uploadingLeft"), _runUploadQueue.Count)
        : _uploadAllFmt.Get(_loc.T("history.upload.all"), _selectedSegments.Length);

    private string? _statusUrl;
    private string _statusShort = "";

    private string UploadStatusText()
    {
        if (_selectedSession is not { } s) return "";
        switch (_uploadBlock)
        {
            case UploadBlock.NoRunId: return _loc.T("history.upload.status.noRunId");
            case UploadBlock.OldRun:  return _loc.T("history.upload.status.oldRun");
            case UploadBlock.Update:  return _loc.T("history.upload.status.update");
            case UploadBlock.Off:     return _loc.T("history.upload.status.off");
        }
        var phase = UploadStateFor(s);
        if (phase == UploadPhase.Failed) return _loc.T("history.upload.status.failed");
        if (phase != UploadPhase.Done || UploadUrlFor(s) is not { } u) return "";
        // "sea/HpPqOu76Bh" rather than the whole URL (ShortRunLabel); Copy link beside it yields the full link.
        if (!ReferenceEquals(u, _statusUrl)) { _statusUrl = u; _statusShort = ShortRunLabel(u); }
        return _statusShort;
    }

    private void UploadSelectedClicked()
    {
        if (_selectedSession is { } s && UploadStateFor(s) != UploadPhase.InFlight) UploadHistoryEntry(s);
    }

    private bool HasUploadLink() => _selectedSession is { } s && UploadStateFor(s) == UploadPhase.Done && UploadUrlFor(s) != null;

    // Feedback where you clicked (R13): "✓ Copied" for a moment, same pill width.
    private const double CopiedFlashS = 1.5;
    private double _copiedAt = -10.0;

    private string CopyLinkLabel() => _services.Framework.TimeNow - _copiedAt < CopiedFlashS
        ? _loc.T("history.copied") : _loc.T("history.copyLink");

    private void CopyUploadLink()
    {
        if (_selectedSession is not { } s || UploadUrlFor(s) is not { } u) return;
        UnityEngine.GUIUtility.systemCopyBuffer = u;   // plugins may reference UnityEngine; IL2CPP-safe clipboard
        _copiedAt = _services.Framework.TimeNow;
    }

    // ── delete (one archive = the selected segment) ───────────────────────────────────────────────────────────────
    private HudElement DeleteRunConfirmRow()
    {
        float w = MaxBoldLabelW(13, _loc.T("history.delete.confirm"), _loc.T("cfg.cancel"));
        return new RowElement(new HudElement[]
        {
            new SpacerElement(1f, PillHeight),
            new TextElement(() => _loc.T(_selectedSegments.Length > 1 ? "history.delete.askSegment" : "history.delete.askRun"),
                Color: () => TextColor(), NoWrap: true, FontSize: 13) { Bold = true },
            new CellElement(new TextElement(() => _loc.T("history.delete.consequence"), Color: () => MutedColor(), NoWrap: true)
                { Ellipsize = true }, Weight: 1f),
            DangerButton(() => _loc.T("history.delete.confirm"), DeleteSelectedSession, w),
            NeutralButton(() => _loc.T("cfg.cancel"), () => _deleteRunConfirm = false, w),
        }, Gap: 6f);
    }

    private void DeleteSelectedSession()
    {
        _deleteRunConfirm = false;
        if (_historyIndex >= 0) DeleteSession(_historyIndex);
    }

    // ── chart panel ───────────────────────────────────────────────────────────────────────────────────────────────
    private Metric _chartHintMetric = (Metric)(-1);
    private string? _chartHintLang;
    private string _chartHint = "";

    private HudElement BuildChartPanel() => PanelBox(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new CellElement(BuildHistoryMetricTabs(), Weight: 1f),
            new TextElement(ChartHint, Color: () => MutedColor(), NoWrap: true),
        }, Gap: 6f),
        // `nohistchart` diag switch (Plugin.Diag.cs): same height, no injected ChartGraphic.
        CmDiag.NoHistChart ? new SpacerElement(1f, 150f) : BuildHistoryChart(),
    });

    // "Damage / sec · click a player to add their line" — composed once per metric / language.
    private string ChartHint()
    {
        string lang = _loc.Language;
        if (_historyMetric == _chartHintMetric && ReferenceEquals(lang, _chartHintLang)) return _chartHint;
        _chartHintMetric = _historyMetric; _chartHintLang = lang;
        return _chartHint = MetricAxisTitle(_historyMetric) + " · " + _loc.T("history.chart.hint");
    }

    // The timeline chart: team-total (always) + a line per source toggled into _chartedSources. Axis scale + Y title
    // follow _historyMetric; rebuilt (not refreshed) on metric change so the baked axis rescales.
    private HudElement BuildHistoryChart() => new LineChartElement(
        Series:          BuildChartSeries,
        BucketSeconds:   () => _selectedSession is { } h ? SeriesBucketSeconds(h) : 1f,
        FormatY:         v => FormatAmount((long)v),
        FormatX:         FormatChartX,
        TitleY:          () => MetricAxisTitle(_historyMetric),
        TitleX:          () => _loc.T("history.chart.xTitle"),
        VisibleRange:    () => _chartVisibleRange,
        SetVisibleRange: r => _chartVisibleRange = r,
        Width:           HistDetailW - 2f * PanelPadX,
        Height:          150f,
        ShowNavigator:   true,    // Highcharts-style overview + brush
        FillWidth:       true);

    private static string FormatSeconds(float s)
    {
        var total = (int)(s < 0f ? 0f : s);
        return $"{total / 60}:{total % 60:00}";
    }

    // Span-aware X tick label: short encounters get sub-second / integer-second precision so the few visible ticks stay
    // distinct (a short span otherwise collapses every tick to "0:00"); longer ranges fall back to the m:ss clock.
    private string FormatChartX(float v)
    {
        var span = _chartVisibleRange.Max - _chartVisibleRange.Min;
        if (span < 3f) return $"{v:0.0}s";
        if (span < 60f) return $"{(int)(v < 0f ? 0f : v)}s";
        return FormatSeconds(v);
    }

    // ── label width estimate ──────────────────────────────────────────────────────────────────────────────────────
    // Drawn width of a BOLD label at `fontSize` (SDL §6 budget at 12: Latin ≈ 7.5 px/char, CJK ≈ 12.5, Thai ≈ 8 per code
    // point; scaled linearly), + 4 px slack. Build time only — pill widths are fixed from it, never per refresh.
    private static float EstimateBoldLabelW(string label, int fontSize)
    {
        float w = 0f;
        foreach (char c in label) w += c >= 0x2E80 ? 12.5f : c is >= '฀' and <= '๿' ? 8f : 7.5f;
        return MathF.Ceiling(w * fontSize / 12f) + 4f;
    }

    private static float MaxBoldLabelW(int fontSize, params string[] labels)
    {
        float w = 0f;
        foreach (var l in labels) w = MathF.Max(w, EstimateBoldLabelW(l, fontSize));
        return w;
    }
}
