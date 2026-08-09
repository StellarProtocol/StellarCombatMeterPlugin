using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Event-driven social-snapshot capture. Populating a member's social snapshot (name/guild/portrait
// URLs/collections) requires the game's AsyncGetSocialData RPC, which the plugin drives via
// IEntityDetail.RefreshSocialSnapshot — WITHOUT the player opening any in-game ID card. We fire it
// once per member on join (and self on login), plus a one-time catch-up over an already-formed party.
// No polling loop. RefreshSocialSnapshot is main-thread-only.
public sealed partial class Plugin
{
    private const long SocialReqFloorMs = 60_000;          // hard min interval between RPCs per uid
    private readonly Dictionary<long, long> _socialReqAtMs = new();   // uid -> last refresh request ms
    private readonly Queue<EntityId> _socialCatchup = new();          // staggered one-time roster catch-up

    private void WireSocialCapture()
    {
        _services.ClientState.Login   += OnSocialLogin;
        _services.PartyEvents.MemberJoined += OnSocialMemberJoined;
        _services.PartyEvents.MemberUpdated += OnSocialMemberUpdated;
        // Catch-up: if we loaded while already in a party, refresh everyone once (drained a few/frame).
        foreach (var m in _services.PartyRoster.Members) _socialCatchup.Enqueue(m.EntityId);
        // Self may already be in-world at load time.
        var self = _services.CombatSnapshot.LocalEntityId;
        if (!self.IsNone) _socialCatchup.Enqueue(self);
    }

    private void UnwireSocialCapture()
    {
        _services.ClientState.Login   -= OnSocialLogin;
        _services.PartyEvents.MemberJoined -= OnSocialMemberJoined;
        _services.PartyEvents.MemberUpdated -= OnSocialMemberUpdated;
    }

    private void OnSocialLogin()
    {
        var self = _services.CombatSnapshot.LocalEntityId;
        if (!self.IsNone) RequestSocialRefresh(self, isUpdate: false);
        foreach (var m in _services.PartyRoster.Members) _socialCatchup.Enqueue(m.EntityId);
    }

    private void OnSocialMemberJoined(PartyMember m) => RequestSocialRefresh(m.EntityId, isUpdate: false);

    private void OnSocialMemberUpdated(PartyMember m) => RequestSocialRefresh(m.EntityId, isUpdate: true);

    // Drained from the existing per-frame OnUpdate — at most 2 refreshes per frame to avoid a Lua spike.
    private void DrainSocialCatchup()
    {
        for (var i = 0; i < 2 && _socialCatchup.Count > 0; i++)
            RequestSocialRefresh(_socialCatchup.Dequeue(), isUpdate: false);
    }

    // Fires the game's social-data RPC for one player, subject to the per-uid floor. Main-thread only.
    private void RequestSocialRefresh(EntityId e, bool isUpdate)
    {
        if (e.IsNone || !e.IsPlayer) return;
        var uid = e.Value;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_socialReqAtMs.TryGetValue(uid, out var last) && now - last < SocialReqFloorMs) return;
        // MemberUpdated fires often; only re-capture when we don't yet have a usable snapshot,
        // or it's been a while since the last refresh for this uid (catch level/portrait/guild changes
        // without churning the Lua VM during busy parties / spam-clears).
        const long updateStaleMs = 5 * 60_000;
        if (isUpdate && _socialReqAtMs.TryGetValue(uid, out var lastReq))
        {
            var snap = _services.EntityDetail.GetSocialSnapshot(e);
            var haveUrls = snap is not null && (!string.IsNullOrEmpty(snap.ProfileUrl) || !string.IsNullOrEmpty(snap.HalfBodyUrl));
            if (haveUrls && now - lastReq < updateStaleMs) return;
        }
        _socialReqAtMs[uid] = now;
        try { _services.EntityDetail.RefreshSocialSnapshot(e); }
        catch (Exception ex) { _services.Log.Warning($"[CombatMeter.Social] refresh threw: {ex.Message}"); }
    }
}
