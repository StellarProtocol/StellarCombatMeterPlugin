using System;
using Stellar.Abstractions.Domain;   // MicrophoneStatus (own-voice cycle)

namespace Stellar.CombatMeter;

// Party leader actions driven through the framework Lua bridge (services.Lua).
public sealed partial class Plugin
{
    // ── Party leader actions ─────────────────────────────────────────────────

    private const float ReadyCheckCooldownS = 25f;
    private float _readyCheckCooldown;

    internal string ReadyCheckLabel()
        => _readyCheckCooldown > 0f
            ? $"{(int)Math.Ceiling(_readyCheckCooldown)}"
            : "";

    private void TickReadyCheckCooldown(float dt)
    {
        if (_readyCheckCooldown > 0f)
            _readyCheckCooldown = Math.Max(0f, _readyCheckCooldown - dt);
    }

    private void TogglePunctuate()
        => _services.Lua.DoString("pcall(function() local k='main_copy_punctuate' if (Z.UIMgr):IsActive(k) then (Z.UIMgr):CloseView(k) else (Z.UIMgr):OpenView(k) end end)");

    private void LeaderConvene()
        => _services.Lua.DoString("pcall(function() (Z.CoroUtil).create_coro_xpcall(function() local vm=(Z.VMMgr).GetVM('team') if vm then local cs=(Z.CancelSource).Rent() vm.AsyncTeamLeaderCall(cs:CreateToken()) end end, function() end)() end)");

    // Leader-initiated team countdown (the game's own "pull timer"). Mirrors the game's
    // team_view btn_countdown → TeamVM.AsyncStartCountDown → WorldProxy.StartCountDown
    // (net msg zproto.World.StartCountDown, empty request). Plain fire-and-forget async
    // click, same shape as LeaderConvene — no cooldown, server broadcasts the countdown.
    private void LeaderCountdown()
        => _services.Lua.DoString("pcall(function() (Z.CoroUtil).create_coro_xpcall(function() local vm=(Z.VMMgr).GetVM('team') if vm then local cs=(Z.CancelSource).Rent() vm.AsyncStartCountDown(cs:CreateToken()) end end, function() end)() end)");

    private bool  _readyCheckResultPending;
    private float _readyCheckResultTimeoutS;
    private const float ReadyCheckResultTimeoutS = 5f;
    // Two sentinels: _done = any non-nil when coroutine finishes; _ok = non-nil only on success (ret==0).
    // Only null vs non-null matters — no numeric extraction needed.
    private const string RcDoneKey = "_stellar_rc_done";
    private const string RcOkKey   = "_stellar_rc_ok";

    private void LeaderReadyCheck()
    {
        if (_readyCheckCooldown > 0f) return;
        if (_readyCheckResultPending) return;
        _services.Lua.DoString($"rawset(_G,'{RcDoneKey}',nil) rawset(_G,'{RcOkKey}',nil)");
        _services.Lua.DoString(
            "pcall(function() (Z.CoroUtil).create_coro_xpcall(function()" +
            $" local vm=require('ui.view_model.dungeon.dungeon_prepare_vm')" +
            $" if vm then local cs=(Z.CancelSource).Rent()" +
            $" local ret=vm.AsyncLeaderReadyCheck(cs:CreateToken())" +
            $" rawset(_G,'{RcDoneKey}',true)" +
            $" if ret==nil or ret==0 then rawset(_G,'{RcOkKey}',true) end" +
            $" else rawset(_G,'{RcDoneKey}',true) end" +
            $" end,function() rawset(_G,'{RcDoneKey}',true) end)() end)");
        _readyCheckResultPending = true;
        _readyCheckResultTimeoutS = ReadyCheckResultTimeoutS;
    }

    // Polls the Lua sentinels written by the coroutine. Fires at most once per button press.
    internal void TickReadyCheckResult(float dt)
    {
        if (!_readyCheckResultPending) return;
        _readyCheckResultTimeoutS -= dt;
        // Sentinels are set via rawset(_G, key, true) — the coroutine only ever writes `true` or nil,
        // so a boolean-true read is exactly equivalent to the old "non-nil" check.
        bool done = _services.Lua.TryReadGlobalBool(RcDoneKey, out var doneVal) && doneVal;
        bool timedOut = _readyCheckResultTimeoutS <= 0f;
        if (!done && !timedOut) return;   // coroutine still running

        _readyCheckResultPending = false;
        bool ok = _services.Lua.TryReadGlobalBool(RcOkKey, out var okVal) && okVal;
        if (ok)
        {
            OnReadyCheckPressed();
            _readyCheckCooldown = ReadyCheckCooldownS;
        }
        // on failure the game shows its own notice tip — nothing extra needed here
    }

    // Own team-voice cycle Speak -> Listen -> Mute -> Speak, replicating team_view.switchVoiceState EXACTLY.
    // Driven by a PLUGIN-tracked position, NOT GetMicStatus: the wire/server mic status lags and is echoed a
    // frame late, so reading it kept the cycle re-issuing the same transition (stuck at Mute, then stuck at
    // Listen — owner reports 2026-09-24). We mirror the game's OWN curVoiceState the way its Ctrl+I does, so the
    // emitted VM calls are byte-identical to Ctrl+I (which the owner confirmed cycles + syncs both icons).
    // ETeamVoiceState: MicVoice=Speak, SpeakerVoice=Listen, CloseVoice=Mute. Position: 0=Speak,1=Listen,2=Mute.
    private int  _ownVoicePos;
    private bool _ownVoiceKnown;

    private void CycleOwnVoiceMode()
    {
        if (!_services.Lua.Ready) return;
        if (!_ownVoiceKnown)   // lazy seed from the live icon state so the FIRST click advances from the current mode
        {
            _ownVoicePos = _services.PartyRoster.GetMicStatus(_services.CombatSnapshot.LocalEntityId.Uid) switch
            {
                MicrophoneStatus.Closed => 0, MicrophoneStatus.OpenSpeaker => 2, _ => 1,
            };
            _ownVoiceKnown = true;
        }
        string action;
        switch (_ownVoicePos)
        {
            case 1:  action = "vm.CloseTeamVoice() vm.SetMicrophoneStatus((E.ETeamVoiceState).CloseVoice)"; _ownVoicePos = 2; break;                 // Listen -> Mute (send CloseVoice, else server keeps Listen)
            case 2:  action = "local o=vm.OpenTeamMic() if o then vm.SetMicrophoneStatus((E.ETeamVoiceState).MicVoice) end"; _ownVoicePos = 0; break;   // Mute -> Speak
            default: action = "vm.OpenTeamSpeaker()"; _ownVoicePos = 1; break;                                                                       // Speak -> Listen
        }
        _services.Lua.DoString($"pcall(function() local vm=(Z.VMMgr).GetVM('team') if vm and vm.CheckIsInTeam() then {action} end end)");
    }
}
