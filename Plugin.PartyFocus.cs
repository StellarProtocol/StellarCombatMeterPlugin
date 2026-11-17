using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Party-focus mode — fixed 4-group × 5-slot grid drawn as 2×2 group quadrants (Group 1/2 top, 3/4 bottom),
// mirroring the game raid panel. Occupied slots render a MeterRowElement; empty slots render a faint caption.
// The plugin snapshots the grid into _gridRows/_gridOccupied each shown frame.
public sealed partial class Plugin
{
    private static readonly int GridSize = MeterAggregator.Groups * MeterAggregator.SlotsPerGroup;
    private readonly MeterRowData[] _gridRows = new MeterRowData[GridSize];
    private int _gridOccupied;   // occupied slots in the last snapshot (the header status "20 players")
    // Per-slot "hide this slot" (solo AND empty) + the solo flag itself, both refreshed in RebuildPartyFocusRows.
    private readonly bool[] _gridSlotHidden = new bool[GridSize];
    private bool _partyFocusSolo;
    private readonly List<PartyMember> _focusRoster = new();

    // The grid auto-follows the live party size: a 20-player raid renders the 2×2 four-group grid; a 5-player
    // party (and solo) renders a single full-width group of 5. Nested ConditionalElement (retained-mode) flips
    // branch visibility per poll; the window height follows via Plugin.PartyFocusHeight(). Prebuild (fw 2.30.0): the
    // other size's grid is built in the background, so a 5↔20 switch (joining a raid) doesn't build it in one frame.
    private HudElement BuildPartyFocusBody()
        => new ConditionalElement(() => IsRaid20View,
                RowSizeSwitch(() => Party20Toggles.RowSize, BuildFourGroupGrid),   // each party size keeps its own Row size
                RowSizeSwitch(() => Party5Toggles.RowSize, d => BuildGroupColumn(0, d)))
            { Prebuild = true, PrebuildName = "cm.partyGrid" };

    private HudElement BuildFourGroupGrid(MeterRowDensity d) => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[] { BuildQuadrant(0, d), new SpacerElement(8f), BuildQuadrant(1, d) }, Gap: 4f),
        new RowElement(new HudElement[] { BuildQuadrant(2, d), new SpacerElement(8f), BuildQuadrant(3, d) }, Gap: 4f),
    }, Gap: 4f);

    // True when the live party is a 20-player raid; 5-player AND solo fall through to the single 5-slot group.
    private bool IsRaid20View => _services.PartySnapshot.PartyType == PartyType.Raid20;

    // A party exists (vs solo) the moment a team is created — true even for a 1-member self-created team.
    // PartyId != 0 is the right gate; IsInParty requires 2+ members, which is wrong for a freshly-made party.
    private bool PartyExists => _services.PartySnapshot.PartyId != 0;

    // The 5/20 size control shows only in party-focus, only when a party exists, and only for the LEADER
    // (ChangeTeamMemberType is leader-only — the game rejects it otherwise). When leadership transfers away,
    // IsLeader flips false and the control disappears.
    private bool ShowPartySizeControl
        => _viewMode == ViewMode.PartyFocus && PartyExists && _services.PartySnapshot.IsLeader;

    // One group as a weighted cell (used in the 2×2 four-group grid where two cells share a row's width).
    private HudElement BuildQuadrant(int group, MeterRowDensity d) => new CellElement(BuildGroupColumn(group, d), Weight: 1f);

    // Every slot is a full-height MeterRowElement (occupied rows + faint placeholders), so all four quadrants
    // keep an equal 5-row height and the 2×2 grid stays proportional — the IMGUI build drew empty slots as a
    // full-height faint box; bare text collapsed them, skewing the grid.
    private HudElement BuildGroupColumn(int group, MeterRowDensity density)
    {
        var children = new HudElement[1 + MeterAggregator.SlotsPerGroup];
        children[0] = BuildGroupCaption(group);
        for (var slot = 0; slot < MeterAggregator.SlotsPerGroup; slot++)
        {
            var idx = group * MeterAggregator.SlotsPerGroup + slot;
            // Solo (no party): an empty slot is hidden outright — only the self row shows (owner 2026-11-17). The flag is
            // computed at ~10 Hz in RebuildPartyFocusRows, so the per-frame predicate is one array read. The window keeps
            // its fixed PartyFocusHeight(), so the freed area just stays blank (no resize/jump).
            children[slot + 1] = new ConditionalElement(() => !_gridSlotHidden[idx], new DragSlotElement(
                // fw 2.33.0 opt-in house-style row; empty slots render as recessed tiles (MeterRowData.IsEmptySlot).
                new MeterRowElement(() => _gridRows[idx], OnRightClick: () => OnGridSlotRightClick(idx))
                    { Style = MeterRowStyle.Rounded, BackingOpacity = MeterRowBackingFn, Density = density },
                Key: idx,
                OnDrop: OnGridDrop,
                // Leader-only, Raid20-only, and only an OCCUPIED slot is a drag source (an empty "—" slot
                // would otherwise spawn a ghost of a blank card). Empty slots remain valid drop TARGETS.
                CanDrag: () => IsRaid20View && _services.PartySnapshot.IsLeader && _gridRows[idx].Id.IsPlayer));
        }
        return new ColumnElement(children, Gap: RowGapFor(density));
    }

    private void RebuildPartyFocusRows()
    {
        var grid = _agg.PartyGrid(PartyFocusRoster(), _metric);
        double elapsed = EncounterElapsedSeconds();
        // Player-tile size for the active party mode — applied to empty/absent slots too so every row in the
        // grid renders at the same height (owner 2026-09-24). Occupied rows read it via BuildRowData.
        float tilePx = MeterElementToggles.TileSizePx((IsRaid20View ? Party20Toggles : Party5Toggles).TileSize);
        // Empty-slot labels: catalog lookups (cached strings); the invite hint only for the leader of an existing party
        // (the right-click invite is leader-only, OnGridSlotRightClick).
        string emptyName = _loc.T("hud.slot.empty");
        bool solo = !PartyExists;
        _partyFocusSolo = solo;
        string? emptyHint = !solo && _services.PartySnapshot.IsLeader ? _loc.T("hud.slot.inviteHint") : null;
        int occupied = 0;
        for (var i = 0; i < GridSize; i++)
        {
            if (grid[i] is { } row) { _gridRows[i] = BuildRowData(row, i + 1, elapsed, collapse: false); occupied++; _gridSlotHidden[i] = false; }
            else { _gridRows[i] = EmptySlot(i + 1, tilePx, emptyName, emptyHint); _gridSlotHidden[i] = solo; }
        }
        _gridOccupied = occupied;
        CountGroups();
    }

    // Placeholder for an unoccupied slot: the rounded row draws IsEmptySlot as a recessed tile with a faint "N  Empty"
    // and the optional right-aligned hint (no bar/spine). Carries the same tile size (DebuffCellSize) as the occupied
    // rows so its height matches; no player ⇒ no debuff block (ShowDebuffs off). Rank = the slot number in its group.
    private static MeterRowData EmptySlot(int number, float tilePx, string name, string? hint) => new MeterRowData
    {
        IsEmptySlot = true, EmptyHint = hint,
        HideSheen = true,   // no bar is drawn → don't keep sweeping (and masking) an invisible band every frame
        Rank = SmallNumber(number), Name = name, Spec = "", ClassName = "", AbilityScore = "",
        PrimaryValue = "", SecondaryValue = "", SharePercent = "",
        RoleColor = new ColorRgba(0f, 0f, 0f, 0f), HpColor = new ColorRgba(0f, 0f, 0f, 0f),
        HpFraction = 0f, BarFraction = 0f, CrestTexture = null, CrestUv = new UvRect(0f, 0f, 1f, 1f),
        IsSelf = false, Offline = false, ShowSpec = false, ShowSecondary = false, ShowShare = false,
        ShowRank = true, ShowDebuffs = false, DebuffCellSize = tilePx,
    };

    private void OnGridSlotRightClick(int idx)
    {
        var id = _gridRows[idx].Id;
        // Self slot may have no valid EntityId when there's no combat data yet — fall back to the live local ID.
        if (_gridRows[idx].IsSelf && !id.IsPlayer)
            id = _services.CombatSnapshot.LocalEntityId;
        if (id.IsPlayer)
            OpenRowMenu(id);
        else if (PartyExists && _services.PartySnapshot.IsLeader && !_gridRows[idx].IsSelf)
            OpenInviteMenu();
    }

    // Party-focus must ALWAYS show the local player — synthesise a self entry when the roster lacks us.
    private IReadOnlyList<PartyMember> PartyFocusRoster()
    {
        _focusRoster.Clear();
        var roster = _services.PartyRoster.Members;
        long selfChar = _services.CombatSnapshot.LocalEntityId.Value >> 16;

        bool hasSelf = false;
        foreach (var m in roster)
        {
            _focusRoster.Add(m);
            if (m.CharId == selfChar) hasSelf = true;
        }

        if (!hasSelf && selfChar != 0)
        {
            var ps = _services.PlayerState;
            _focusRoster.Add(new PartyMember(
                CharId: selfChar, Name: ps.Name, Profession: ps.Profession, Level: ps.Level,
                Hp: ps.Health, MaxHp: ps.MaxHealth, SceneId: 0, Position: ps.Position,
                IsOnline: true, IsSelf: true, GroupId: 1));
        }

        return _focusRoster;
    }
}
