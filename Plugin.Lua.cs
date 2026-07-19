using System;
using System.Reflection;

namespace Stellar.CombatMeter;

// Minimal Lua bridge — resolves LuaInterface.LuaState.mainState once, then wraps DoString.
public sealed partial class Plugin
{
    private object?     _luaState;
    private MethodInfo? _luaDoString;
    private MethodInfo? _luaGetItem;   // get_Item(string) or GetNumber — reads a Lua global by name

    private void EnsureLuaState()
    {
        if (_luaDoString != null) return;

        var lsType = FindLuaType("LuaInterface.LuaState");
        if (lsType != null)
        {
            _luaState =
                lsType.GetProperty("mainState", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
                ?? lsType.GetField("mainState",  BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
        }

        if (_luaState is null)
        {
            var clientType = FindLuaType("LuaClient");
            var clientInst = clientType
                ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (clientInst != null)
            {
                var t = clientInst.GetType();
                _luaState =
                    t.GetProperty("luaState", BindingFlags.Instance | BindingFlags.Public)?.GetValue(clientInst)
                    ?? t.GetField("luaState",   BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(clientInst);
            }
        }

        if (_luaState is null) { _services.Log.Warning("[CombatMeter] LuaState not found"); return; }

        foreach (var m in _luaState.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.IsGenericMethod) continue;
            var ps = m.GetParameters();
            if (_luaDoString is null && m.Name == "DoString" && ps.Length >= 2
                    && ps[0].ParameterType == typeof(string) && m.ReturnType == typeof(void))
                _luaDoString = m;
        }

        // Priority: GetNumber > get_Item > GetString. Only check param count (not type) — IL2CPP
        // may expose get_Item(object) not get_Item(string), so a type check would skip it.
        foreach (var candidate in new[] { "GetNumber", "get_Item", "GetString" })
        {
            foreach (var m in _luaState.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != candidate || m.IsGenericMethod) continue;
                if (m.GetParameters().Length != 1) continue;
                _luaGetItem = m;
                break;
            }
            if (_luaGetItem != null) break;
        }
    }

    // Read a Lua global by name. Returns null while the key is nil / not yet set.
    private object? ReadLuaRaw(string key)
    {
        EnsureLuaState();
        if (_luaState is null || _luaGetItem is null) return null;
        try { return _luaGetItem.Invoke(_luaState, new object[] { key }); }
        catch { return null; }
    }

    private static int ExtractInt(object? v)
    {
        if (v is double d) return (int)d;
        if (v is float  f) return (int)f;
        if (v is int    i) return i;
        if (v is long   l) return (int)l;
        if (v is string s && double.TryParse(s, out var sd)) return (int)sd;
        // IL2CPP tolua# boxes Lua numbers as Il2CppSystem.Double or similar — use Convert as last resort.
        try { return (int)Convert.ToDouble(v); } catch { return -1; }
    }

    private void CallLua(string chunk)
    {
        EnsureLuaState();
        if (_luaState is null || _luaDoString is null) return;
        try { _luaDoString.Invoke(_luaState, new object[] { chunk, "plugin" }); }
        catch (Exception ex) { _services.Log.Warning($"[CombatMeter] CallLua threw: {ex.Message}"); }
    }

    private static Type? FindLuaType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t != null) return t;
        }
        return null;
    }

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
        => CallLua("pcall(function() local k='main_copy_punctuate' if (Z.UIMgr):IsActive(k) then (Z.UIMgr):CloseView(k) else (Z.UIMgr):OpenView(k) end end)");

    private void LeaderConvene()
        => CallLua("pcall(function() (Z.CoroUtil).create_coro_xpcall(function() local vm=(Z.VMMgr).GetVM('team') if vm then local cs=(Z.CancelSource).Rent() vm.AsyncTeamLeaderCall(cs:CreateToken()) end end, function() end)() end)");

    // Leader-initiated team countdown (the game's own "pull timer"). Mirrors the game's
    // team_view btn_countdown → TeamVM.AsyncStartCountDown → WorldProxy.StartCountDown
    // (net msg zproto.World.StartCountDown, empty request). Plain fire-and-forget async
    // click, same shape as LeaderConvene — no cooldown, server broadcasts the countdown.
    private void LeaderCountdown()
        => CallLua("pcall(function() (Z.CoroUtil).create_coro_xpcall(function() local vm=(Z.VMMgr).GetVM('team') if vm then local cs=(Z.CancelSource).Rent() vm.AsyncStartCountDown(cs:CreateToken()) end end, function() end)() end)");

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
        CallLua($"rawset(_G,'{RcDoneKey}',nil) rawset(_G,'{RcOkKey}',nil)");
        CallLua(
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
        var done = ReadLuaRaw(RcDoneKey);
        bool timedOut = _readyCheckResultTimeoutS <= 0f;
        if (done == null && !timedOut) return;   // coroutine still running

        _readyCheckResultPending = false;
        bool ok = ReadLuaRaw(RcOkKey) != null;
        if (ok)
        {
            OnReadyCheckPressed();
            _readyCheckCooldown = ReadyCheckCooldownS;
        }
        // on failure the game shows its own notice tip — nothing extra needed here
    }
}
