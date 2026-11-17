using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Header line 2 — the LEADER line (approved addendum 2026-11-17 "LEADER line", _scratch\prototypes\
// combatmeter-redesign-APPROVED.md): Party-focus + in a party + leader → "LEADER" caption + four labelled 28 px pills
// (drawn icon + text): Mark · Ready check · Convene · Countdown. During the ready-check cooldown that pill is RECESSED and
// reads "Ready check 18s". It replaced the four round icon buttons on line 1, which squeezed the status to "● 0:00 i…".
//
// Height — RESERVED, not grown (user 2026-11-17 "fixed", replacing the first "grow by 34 px while leader" build): in Party
// view the line's slot (28 px + the root Column's 6 px gap = LeaderLineH) is ALWAYS in the layout and ALWAYS in
// PartyFocusHeight. Leader → caption + pills; anyone else (member / solo / no party) → a blank 28 px spacer of the same
// height (nothing drawn, no Selectable, so no input is blocked). Leadership changes therefore never resize the window
// (house rule: windows never grow) — only the content inside the slot swaps. The grow build also needed a one-shot re-fit
// whose too-early SetRect persisted the (20,20) mount placeholder (the 2026-11-17 top-left bug, Plugin.ModeGeometry.cs).
// The group/rows container below keeps EXACTLY the height it had before the line existed: the window is +34, the slot 34.
//
// Performance (Plugin-Performance-Rules §7): built once; the predicates are field reads (_leaderLine is latched at the
// ~10 Hz tick, not read off the party snapshot per poll), the labels catalog lookups or per-language caches (the cooldown
// "Ready check 18s" strings are formatted once per language), the fills struct maths. No SetRect anywhere on this path.
public sealed partial class Plugin
{
    private const float LeaderLineH = HudControlH + 6f;   // the reserved slot + the root Column gap (34), always in Party view

    // Pill geometry: 8 | 16 icon | 5 | label (fixed) | 8. Each label width = the longest translation in ANY shipped locale
    // (and, for Ready check, its cooldown form "Ready check 25s" / th "เช็กความพร้อม 25 วิ"), so a language switch or
    // the cooldown never moves a pill (SDL R10). Budget: the 560 px Party minimum leaves 536 px of content; caption (≤ 50,
    // "LEADER"/"KETUA") + 6 + 4 × (37 + label) + 3 × 6 = 530 with the widths below.
    private const float LeaderPillPad = 8f, LeaderPillIconGap = 5f;
    private const float LeaderMarkW = 40f, LeaderReadyW = 112f, LeaderConveneW = 68f, LeaderCountdownW = 88f;

    private bool _leaderLine;   // latched "you lead a party" — picks the slot's content (pills vs blank); never the height

    // ~10 Hz from RebuildSnapshots (Party view, window shown) and on a mode switch. Content-only: a flip just swaps the
    // slot's Conditional branch (MarkDirty applies it on the next window tick instead of the next ~10 Hz poll).
    private void SyncLeaderLine()
    {
        bool want = _services.PartySnapshot.IsInParty && _services.PartySnapshot.IsLeader;
        if (want == _leaderLine) return;
        _leaderLine = want;
        _mainWindow.MarkDirty();
    }

    // The root Column's Party-view slot (Plugin.Header.cs): the LEADER line, or a same-height blank. The 0-wide spacer pins
    // the blank at HudControlH exactly like the line's own pinning spacer, so the slot never changes height.
    private HudElement BuildLeaderLineSlot()
        => new ConditionalElement(() => _leaderLine, BuildLeaderLine(), new SpacerElement(0f, HudControlH));

    private HudElement BuildLeaderLine() => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, HudControlH),   // pins the line at 28 px
        new TextElement(LeaderCaption, Color: () => TextColor(), NoWrap: true, FontSize: 11) { Bold = true },
        LeaderPill(() => _locationPinPng, () => _loc.T("hud.leader.mark"), LeaderMarkW, TogglePunctuate),
        new ConditionalElement(() => _readyCheckCooldown <= 0f,
            LeaderPill(() => _checkmarkPng, () => _loc.T("hud.leader.readyCheck"), LeaderReadyW, LeaderReadyCheck),
            LeaderRecessedPill(() => _checkmarkPng, ReadyCheckLabel, LeaderReadyW)),
        LeaderPill(() => _megaphonePng, () => _loc.T("hud.leader.convene"), LeaderConveneW, LeaderConvene),
        LeaderPill(() => _countdownPng, () => _loc.T("hud.leader.countdown"), LeaderCountdownW, LeaderCountdown),
    }, Gap: 6f);

    // A pressable pill: the HUD's faint raised fill (hover lifts it, like the line-1 icon buttons); icon AND label in the
    // full theme Text colour (owner: "the text should be white not dim").
    private HudElement LeaderPill(Func<byte[]?> png, Func<string> label, float labelW, Action onClick)
    {
        var hover = new HoverFlag();
        return new CellElement(new BubbleElement(new SelectableElement(
            LeaderPillContent(png, label, labelW, static p => p.TextColor()), OnClick: onClick)
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(hover.On ? HudHoverFill() : HudRestFill()),
            CornerRadius = RoundRadius(HudControlH), PaddingX = 0f, PaddingY = 0f, MaxWidth = LeaderPillW(labelW),
        }, Width: LeaderPillW(labelW));
    }

    // The same pill RECESSED (SDL rule 22: sunken fill + faded label/icon, no ring, same size, not clickable) — the ready
    // check while its cooldown runs. The only dim thing on the line.
    private HudElement LeaderRecessedPill(Func<byte[]?> png, Func<string> label, float labelW)
        => new CellElement(new BubbleElement(LeaderPillContent(png, label, labelW, static p => p.DisabledLabel(p.PanelColor())))
        {
            FillColorFn = () => Fade(DisabledFill(PanelColor())),
            CornerRadius = RoundRadius(HudControlH), PaddingX = 0f, PaddingY = 0f, MaxWidth = LeaderPillW(labelW),
        }, Width: LeaderPillW(labelW));

    private static float LeaderPillW(float labelW) => 2f * LeaderPillPad + HudIconPx + LeaderPillIconGap + labelW;

    // Row[pad, Cell(icon + label group, centred), pad] — the framework pill-dropdown recipe: the fixed inner Cell makes the
    // pill exactly its width and centres the icon+label group in it, so a shorter translation sits centred instead of
    // leaving a trailing gap; a too-long one ellipsizes rather than growing the pill. 0.01 gaps = a real zero gap.
    private HudElement LeaderPillContent(Func<byte[]?> png, Func<string> label, float labelW, Func<Plugin, ColorRgba> ink)
    {
        var group = new RowElement(new HudElement[]
        {
            new ImageElement(png, (int)HudIconPx, (int)HudIconPx) { Tint = () => ink(this) },
            new SpacerElement(LeaderPillIconGap, 0f),
            new TextElement(label, Color: () => ink(this), NoWrap: true, FontSize: 12) { Bold = true, Ellipsize = true },
        }, Gap: 0.01f, Justify: RowJustify.Center);
        return new RowElement(new HudElement[]
        {
            new SpacerElement(LeaderPillPad, HudControlH),
            new CellElement(group, Width: HudIconPx + LeaderPillIconGap + labelW),
            new SpacerElement(LeaderPillPad, HudControlH),
        }, Gap: 0.01f, Justify: RowJustify.Center);
    }

    // ----- cached labels -----

    private string _leaderCaption = "";
    private string? _leaderCaptionLang;

    // "LEADER" — upper-cased once per language (the catalog holds the sentence-case word).
    private string LeaderCaption()
    {
        string lang = _loc.Language;
        if (!ReferenceEquals(lang, _leaderCaptionLang))
        {
            _leaderCaptionLang = lang;
            _leaderCaption = _loc.T("hud.leader.caption").ToUpperInvariant();
        }
        return _leaderCaption;
    }

    private readonly string[] _readyWaitLabels = new string[(int)ReadyCheckCooldownS + 1];
    private string? _readyWaitLang;

    // The recessed pill's "Ready check 18s" — every second's string formatted once per language, then array reads.
    internal string ReadyCheckLabel()
    {
        if (_readyCheckCooldown <= 0f) return "";
        string lang = _loc.Language;
        if (!ReferenceEquals(lang, _readyWaitLang))
        {
            _readyWaitLang = lang;
            for (int s = 0; s < _readyWaitLabels.Length; s++) _readyWaitLabels[s] = _loc.TFormat("hud.leader.readyCheckWait", s);
        }
        int secs = (int)Math.Ceiling(_readyCheckCooldown);
        return _readyWaitLabels[Math.Clamp(secs, 0, _readyWaitLabels.Length - 1)];
    }
}
