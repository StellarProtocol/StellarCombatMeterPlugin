using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// Main meter window root + its 28 px header (approved redesign 2026-11-17 §1,
/// _scratch\prototypes\combatmeter-redesign-APPROVED.md): metric pill dropdown · List | Party segmented control · live
/// muted status (flexible, ellipsizes first) · leader tools + divider (Party-focus leader only) · Pause · Archive ·
/// ↺ Reset · ⋯ More. The old ▾ inline menu is GONE — it grew the HUD by 72 px; scope / party size / history / settings
/// live in the ⋯ popover (Plugin.MorePopover.cs), Pause / Archive in the header (Plugin.HudEncounter.cs), so the HUD
/// never changes height. Metric/scope/mode changes persist via PersistPrefs().
/// </summary>
public sealed partial class Plugin
{
    private const int MetricCount = 3;   // Dps, Hps, Taken — used by CycleMetric wraparound
    // Fixed dropdown width = the longest metric caption in ANY shipped locale (ja "被ダメージ") + caption padding and the
    // chevron, so switching DPS → Taken never moves the controls beside it (R10).
    private const float MetricDropdownW = 104f;

    // Localized metric name (DPS/HPS acronyms stay per language; "Taken" translates). Resolved live via _loc.
    private string MetricName(Metric m) => m switch
    {
        Metric.Dps => _loc.T("list.metric.dps"),
        Metric.Hps => _loc.T("list.metric.hps"),
        _          => _loc.T("list.metric.taken"),
    };

    // Legacy muted colour, still used by the History / Skill Breakdown / Snapshot windows.
    private ColorRgba? MutedCol() => new ColorRgba(0.66f, 0.70f, 0.73f, 1f);

    // The approved "Background" panel behind the whole meter (Settings → Look → Meter window): the radius-6 theme Panel
    // faded by MeterBackgroundOpacity × theme window opacity. Drawn by the framework's WindowSpec.BackgroundColor backdrop
    // (fw 2.33.0) — a non-layout Image, NOT a root BubbleElement, which would re-measure the whole meter on every
    // list/status change. Polled at the window refresh rate; struct maths only (no allocation). 0 % → alpha 0 → the
    // framework disables the Image, so the default look (no panel) costs nothing.
    private ColorRgba MeterBackdropColor()
    {
        var a = MeterBackgroundOpacity;
        if (a <= 0f) return Transparent;
        var c = PanelColor();
        return c with { A = c.A * a * WindowOpacity() };
    }
    private HudElement BuildMainRoot() => new ColumnElement(new HudElement[]
    {
        // Persistent "update the plugin" banner — shown while this build is below the server upload floor and not
        // dismissed this session (owner 2026-08-16: the notice must stay until the user closes it, which the transient
        // framework toast cannot do). Sits above the header so it's the first thing seen.
        new ConditionalElement(() => ShowCompatBanner, BuildCompatBanner()),
        BuildHeaderBar(),
        new ConditionalElement(() => _viewMode == ViewMode.List, BuildListBody(), Fill: true),
        new ConditionalElement(() => _viewMode == ViewMode.PartyFocus, BuildPartyFocusBody()),
    }, Gap: 6f);

    // Amber rounded strip (amber @14 % over the panel) with bold amber text and a round ✕ (red "remove" hover) that
    // dismisses it for the session. The server's own message wins over the localized default (Plugin.UploadCompat.cs).
    private HudElement BuildCompatBanner()
    {
        ColorRgba Fill() => Over(CautionColor() with { A = 0.14f }, PanelColor());
        return new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(6f, HudControlH),
            new CellElement(new TextElement(CompatBannerText, Color: () => Readable(CautionColor(), Fill()), FontSize: 12)
                { Bold = true, MaxLines = 2 }, Weight: 1f),
            IconButton(() => "✕", DismissCompatNotice, remove: true),
        }, Gap: 6f))
        {
            FillColorFn = () => Fade(Fill()), CornerRadius = 6f, PaddingX = 2f, PaddingY = 1f,
        };
    }

    private HudElement BuildHeaderBar() => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, HudControlH),   // pins the header at 28 px whatever the children measure
        PillDropdown(() => (int)_metric, MetricOptions, i => SelectMetric((Metric)i), MetricDropdownW),
        HudSegmented(new Func<string>[] { () => _loc.T("header.mode.list"), () => _loc.T("hud.mode.party") },
            () => _viewMode == ViewMode.List ? 0 : 1,
            i => { if ((i == 0) != (_viewMode == ViewMode.List)) ToggleViewMode(); }),
        new ConditionalElement(HudStatusHasDot, StatusDot(HudStatusDotColor)),
        // The status takes the slack (weighted) and ellipsizes, so it is also the flexible gap before the tools.
        new CellElement(new TextElement(HudStatusText, Color: HudStatusColor, NoWrap: true, FontSize: 12)
            { RichEmoji = true, Ellipsize = true }, Weight: 1f),
        new ConditionalElement(() => ShowLeaderTools, BuildLeaderTools()),
        BuildPauseButton(),     // Plugin.HudEncounter.cs (addendum: Pause + Archive header icons)
        BuildArchiveButton(),
        HudIconButton(() => _resetPng, Clear),
        HudIconButton(() => _morePng, static () => { }, ToggleMorePopover),
    }, Gap: 6f);

    // Party-focus + in a party + leader — exactly the old header gate. Mark / ready check / convene / countdown as round
    // 28 px icon buttons; while the ready-check cooldown runs, that button is RECESSED and shows the seconds left. A 1 px
    // MenuText@14 % divider, 18 px tall, separates them from Pause / Archive / ↺ / ⋯ (addendum 2026-11-17).
    private bool ShowLeaderTools => _viewMode == ViewMode.PartyFocus
                                    && _services.PartySnapshot.IsInParty && _services.PartySnapshot.IsLeader;

    private HudElement BuildLeaderTools() => new RowElement(new HudElement[]
    {
        HudIconButton(() => _locationPinPng, TogglePunctuate),
        new ConditionalElement(() => _readyCheckCooldown <= 0f,
            HudIconButton(() => _checkmarkPng, LeaderReadyCheck),
            HudRecessedLabel(ReadyCheckLabel)),
        HudIconButton(() => _megaphonePng, LeaderConvene),
        HudIconButton(() => _countdownPng, LeaderCountdown),
        new ImageElement(() => _dividerPng, 1, 18) { Tint = () => Fade(TextColor() with { A = 0.14f }) },
    }, Gap: 6f);

    // DPS / HPS / Taken captions, rebuilt only when the language changes (the dropdown polls this every refresh).
    private string[]? _metricOptions;
    private string? _metricOptionsLang;

    private System.Collections.Generic.IReadOnlyList<string> MetricOptions()
    {
        string lang = _loc.Language;
        if (_metricOptions == null || !ReferenceEquals(lang, _metricOptionsLang))
        {
            _metricOptionsLang = lang;
            _metricOptions = new[] { MetricName(Metric.Dps), MetricName(Metric.Hps), MetricName(Metric.Taken) };
        }
        return _metricOptions;
    }

    // ----- actions -----

    private void SelectMetric(Metric m) { _metric = m; PersistPrefs(); }

    // Advance to the next metric (DPS → HPS → Taken → DPS). Wired to the combatmeter.mode hotkey.
    private void CycleMetric() => SelectMetric((Metric)(((int)_metric + 1) % MetricCount));
    private void SelectScope(FilterMode f) { _filter = f; PersistPrefs(); }

    // Invoke the game's OWN ChangeTeamMemberType through IPartyControl (Lua bridge) — never a hand-built
    // packet; the game validates (leader + 20-player unlock) and may reject. The grid follows the resulting
    // server broadcast. No-op when the requested size is already active.
    private void RequestPartySize(PartyType size)
    {
        if (_services.PartySnapshot.PartyType == size) return;
        _services.PartyControl.SetMemberType(size);
    }

    private void ToggleViewMode()
    {
        CaptureModeGeometry();   // remember the view we're leaving
        _viewMode = _viewMode == ViewMode.List ? ViewMode.PartyFocus : ViewMode.List;
        PersistPrefs();
        ApplyModeSize();         // restore the view we're entering
    }

    private void TogglePause() { _paused = !_paused; }

    // One unconditional line BEFORE the archive runs — a user-initiated press, so ungated logging is
    // correct (matching LogArchiveOutcome). The 2026-07-19 field report was "I pressed Archive and
    // nothing happened, with no log evidence"; this proves the press reached the plugin, paired with
    // the archive-outcome line ManualArchive emits (skip-empty | suppressed | banked | banked+upload).
    private void ManualArchiveFromMenu()
    {
        _services.Log.Info("[CombatMeter][archive] manual-press");
        ManualArchive();
    }

    // Visible acknowledgment that a MANUAL archive landed (banked) — a success toast on the meter, plus the header's green
    // ✓ flash on Archive and "✓ Archived (102 s) · History 37" status line (Plugin.HudEncounter.cs). Called from ManualArchive only when
    // reason == Manual, so auto archives (scene/stage/boss/idle) don't spam the toast surface.
    private void NotifyManualArchived(long durationMs)
    {
        _services.Notifications.Notify(_loc.TFormat("archive.saved.toast", durationMs / 1000), NotificationKind.Success);
        NoteArchivedFeedback(durationMs);
    }

    // Shift+F9 and the launcher tile. Opening goes through OpenHistory (centred on the screen + list filled at once) like
    // every other entry point.
    private void ToggleHistory()
    {
        if (_historyWindow.IsShown) CloseHistory();
        else OpenHistory(null);
    }

    private void CloseHistory()
    {
        _historyWindow.SetVisible(false);
        _historyIndex = -1;
        CloseSkillBreakdown();
    }
}
