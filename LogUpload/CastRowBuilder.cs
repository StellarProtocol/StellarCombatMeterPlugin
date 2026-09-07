using System.Globalization;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>A player's CAST, read off its AOI attribute stream (rDPS phase 2, spec § 6.1.2; decision D1). The game
/// writes <c>AttrSkillId</c> (EAttrType 100) on every cast start — the signal the reference decoder ZDPS fires its
/// Skill Cast Timeline on — and the framework announces every player's scalar attr writes as
/// <see cref="CombatEvent.EntityAttributesChanged"/>. The framework's own <c>SkillUsed</c> event (AoiSyncDelta field 4)
/// has never fired on the live wire (docs/recon/imagine-cast-detection.md § STILL FALSIFIED), so this is the cast
/// source. The row is the EXISTING <c>skill</c> wire row, phase <see cref="SkillEventPhase.Begin"/>, stamped like the
/// packet — the worker's cast model needs no new shape.</summary>
internal static class CastRowBuilder
{
    /// <summary><c>EAttrType.AttrSkillId</c> — the skill an entity started casting.</summary>
    internal const int AttrSkillId = 100;

    internal static SkillEvent? Project(CombatEvent.EntityAttributesChanged ac)
    {
        for (var i = 0; i < ac.Attrs.Count; i++)
        {
            var a = ac.Attrs[i];
            if (a.AttrId != AttrSkillId) continue;
            if (a.Value <= 0 || a.Value > int.MaxValue) return null;
            return new SkillEvent(ac.TimestampMs, ac.TargetId.Value.ToString(CultureInfo.InvariantCulture), (int)a.Value, (int)SkillEventPhase.Begin);
        }
        return null;
    }
}
