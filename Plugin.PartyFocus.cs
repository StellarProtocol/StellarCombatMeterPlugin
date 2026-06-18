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
    private readonly List<PartyMember> _focusRoster = new();

    // The grid auto-follows the live party size: a 20-player raid renders the 2×2 four-group grid; a 5-player
    // party (and solo) renders a single full-width group of 5. Nested ConditionalElement (retained-mode) flips
    // branch visibility per poll; the window height follows via Plugin.PartyFocusHeight().
    private HudElement BuildPartyFocusBody()
        => new ConditionalElement(() => IsRaid20View, BuildFourGroupGrid(), BuildGroupColumn(0));

    private HudElement BuildFourGroupGrid() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[] { BuildQuadrant(0), new SpacerElement(8f), BuildQuadrant(1) }, Gap: 4f),
        new RowElement(new HudElement[] { BuildQuadrant(2), new SpacerElement(8f), BuildQuadrant(3) }, Gap: 4f),
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
    private HudElement BuildQuadrant(int group) => new CellElement(BuildGroupColumn(group), Weight: 1f);

    // Every slot is a full-height MeterRowElement (occupied rows + faint placeholders), so all four quadrants
    // keep an equal 5-row height and the 2×2 grid stays proportional — the IMGUI build drew empty slots as a
    // full-height faint box; bare text collapsed them, skewing the grid.
    private HudElement BuildGroupColumn(int group)
    {
        var children = new HudElement[1 + MeterAggregator.SlotsPerGroup];
        children[0] = new TextElement(() => $"Group {group + 1}", MutedCol, Emphasis: true);
        for (var slot = 0; slot < MeterAggregator.SlotsPerGroup; slot++)
        {
            var idx = group * MeterAggregator.SlotsPerGroup + slot;
            children[slot + 1] = new DragSlotElement(
                new MeterRowElement(() => _gridRows[idx], OnRightClick: () => OpenRowMenu(_gridRows[idx].Id)),
                Key: idx,
                OnDrop: OnGridDrop,
                // Leader-only, Raid20-only, and only an OCCUPIED slot is a drag source (an empty "—" slot
                // would otherwise spawn a ghost of a blank card). Empty slots remain valid drop TARGETS.
                CanDrag: () => IsRaid20View && _services.PartySnapshot.IsLeader && _gridRows[idx].Id.IsPlayer);
        }
        return new ColumnElement(children, Gap: 3f);
    }

    private void RebuildPartyFocusRows()
    {
        var grid = _agg.PartyGrid(PartyFocusRoster(), _metric);
        double elapsed = EncounterElapsedSeconds();
        for (var i = 0; i < GridSize; i++)
            _gridRows[i] = grid[i] is { } row ? BuildRowData(row, i + 1, elapsed, collapse: false) : EmptySlot(i + 1);
    }

    // Faint full-height placeholder for an unoccupied raid slot ("N. —", no bar/spine).
    private static MeterRowData EmptySlot(int number) => new MeterRowData
    {
        Rank = $"{number}.", Name = "—", Spec = "", ClassName = "", AbilityScore = "",
        PrimaryValue = "", SecondaryValue = "", SharePercent = "",
        RoleColor = new ColorRgba(0f, 0f, 0f, 0f), HpColor = new ColorRgba(0f, 0f, 0f, 0f),
        HpFraction = 0f, BarFraction = 0f, CrestTexture = null, CrestUv = new UvRect(0f, 0f, 1f, 1f),
        IsSelf = false, Offline = false, ShowSpec = false, ShowSecondary = false, ShowShare = false,
    };

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
