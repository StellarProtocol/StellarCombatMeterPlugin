using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.CombatMeter;

// List mode — borderless, ranked, role-coloured rows via the bespoke MeterRowElement. The plugin snapshots the
// visible rows into _listRows each frame the window is shown; the element Funcs index that cache.
public sealed partial class Plugin
{
    private const int MaxListRows = 24;
    private const float ListScrollHeight = 312f;   // ~6 rows (48 + gap) before scrolling

    private readonly List<MeterRowData> _listRows = new(MaxListRows);
    private readonly Dictionary<EntityId, float> _barAnim = new();
    private bool _listEmpty = true;
    private string _listEmptyCaption = "Waiting for combat...";

    // Per-mode element toggle instances — lazy-loaded from config so they never touch the Plugin.cs constructor.
    // Task 8 settings panel will expose accessors to mutate/persist these.
    private MeterElementToggles? _listTogglesCache;
    private MeterElementToggles? _party5TogglesCache;
    private MeterElementToggles? _party20TogglesCache;
    private MeterElementToggles ListToggles    => _listTogglesCache    ??= MeterElementToggles.Load(_prefs, "list",    MeterElementToggles.Defaults());
    private MeterElementToggles Party5Toggles  => _party5TogglesCache  ??= MeterElementToggles.Load(_prefs, "party5",  MeterElementToggles.Defaults());
    private MeterElementToggles Party20Toggles => _party20TogglesCache ??= MeterElementToggles.Load(_prefs, "party20", MeterElementToggles.Raid20Defaults());

    private HudElement BuildListBody()
    {
        var slots = new HudElement[MaxListRows];
        for (var i = 0; i < MaxListRows; i++)
        {
            var idx = i;
            slots[i] = new MeterRowElement(
                () => idx < _listRows.Count ? _listRows[idx] : default,
                OnRightClick: () => OpenRowMenu(idx < _listRows.Count ? _listRows[idx].Id : default));
        }
        // Empty → caption; else the scroll. Fill:true so the scroll grows with the (resizable) window height.
        return new ConditionalElement(() => _listEmpty,
            new TextElement(() => _listEmptyCaption, MutedCol),
            new ScrollElement(new ListElement(() => _listRows.Count, slots), ListScrollHeight),
            Fill: true);
    }

    private void RebuildListRows()
    {
        _listRows.Clear();

        if (_stats.Count == 0) { _listEmpty = true; _listEmptyCaption = "Waiting for combat..."; return; }

        double elapsed = EncounterElapsedSeconds();
        var rows = _agg.Rows(_metric, elapsed);
        if (rows.Count == 0) { _listEmpty = true; _listEmptyCaption = EmptyMetricCaption(); return; }

        var visible = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!InScope(rows[i].Id)) continue;   // scope is a view filter; rank stays global
            if (_listRows.Count >= MaxListRows) break;
            _listRows.Add(BuildRowData(rows[i], i + 1, elapsed, collapse: true));
            visible++;
        }
        _listEmpty = visible == 0;
        if (_listEmpty) _listEmptyCaption = "No players in scope.";
    }

    private string EmptyMetricCaption() => _metric switch
    {
        Metric.Hps   => "No healing yet.",
        Metric.Taken => "No damage taken yet.",
        _            => "No players in combat.",
    };

    // Map an aggregator MeterRow → the framework MeterRowData (the renderer-neutral row snapshot).
    // collapse=true → List mode with width-driven collapse guards; false → Party-focus (fixed-width).
    private MeterRowData BuildRowData(MeterRow row, int rank, double elapsed, bool collapse)
    {
        // Party-focus (collapse=false) picks its toggle set by live party size — the dense 20-grid has its own
        // (leaner) config separate from the 5-player grid.
        var toggles = collapse ? ListToggles : (IsRaid20View ? Party20Toggles : Party5Toggles);
        var vis = toggles.Resolve(collapse, _listWidthNow);
        EntityId id = row.Id;
        var target = (float)row.BarFraction;
        if (!_barAnim.TryGetValue(id, out var cur)) cur = target;
        cur = Mathf.Lerp(cur, target, 0.18f);
        _barAnim[id] = cur;
        return AssembleRow(row, rank, elapsed, vis, toggles);
    }

    // Assembles the MeterRowData value. Extracted to keep BuildRowData under 50 LoC (STELLAR0002).
    // Reads shared fields (_services, _barAnim, etc.) directly from the partial class.
    private MeterRowData AssembleRow(MeterRow row, int rank, double elapsed,
        MeterElementToggles.Resolved vis, MeterElementToggles toggles)
    {
        EntityId id = row.Id;
        EntityId self = _services.CombatSnapshot.LocalEntityId;
        _barAnim.TryGetValue(id, out var cur);
        var frac = HpFractionFor(id);
        object? crest = LoadCrest(id, out var crestUv);
        double dur = elapsed >= 1d ? elapsed : 1d;
        long perSec = (long)(row.Value / dur);
        var label = EntityLabel.Resolve(id, self, _services.PlayerState, _services.CombatLookup, _services.PartyRoster.Members);
        var (imagine0, imagine1) = ResolveImagines(id, id == self);
        return new MeterRowData
        {
            Id               = id,
            Rank             = $"{rank}.",
            Name             = label,
            ClassName        = vis.ClassName ? GetClassLine(id) : "",
            Spec             = vis.Spec ? SpecLine(id) : "",
            AbilityScore     = vis.AbilityScore && _services.CombatLookup.GetFightPoint(id) is var fp && fp > 0 ? fp.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "",
            RoleColor        = RoleColorFor(id),
            HpColor          = HpColor(),
            SelfAccent       = _selfAccentSlot.Value,
            HpFraction       = frac,
            CrestTexture     = crest,
            CrestUv          = crestUv,
            PrimaryValue     = FormatAmount(perSec),
            SecondaryValue   = FormatAmount(row.Value),
            BarFraction      = cur,
            SharePercent     = $"{row.Share * 100d:F0}%",
            IsSelf           = id == self,
            IsLeader         = IsPartyLeader(id),
            Offline          = IsOffline(id),
            Dead             = IsDead(id),
            ShowRank         = vis.Rank,
            ShowCrest        = vis.Crest,
            ShowSpec         = vis.Spec,
            ShowClassName    = vis.ClassName,
            ShowAbilityScore = vis.AbilityScore,
            ShowHpBar        = vis.HpBar,
            ShowPrimary      = vis.Primary,
            ShowSecondary    = vis.Total,
            ShowShare        = vis.Share,
            ShowImagine      = vis.Imagine,
            ShowImagineCooldown = vis.ImagineCooldown,
            ShowLeaderFlag   = vis.LeaderFlag,
            ImagineSize      = toggles.ImagineSize,
            ImaginePosition  = toggles.ImaginePosition,
            Imagine0         = imagine0,
            Imagine1         = imagine1,
        };
    }

    // Dead = HP known AND zero. Distinguish from "no HP data" (unknown ⇒ alive) so a vitals-less row isn't
    // falsely marked dead. Self via IPlayerState (authoritative); others via combat vitals then roster.
    private bool IsDead(EntityId id)
    {
        if (id == _services.CombatSnapshot.LocalEntityId)
        {
            var ps = _services.PlayerState;
            return ps.MaxHealth > 0 && ps.Health <= 0;
        }
        var v = _services.CombatLookup.GetVitals(id);
        if (v.IsKnown && v.MaxHp > 0) return v.Hp <= 0;
        long charId = id.Value >> 16;
        foreach (var m in _services.PartyRoster.Members)
            if (m.CharId == charId && m.MaxHp > 0) return m.Hp <= 0;
        return false;
    }

    // Class-crest icon for a row (null while loading / no profession). uv is the atlas sub-rect.
    private object? LoadCrest(EntityId id, out UvRect uv)
    {
        int profId = ResolveProfessionId(id);
        if (profId > 0) return _services.GameAssets.LoadProfessionIcon(profId, out uv);
        uv = new UvRect(0f, 0f, 1f, 1f);
        return null;
    }

    private bool IsPartyLeader(EntityId id)
    {
        var leaderCharId = _services.PartySnapshot.LeaderCharId;
        return leaderCharId != 0 && (id.Value >> 16) == leaderCharId;
    }

    private bool IsOffline(EntityId id)
    {
        long charId = id.Value >> 16;
        foreach (var m in _services.PartyRoster.Members)
            if (m.CharId == charId) return !m.IsOnline;
        return false;
    }

    private float HpFractionFor(EntityId id)
    {
        // SELF first: the combat-wire vitals cache does NOT reliably track the local player — self is
        // filtered out of the SyncNearDeltaInfo fanout, so GetVitals(self) is Unknown (and when the
        // SyncToMeDeltaInfo path does feed it, it can lag the live entity). IPlayerState reads the live
        // entity HP every sample, so it's the authoritative source for self — prefer it.
        if (id == _services.CombatSnapshot.LocalEntityId)
        {
            var ps = _services.PlayerState;
            if (ps.MaxHealth > 0) return Mathf.Clamp01((float)ps.Health / ps.MaxHealth);
        }

        var v = _services.CombatLookup.GetVitals(id);
        if (v.IsKnown && v.MaxHp > 0) return Mathf.Clamp01((float)v.Hp / v.MaxHp);

        long charId = id.Value >> 16;
        foreach (var m in _services.PartyRoster.Members)
            if (m.CharId == charId && m.MaxHp > 0) return Mathf.Clamp01((float)m.Hp / m.MaxHp);
        return 0f;
    }

    // Prefer the SELECTED spec name (e.g. "Icicle") over the class name ("Frost Mage"). Spec is cast-inferred
    // only (see CaptureSpec) — blank until a spec-defining skill is observed, falling back to the class name.
    // The wire talent_id turned out to be a tier id (=1), not the spec; the AOI loadout carries both specs'
    // skills so it can't disambiguate either — leaving combat casts as the only authoritative source.
    private string SpecLine(EntityId id)
    {
        var sub = ResolveSpec(id);
        if (sub != 0 && ProfessionSpecs.Name(sub) is { Length: > 0 } n) return n;
        return GetClassLine(id);
    }
}
