using System;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

// Registers party-management items in the entity right-click context menu.
// Leader-only items gate on IsLeader (checked live). Invite gates on target not in roster.
public sealed partial class Plugin
{
    private IDisposable _transferLeaderReg = null!;
    private IDisposable _kickMemberReg     = null!;
    private IDisposable _inviteToTeamReg   = null!;
    private IDisposable _createPartyReg    = null!;
    private IDisposable _leavePartyReg     = null!;

    private void RegisterTeamContextMenuItems()
    {
        _transferLeaderReg = _services.EntityContextMenu.Register(
            "Transfer Leader",
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && _services.PartySnapshot.IsLeader
                         && e != _services.CombatSnapshot.LocalEntityId
                         && IsInParty(e),
            onClick: e => _services.PartyControl.TransferLeader(e.Value >> 16));

        _kickMemberReg = _services.EntityContextMenu.Register(
            "Kick from Party",
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && _services.PartySnapshot.IsLeader
                         && e != _services.CombatSnapshot.LocalEntityId
                         && IsInParty(e),
            onClick: e => _services.PartyControl.KickMember(e.Value >> 16));

        _inviteToTeamReg = _services.EntityContextMenu.Register(
            "Invite to Party",
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && PartyExists
                         && _services.PartySnapshot.IsLeader
                         && e != _services.CombatSnapshot.LocalEntityId
                         && !IsInParty(e),
            onClick: e => _services.PartyControl.InviteToTeam(e.Value >> 16));

        _createPartyReg = _services.EntityContextMenu.Register(
            "Create Party",
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && e == _services.CombatSnapshot.LocalEntityId
                         && !PartyExists,
            onClick: _ => CallLua("pcall(function() (Z.CoroUtil).create_coro_xpcall(function() local vm=(Z.VMMgr).GetVM('team') if vm then local cs=(Z.CancelSource).Rent() vm.AsyncCreatTeam(1000,cs:CreateToken()) end end,function() end)() end)"));

        _leavePartyReg = _services.EntityContextMenu.Register(
            "Leave Party",
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && e == _services.CombatSnapshot.LocalEntityId
                         && PartyExists,
            onClick: _ => _services.PartyControl.LeaveParty());
    }

    // Returns true when the entity is already a party member (skip self — the row menu
    // already filters self out at the OpenRowMenu call site).
    private bool IsInParty(EntityId e)
    {
        long charId = e.Value >> 16;
        foreach (var m in _services.PartyRoster.Members)
            if (m.CharId == charId) return true;
        return false;
    }
}
