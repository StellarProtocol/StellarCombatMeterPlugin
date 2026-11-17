using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Party-focus group caption (approved redesign §1, prototype 1b): "GROUP 1" bold 12 uppercase + a right-aligned muted hint
// "4 / 5", which during a ready check reads "4 / 5 · ready check 2 / 4" (ready = the leader, who auto-readies, plus every
// member who voted ready — the same rule as the row name tints, Plugin.ReadyCheck.cs).
// The per-group counts are taken in RebuildPartyFocusRows (~10 Hz, already walking the grid); the caption and hint strings
// are re-formatted ONLY when a group's counts or the language change (Plugin-Performance-Rules §2).
public sealed partial class Plugin
{
    private const float GroupCaptionH = 20f;

    private readonly int[] _groupOccupied = new int[MeterAggregator.Groups];
    private readonly int[] _groupReady    = new int[MeterAggregator.Groups];
    private readonly int[] _groupHintKey  = NewKeys(MeterAggregator.Groups);
    private readonly string[] _groupHint  = NewTexts(MeterAggregator.Groups);
    private readonly string[] _groupLabel = NewTexts(MeterAggregator.Groups);
    private string? _groupLang, _groupHintLang;

    private static int[] NewKeys(int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = -1; return a; }
    private static string[] NewTexts(int n) { var a = new string[n]; for (int i = 0; i < n; i++) a[i] = ""; return a; }

    private HudElement BuildGroupCaption(int group) => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, GroupCaptionH),
        new TextElement(() => GroupLabel(group), Color: () => TextColor(), NoWrap: true, FontSize: 12) { Bold = true },
        new SpacerElement(0f),   // flexible: pushes the hint to the right edge
        new TextElement(() => GroupHint(group), Color: () => MutedColor(), NoWrap: true, FontSize: 12) { RichEmoji = true },
    }, Gap: 6f);

    // Occupied + ready counts per group from the snapshot just built (no allocation).
    private void CountGroups()
    {
        long leader = _services.PartySnapshot.LeaderCharId;
        for (int g = 0; g < MeterAggregator.Groups; g++)
        {
            int occ = 0, ready = 0;
            for (int s = 0; s < MeterAggregator.SlotsPerGroup; s++)
            {
                ref readonly var r = ref _gridRows[g * MeterAggregator.SlotsPerGroup + s];
                if (r.IsEmptySlot) continue;
                occ++;
                if (!_readyCheckInProgress) continue;
                long uid = r.Id.Uid;
                if ((uid != 0 && uid == leader) || (_readyStates.TryGetValue(uid, out var st) && st == ReadyStatus.Ready)) ready++;
            }
            _groupOccupied[g] = occ;
            _groupReady[g] = ready;
        }
    }

    // "GROUP 3" — formatted + upper-cased once per language.
    private string GroupLabel(int group)
    {
        string lang = _loc.Language;
        if (!ReferenceEquals(lang, _groupLang))
        {
            _groupLang = lang;
            for (int g = 0; g < _groupLabel.Length; g++)
                _groupLabel[g] = _loc.TFormat("partyFocus.group", g + 1).ToUpperInvariant();
        }
        return _groupLabel[group];
    }

    private string GroupHint(int group)
    {
        int occ = _groupOccupied[group], ready = _groupReady[group];
        bool check = _readyCheckInProgress && occ > 0;
        int key = occ | ready << 8 | (check ? 1 << 16 : 0);
        string lang = _loc.Language;
        if (key == _groupHintKey[group] && ReferenceEquals(lang, _groupHintLang)) return _groupHint[group];
        if (!ReferenceEquals(lang, _groupHintLang)) { _groupHintLang = lang; for (int g = 0; g < _groupHintKey.Length; g++) _groupHintKey[g] = -1; }
        _groupHintKey[group] = key;
        _groupHint[group] = check
            ? _loc.TFormat("hud.group.countReady", occ, MeterAggregator.SlotsPerGroup, ready, occ)
            : _loc.TFormat("hud.group.count", occ, MeterAggregator.SlotsPerGroup);
        return _groupHint[group];
    }
}
