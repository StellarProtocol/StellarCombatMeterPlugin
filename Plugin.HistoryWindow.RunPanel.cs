using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;   // UploadPhase

namespace Stellar.CombatMeter;

// Combat History run panel — approved addendum "run panel B" (2026-11-17,
// _scratch\prototypes\combatmeter-redesign-APPROVED.md):
//   row 1  bold map title · muted "date · N players · M segments"
//   row 2  status TEXT (green "✓ Uploaded" / amber "Uploading…" / muted "Not uploaded") + muted " · segment N · sea/<id>",
//          or — when the selected segment can be uploaded and isn't — the Primary "Upload segment" pill in its place;
//          then Neutral "Upload all (N)" (recessed once every segment is uploaded), round Copy link and Delete (red,
//          still confirms) icon buttons. "Uploaded" is never a pill (rule 22: status that isn't clickable is plain text).
//   then the SEGMENTS cards (Plugin.HistoryWindow.Segments.cs).
//
// Performance: the upload phases come from a ConcurrentDictionary written by the upload callback, so they are polled —
// RefreshRunUploadState runs on the ~10 Hz snapshot tick (≤ 24 lookups, no allocation) and re-composes the status strings
// only when a phase, the run URL or the upload block actually changed. Every Func below reads a cached field.
public sealed partial class Plugin
{
    private bool _deleteRunConfirm;

    // Cached upload state of the selected run (see RefreshRunUploadState).
    private readonly UploadPhase[] _segPhases = new UploadPhase[MaxSegmentChips];
    private UploadPhase _selPhase = (UploadPhase)(-1);
    private string? _selUrl;
    private UploadBlock _statusBlock = (UploadBlock)(-1);
    private bool _allUploaded;
    private enum RunStatus { None, Uploaded, Uploading, NotUploaded, Pill }
    private RunStatus _runStatus;
    private string _statusWord = "", _statusTail = "";

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
        // The status row and its delete confirm are the same 28 px row, so confirming never moves the panels below.
        new ConditionalElement(() => _deleteRunConfirm, DeleteRunConfirmRow(), RunStatusRow()),
        BuildSegmentArea(),
    });

    private HudElement RunStatusRow()
    {
        // Fixed widths = the longest label each pill can show in this language (rebuilt on a language change), so a label
        // change never moves anything (R10).
        float upW = MaxBoldLabelW(13, _loc.T("history.upload.segment"), _loc.T("history.upload.thisRun"),
            _loc.T("history.upload.retry"));
        float allW = MaxBoldLabelW(13, _loc.TFormat("history.upload.all", MaxSegmentChips),
            _loc.TFormat("upload.uploadingLeft", MaxSegmentChips));
        return new RowElement(new HudElement[]
        {
            new SpacerElement(1f, PillHeight),
            new ConditionalElement(() => _runStatus == RunStatus.Pill,
                PrimaryButton(UploadPillLabel, UploadSelectedClicked, upW, CanUploadSelected),
                new TextElement(() => _statusWord, Color: StatusWordColor, NoWrap: true, FontSize: 13) { Bold = true }),
            new CellElement(new TextElement(() => _statusTail, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true },
                Weight: 1f),
            new CellElement(new ConditionalElement(() => _selectedSegments.Length > 1,
                NeutralButton(UploadAllLabel, QueueRunUpload, allW,
                    () => _runUploadQueue.Count == 0 && !_allUploaded && !UploadBelowFloor && _uploadBlock != UploadBlock.Off))),
            RunIconButton(() => CopiedFlashing() ? _checkmarkPng : _linkPng,
                hover => CopiedFlashing() ? Readable(SuccessColor(), NeutralFill()) : hover ? TextColor() : MutedColor(),
                danger: false, CopyUploadLink, () => _selUrl != null),
            RunIconButton(() => _trashPng, _ => Readable(ProblemText, NeutralFill()),
                danger: true, () => _deleteRunConfirm = true, () => _selectedSession != null),
        }, Gap: 6f);
    }

    private ColorRgba? StatusWordColor() => _runStatus switch
    {
        RunStatus.Uploaded  => SuccessText(),
        RunStatus.Uploading => CautionTextOnPanel(),
        _                   => MutedColor(),
    };

    // A round 28 × 28 icon button for a framed window, next to pills: it carries the Neutral pill fill at rest
    // (pressable = has a fill, rule 22 — the house IconButton is clear at rest, which beside pills reads as disabled),
    // hover = Text@0.10 over it, or the house red Remove wash for a destructive one; disabled = recessed + faint glyph.
    // The glyph is a flat-white PNG (Plugin.HudIcons.History.cs) tinted by `glyph(hover)`; Live so the ✓ flash swaps it
    // (byte[]-reference poll — the Funcs return cached arrays only).
    private HudElement RunIconButton(Func<byte[]?> png, Func<bool, ColorRgba> glyph, bool danger, Action onClick,
        Func<bool> enabled)
    {
        var hover = new HoverFlag();
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, IconButtonSize),
                new ImageElement(png, (int)HudIconPx, (int)HudIconPx)
                    { Live = true, Tint = () => enabled() ? glyph(hover.On) : DisabledLabel(PanelColor()) },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (enabled()) onClick(); }) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(!enabled() ? DisabledFill(PanelColor())
                : !hover.On ? NeutralFill()
                : danger ? Over(RemoveWash, NeutralFill()) : HoverFill(NeutralFill())),
            CornerRadius = RoundRadius(IconButtonSize), PaddingX = (IconButtonSize - HudIconPx) / 2f, PaddingY = 0f,
            MaxWidth = IconButtonSize,
        }, Width: IconButtonSize);
    }

    // ── cached upload state ──────────────────────────────────────────────────────────────────────────────────────
    // Polled from RebuildHistorySnapshots (~10 Hz while History is shown) and forced by RebuildRunHeader on a selection /
    // language change. Re-composes the strings only when something changed.
    private void RefreshRunUploadState(bool force)
    {
        bool changed = force;
        int n = Math.Min(_selectedSegments.Length, MaxSegmentChips);
        bool all = n > 0;
        for (int i = 0; i < MaxSegmentChips; i++)
        {
            var ph = UploadPhase.Idle;
            if (i < n && _selectedSegments[i] is var idx && idx >= 0 && idx < _history.Count) ph = UploadStateFor(_history[idx]);
            if (ph != _segPhases[i]) { _segPhases[i] = ph; changed = true; }
            if (i < n && ph != UploadPhase.Done) all = false;
        }
        var sel = _selectedSession;
        var phase = sel != null ? UploadStateFor(sel) : UploadPhase.Idle;
        var url = sel != null && phase == UploadPhase.Done ? UploadUrlFor(sel) : null;
        if (phase != _selPhase || !ReferenceEquals(url, _selUrl) || _uploadBlock != _statusBlock || all != _allUploaded)
            changed = true;
        if (!changed) return;
        _selPhase = phase; _selUrl = url; _statusBlock = _uploadBlock; _allUploaded = all;
        ComposeRunStatus(sel, phase, url);
    }

    private void ComposeRunStatus(EncounterHistoryEntry? sel, UploadPhase phase, string? url)
    {
        _statusWord = ""; _statusTail = "";
        if (sel == null) { _runStatus = RunStatus.None; return; }
        // "segment N" only when the run has more than one (a single-archive run has nothing to tell apart).
        int pos = Array.IndexOf(_selectedSegments, _historyIndex) + 1;
        string seg = _selectedSegments.Length > 1 && pos > 0 ? _loc.TFormat("history.segment.numbered", pos) : "";
        if (phase == UploadPhase.Done)
        {
            _runStatus = RunStatus.Uploaded;
            _statusWord = _loc.T("history.upload.done");
            // The run id ("sea/HpPqOu76Bh", ShortRunLabel) rather than the whole URL; Copy link yields the full link.
            _statusTail = Join(seg, url != null ? ShortRunLabel(url) : "");
        }
        else if (phase == UploadPhase.InFlight)
        {
            _runStatus = RunStatus.Uploading;
            _statusWord = _loc.T("history.upload.uploading");
            _statusTail = Join(seg, "");
        }
        else if (_uploadBlock != UploadBlock.None)
        {
            _runStatus = RunStatus.NotUploaded;
            _statusWord = _loc.T("history.notUploaded");
            _statusTail = Join(seg, _uploadBlock switch
            {
                UploadBlock.NoRunId => _loc.T("history.upload.status.noRunId"),
                UploadBlock.OldRun  => _loc.T("history.upload.status.oldRun"),
                UploadBlock.Update  => _loc.T("history.upload.status.update"),
                _                   => _loc.T("history.upload.status.off"),
            });
        }
        else
        {
            // Uploadable and not uploaded → the Primary pill replaces the status word; the tail names the segment, or
            // why the last try failed.
            _runStatus = RunStatus.Pill;
            _statusTail = phase == UploadPhase.Failed ? _loc.T("history.upload.status.failed") : seg;
        }
    }

    // " · a · b" after a status word (empty parts dropped). Rebuild-only string building.
    private static string Join(string a, string b)
        => (a.Length > 0 ? " · " + a : "") + (b.Length > 0 ? " · " + b : "");

    // ── upload actions ───────────────────────────────────────────────────────────────────────────────────────────
    private string UploadPillLabel() => _selPhase == UploadPhase.Failed ? _loc.T("history.upload.retry") : SegmentUploadVerb();

    private readonly FmtCache _uploadAllFmt = new();

    private string UploadAllLabel() => _runUploadQueue.Count > 0
        ? _uploadAllFmt.Get(_loc.T("upload.uploadingLeft"), _runUploadQueue.Count)
        : _uploadAllFmt.Get(_loc.T("history.upload.all"), _selectedSegments.Length);

    private void UploadSelectedClicked()
    {
        if (_selectedSession is { } s && UploadStateFor(s) != UploadPhase.InFlight) UploadHistoryEntry(s);
    }

    // Feedback where you clicked (R13): the link glyph turns into a green ✓ for a moment.
    private const double CopiedFlashS = 1.5;
    private double _copiedAt = -10.0;

    private bool CopiedFlashing() => _services.Framework.TimeNow - _copiedAt < CopiedFlashS;

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
}
