using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Send-side rule for buff rows (spec 2026-09-05 § 4.2). Capture is unconditional — EVERY buff row is
/// written to disk; this decides only which TRACK it lands in, i.e. what is ever UPLOADED. <c>true</c>
/// routes to the uploaded <c>buff</c> track: a buff a PLAYER put on someone ELSE (a party member, or any
/// monster — player-cast debuffs; boss debuffs are the ones rDPS uses, trash-mob debuffs are harmless
/// extra rows), or anything on the local player (the sampler's audit trail). <c>false</c> routes to the
/// disk-only <c>buffx</c> track (<see cref="SpoolCodec.TrackBuffRejected"/>), which no upload path ever
/// posts: monster self-buffs, monster debuffs on players, other players' self-procs, firer-less buffs.
/// Those rows are retained locally with their run's retention container and die with it.
/// </summary>
internal static class BuffUploadFilter
{
    /// <summary>Send-side admission (doctrine: capture everything, gate the SEND). <paramref name="firerOwner"/>
    /// is the firer RESOLVED TO ITS OWNER (<see cref="SummonOwnerMap.OwnerOf"/>) — a Battle Imagine's buff on a
    /// teammate is a PLAYER's external buff (spec § 6.8); before P2 the bare summon id failed IsPlayer here and
    /// the row was routed to the disk-only track.</summary>
    internal static bool ShouldUpload(EntityId firerOwner, EntityId target, EntityId self)
    {
        if (target == self) return true;
        if (!firerOwner.IsPlayer || firerOwner == target) return false;
        return target.IsPlayer || target.IsMonster;
    }
}
