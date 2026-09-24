using System;
using System.Collections.Generic;
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
    private IDisposable _sendMessageReg    = null!;
    private IDisposable _muteVoiceReg      = null!;
    private IDisposable _enableVoiceReg    = null!;
    private IDisposable _reportVoiceReg    = null!;

    // Local mirror of who I have voice-blocked. The game reflects a block on the ENABLE menu (it flips to
    // "Enable Voice") but the meter row's icon keeps showing the member's wire mic-mode, so the icon needs a
    // local truth to show the muted glyph. Keyed by charId (== EntityId >> 16). VoiceIconFor reads it; every
    // menu open re-syncs it from the game via IsVoiceBlocked, so a block done in the native HUD self-corrects.
    private readonly HashSet<long> _blockedVoice = new();

    private void RegisterTeamContextMenuItems()
    {
        _transferLeaderReg = _services.EntityContextMenu.Register(
            _loc.T("menu.transferLeader"),
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && _services.PartySnapshot.IsLeader
                         && e != _services.CombatSnapshot.LocalEntityId
                         && IsInParty(e),
            onClick: e => _services.PartyControl.TransferLeader(e.Value >> 16));

        _kickMemberReg = _services.EntityContextMenu.Register(
            _loc.T("menu.kickFromParty"),
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && _services.PartySnapshot.IsLeader
                         && e != _services.CombatSnapshot.LocalEntityId
                         && IsInParty(e),
            onClick: e => _services.PartyControl.KickMember(e.Value >> 16));

        _inviteToTeamReg = _services.EntityContextMenu.Register(
            _loc.T("menu.inviteToParty"),
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && PartyExists
                         && _services.PartySnapshot.IsLeader
                         && e != _services.CombatSnapshot.LocalEntityId
                         && !IsInParty(e),
            onClick: e => _services.PartyControl.InviteToTeam(e.Value >> 16));

        _createPartyReg = _services.EntityContextMenu.Register(
            _loc.T("menu.createParty"),
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && e == _services.CombatSnapshot.LocalEntityId
                         && !PartyExists,
            onClick: _ => _services.Lua.DoString("pcall(function() (Z.CoroUtil).create_coro_xpcall(function() local vm=(Z.VMMgr).GetVM('team') if vm then local cs=(Z.CancelSource).Rent() vm.AsyncCreatTeam(1000,cs:CreateToken()) end end,function() end)() end)"));

        _leavePartyReg = _services.EntityContextMenu.Register(
            _loc.T("menu.leaveParty"),
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && e == _services.CombatSnapshot.LocalEntityId
                         && PartyExists,
            onClick: _ => _services.PartyControl.LeaveParty());

        // Voice / social actions on another party member's row (PartyFocus, non-self, in-party). Driven through
        // the framework Lua bridge (friends_main / team VMs) — see docs on the RE'd game calls. Appended AFTER
        // the leader actions so the existing menu order is unchanged.
        _sendMessageReg = _services.EntityContextMenu.Register(
            _loc.T("menu.sendMessage"),
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && e != _services.CombatSnapshot.LocalEntityId
                         && IsInParty(e),
            onClick: e => _services.Lua.DoString(
                $"pcall(function() local vm=(Z.VMMgr).GetVM('friends_main') if vm then vm.OpenPrivateChat({e.Value >> 16}) end end)"));

        _muteVoiceReg = _services.EntityContextMenu.Register(
            _loc.T("menu.muteVoice"),
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && e != _services.CombatSnapshot.LocalEntityId
                         && IsInParty(e)
                         && !IsVoiceBlocked(e),
            onClick: e => SetMemberVoiceBlocked(e, true));

        _enableVoiceReg = _services.EntityContextMenu.Register(
            _loc.T("menu.enableVoice"),
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && e != _services.CombatSnapshot.LocalEntityId
                         && IsInParty(e)
                         && IsVoiceBlocked(e),
            onClick: e => SetMemberVoiceBlocked(e, false));

        // AsyncReportPlayer self-wraps in create_coro_call, which does NOT run from a bare DoString — it needs
        // the same create_coro_xpcall(...)() driver shell the other async team actions use (create-party above),
        // or the report silently no-ops. Sync calls (Send Message, Mute) run fine bare.
        _reportVoiceReg = _services.EntityContextMenu.Register(
            _loc.T("menu.reportVoice"),
            isVisible: e => _viewMode == ViewMode.PartyFocus
                         && e != _services.CombatSnapshot.LocalEntityId
                         && IsInParty(e),
            onClick: e => _services.Lua.DoString(
                $"pcall(function() (Z.CoroUtil).create_coro_xpcall(function() local vm=(Z.VMMgr).GetVM('team') if vm then vm.AsyncReportPlayer({e.Value >> 16}) end end,function() end)() end)"));
    }

    // Blocks/unblocks a member's team voice both locally (the meter-row icon, via _blockedVoice) and in the game
    // (the team VM). Extracted so the mute/enable onClick lambdas stay one-liners under the method-LoC gate.
    private void SetMemberVoiceBlocked(EntityId e, bool block)
    {
        long charId = e.Value >> 16;
        if (block) _blockedVoice.Add(charId); else _blockedVoice.Remove(charId);
        _services.Lua.DoString(
            $"pcall(function() local vm=(Z.VMMgr).GetVM('team') if vm then vm.BlockTeamMemberVoice({charId}, {(block ? "true" : "false")}) end end)");
    }

    // Re-register under a new UI language: the labels are captured by IEntityContextMenu.Register once,
    // so a language change requires disposing the old registrations and registering fresh (i18n P1).
    private void ReRegisterTeamContextMenuItems()
    {
        _transferLeaderReg.Dispose();
        _kickMemberReg.Dispose();
        _inviteToTeamReg.Dispose();
        _createPartyReg.Dispose();
        _leavePartyReg.Dispose();
        _sendMessageReg.Dispose();
        _muteVoiceReg.Dispose();
        _enableVoiceReg.Dispose();
        _reportVoiceReg.Dispose();
        RegisterTeamContextMenuItems();
    }

    // Reads the game's per-member voice-block flag (team_data:GetBlockVoiceState) via the Lua bridge, parking
    // the result in a global then reading it back (no Lua→C# callback). Evaluated once per menu open (OpenRowMenu
    // snapshots ItemsFor), so the Lua cost is fine. Returns false when the Lua state isn't ready or the read
    // fails — so "Mute Voice" shows and "Enable Voice" hides by default.
    private bool IsVoiceBlocked(EntityId e)
    {
        if (!_services.Lua.Ready) return false;
        long charId = e.Value >> 16;
        _services.Lua.DoString(
            $"rawset(_G,'_stellar_vblk',false) pcall(function() local td=(Z.DataMgr).Get('team_data') if td then local b=td:GetBlockVoiceState({charId}) if b==true then rawset(_G,'_stellar_vblk',true) end end end)");
        var blocked = _services.Lua.TryReadGlobalBool("_stellar_vblk", out var v) && v;
        // Sync the local mirror so the row icon self-corrects on any menu open — incl. a block/unblock done in
        // the game's native voice HUD (which never routed through SetMemberVoiceBlocked).
        if (blocked) _blockedVoice.Add(charId); else _blockedVoice.Remove(charId);
        return blocked;
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
