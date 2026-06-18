using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Resolves the two trailing Battle-Imagine slots for a meter row. Identity is the same for
// every entity: the equipped skill loadout (ICombatLookup.GetSkillLevels) filtered to skills
// that are Battle Imagines (SlotPositionId 7/8, via IGameDataResonance.GetImagineForSkill).
// Cooldown/charges differ: self is authoritative (LocalCooldowns matched by skill id); other
// players are best-effort inferred from observed Resonance-Skill casts (ResonanceTracker).
public sealed partial class Plugin
{
    // (Imagine0, Imagine1) for a row. Identity = first 2 imagine skills in the loadout.
    private (ImagineSlot, ImagineSlot) ResolveImagines(EntityId id, bool isSelf)
    {
        ImagineSlot slot0 = ImagineSlot.None, slot1 = ImagineSlot.None;
        int found = 0;
        foreach (var sl in _services.CombatLookup.GetSkillLevels(id))
        {
            if (_services.ResonanceData.GetImagineForSkill(sl.SkillId) is not { } info) continue;
            var slot = Slot(id, info, isSelf);
            if (found == 0) slot0 = slot;
            else { slot1 = slot; break; }
            found++;
        }
        return (slot0, slot1);
    }

    private ImagineSlot Slot(EntityId id, ImagineInfo info, bool isSelf)
    {
        object? icon = _services.GameAssets.LoadImagineIcon(info.SkillId, out var uv);
        return isSelf ? SelfSlot(info, icon, uv) : OtherSlot(id, info, icon, uv);
    }

    // Self slot: cooldown TIMING + cast detection from LocalCooldowns (both authoritative, haste-correct).
    private ImagineSlot SelfSlot(ImagineInfo info, object? icon, UvRect uv)
    {
        int max = info.ChargeCount < 1 ? 1 : info.ChargeCount;
        foreach (var cd in _services.CombatSnapshot.LocalCooldowns)
        {
            // Cooldowns are keyed by the leveled skill_level_id; resolve it to its base via the same
            // authoritative map used for identity (SkillFightLevelTable.SkillId) and match on base.
            if (cd.SkillId != info.SkillId
                && _services.ResonanceData.GetImagineForSkill(cd.SkillId)?.SkillId != info.SkillId) continue;
            return BuildSelfSlot(cd, info, icon, uv);
        }
        // No active cooldown row → ready (full charges, no sweep).
        return new ImagineSlot(icon, uv, 0f, 0, max, max, false);
    }

    // Shared cooldown math (same as CooldownBar): time-to-FULL across charges + sequential-recharge sim, with the
    // flat ~10% default cooldown reduction + imagine cd-acceleration (Tina etc.). Haste does NOT affect cooldown.
    internal const int ImagineCdReductionAttr = 11760;   // 技能冷却缩减万分比 (per-10000; ~1000 = the 10% default)
    private const int ImagineCdAccelAttr = 11980;   // 独立计时战斗资源冷却加速万分比
    private readonly Stellar.Abstractions.Domain.GameData.ImagineCooldownCalc _imgCalc = new();

    private ImagineSlot BuildSelfSlot(SkillCooldown cd, ImagineInfo info, object? icon, UvRect uv)
    {
        long now = _services.CombatSnapshot.ServerNowMs;
        int max = info.ChargeCount < 1 ? 1 : info.ChargeCount;
        long red = _services.PlayerStats.TryGetAttribute(ImagineCdReductionAttr) ?? -1;
        float redFrac = red >= 0 ? red / 10000f : 0.10f;
        long accel = _services.PlayerStats.TryGetAttribute(ImagineCdAccelAttr) ?? 0;
        float reduction = redFrac + accel / 10000f;
        int perCharge = Stellar.Abstractions.Domain.GameData.ImagineCooldownCalc.EffectiveDuration(
            cd.DurationMs, cd.ValidCdTimeMs, reduction);
        if (perCharge <= 0) perCharge = info.ChargeCount > 1 ? info.RechargeMs : info.CooldownMs;
        var rc = _imgCalc.Update(info.SkillId, cd.BeginTimeMs, perCharge, max, now);
        return rc.Active
            ? new ImagineSlot(icon, uv, Clamp01(rc.FullFraction), MsToWholeSeconds(rc.ToFullMs), rc.ChargesAvailable, max, false)
            : new ImagineSlot(icon, uv, 0f, 0, max, max, false);
    }

    // Best-effort inferred slot for another player (keyed by skill id; ready if no cast observed).
    private ImagineSlot OtherSlot(EntityId id, ImagineInfo info, object? icon, UvRect uv)
    {
        int max = info.ChargeCount < 1 ? 1 : info.ChargeCount;
        int rechargeMs = info.ChargeCount > 1 ? info.RechargeMs : info.CooldownMs;
        var (chargesAvail, remainingMs) = _resTracker.State(
            id, info.SkillId, info.ChargeCount, rechargeMs, _services.CombatSnapshot.ServerNowMs);
        float frac = rechargeMs > 0 && remainingMs > 0 ? (float)remainingMs / rechargeMs : 0f;
        return new ImagineSlot(icon, uv, Clamp01(frac), MsToWholeSeconds(remainingMs), chargesAvail, max, true);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    private static int MsToWholeSeconds(int ms) => ms <= 0 ? 0 : (ms + 999) / 1000;
}
