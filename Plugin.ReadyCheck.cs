using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

// A member's recorded vote. "Pending" (no response yet) is represented by absence
// from _readyStates, not an enum value.
internal enum ReadyStatus : byte { Ready, Declined }

// Ready-check tracker. Member responses arrive as IPartyEvents.ReadyCheckResponded —
// decoded by the framework from the WorldNtf NotifyCaptainReady packet (method 71). No
// Lua hooks, no polling, no IL2CPP interop here: the framework owns all of that.
//
// The party LEADER who initiates the check never receives the method-70 open/close
// packet, so our own button press is the "open" and a timer (the game's
// DungeonPrepareTime + DungeonPrepareCD = 25s window) is the "close".
public sealed partial class Plugin
{
    private readonly Dictionary<long, ReadyStatus> _readyStates = new(); // keyed by charId
    private bool _readyCheckInProgress;
    private bool _readyCheckSubscribed;
    private float _readyCheckActiveTimer;

    private const float ReadyCheckWindowS = 27f; // DungeonPrepareTime(20) + DungeonPrepareCD(5) + slack

    // Row name-colour for the vote state (alpha 0 = no override → framework default).
    private static readonly ColorRgba ReadyPendingCol = new(0.38f, 0.62f, 0.96f, 1f); // blue
    private static readonly ColorRgba ReadyReadyCol   = new(0.36f, 0.85f, 0.46f, 1f); // green
    private static readonly ColorRgba ReadyDeclineCol = new(0.92f, 0.36f, 0.33f, 1f); // red

    // The name-colour for a meter row during an active ready-check. The leader auto-readies
    // (green); responders are green/red; party members who haven't answered are blue (pending);
    // non-party entities and the idle state get no override.
    private ColorRgba ReadyVoteColor(EntityId id)
    {
        if (!_readyCheckInProgress) return default;
        long charId = id.Uid;
        if (charId == _services.PartySnapshot.LeaderCharId) return ReadyReadyCol;
        if (_readyStates.TryGetValue(charId, out var st))
            return st == ReadyStatus.Ready ? ReadyReadyCol : ReadyDeclineCol;
        return IsPartyMember(charId) ? ReadyPendingCol : default;
    }

    private bool IsPartyMember(long charId)
    {
        foreach (var m in _services.PartyRoster.Members)
            if (m.CharId == charId) return true;
        return false;
    }

    private void EnsureReadyCheckSubscribed()
    {
        if (_readyCheckSubscribed) return;
        _readyCheckSubscribed = true;
        _services.PartyEvents.ReadyCheckResponded += OnReadyCheckResponded;
    }

    // We initiated a ready-check (leader path — no method-70 open reaches us). Reset and
    // arm the window timer; member responses follow via OnReadyCheckResponded.
    internal void OnReadyCheckPressed()
    {
        _readyCheckInProgress = true;
        _readyStates.Clear();
        _readyCheckActiveTimer = ReadyCheckWindowS;
    }

    // Framework push (Unity main thread): one member readied or declined.
    private void OnReadyCheckResponded(ReadyCheckResponse e)
    {
        if (!_readyCheckInProgress)
        {
            // Non-leader path: a check we didn't start is in progress. Begin tracking.
            _readyCheckInProgress = true;
            _readyCheckActiveTimer = ReadyCheckWindowS;
        }
        _readyStates[e.CharId] = e.IsReady ? ReadyStatus.Ready : ReadyStatus.Declined;
    }

    private void TickReadyCheck(float dt)
    {
        if (!_readyCheckInProgress) return;
        _readyCheckActiveTimer -= dt;
        if (_readyCheckActiveTimer <= 0f)
        {
            _readyCheckInProgress = false;
            _readyStates.Clear();
        }
    }
}
