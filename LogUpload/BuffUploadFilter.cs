using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Send-side rule for buff rows (spec 2026-09-05 § 4.2). Capture is unconditional; this only decides
/// what is UPLOADED: a buff a PLAYER put on someone ELSE (a party member, or any monster — player-cast
/// debuffs; boss debuffs are the ones rDPS uses, trash-mob debuffs are harmless extra rows), or anything
/// on the local player (the sampler's audit trail). Monster self-buffs, monster debuffs on players, other
/// players' self-procs, and firer-less buffs stay on disk and are never sent.
/// </summary>
internal static class BuffUploadFilter
{
    internal static bool ShouldUpload(EntityId firer, EntityId target, EntityId self)
    {
        if (target == self) return true;
        if (!firer.IsPlayer || firer == target) return false;
        return target.IsPlayer || target.IsMonster;
    }
}
