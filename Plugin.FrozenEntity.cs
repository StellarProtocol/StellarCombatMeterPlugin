using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.PluginContracts;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    // Convert the archived EntitySnapshot (parallel ID arrays) into a FrozenEntity for the inter-plugin
    // IFrozenEntityViewer contract — IDs only, mirroring the live IEntityDetail/ICombatLookup shape so the
    // Entity Inspector can swap its data source. Names/icons re-resolve live at render, same as the Session
    // Snapshot fallback. Fashion dyes are rebuilt from the flattened RGBA stream (FashionDyeCounts per entry).
    private static FrozenEntity BuildFrozenEntity(EntityId id, string sessionLabel, EntitySnapshot snap)
    {
        var attrs = new Dictionary<int, long>(snap.AttrIds.Length);
        for (var i = 0; i < snap.AttrIds.Length && i < snap.AttrValues.Length; i++) attrs[snap.AttrIds[i]] = snap.AttrValues[i];

        var gear = new List<EquippedItem>(snap.GearSlots.Length);
        for (var i = 0; i < snap.GearSlots.Length && i < snap.GearItemIds.Length; i++)
            gear.Add(new EquippedItem(snap.GearSlots[i], snap.GearItemIds[i]));

        var skills = new List<SkillLevel>(snap.SkillIds.Length);
        for (var i = 0; i < snap.SkillIds.Length && i < snap.SkillLevels.Length && i < snap.SkillTiers.Length; i++)
            skills.Add(new SkillLevel(snap.SkillIds[i], snap.SkillLevels[i], snap.SkillTiers[i]));

        var fashion = new List<FashionEntry>(snap.FashionSlots.Length);
        var dyeCursor = 0;
        for (var i = 0; i < snap.FashionSlots.Length && i < snap.FashionIds.Length; i++)
        {
            int count = i < snap.FashionDyeCounts.Length ? snap.FashionDyeCounts[i] : 0;
            var dyes = new List<ColorRgba>(count);
            for (var d = 0; d < count && dyeCursor + 3 < snap.FashionDyes.Length; d++)
            {
                dyes.Add(new ColorRgba(snap.FashionDyes[dyeCursor], snap.FashionDyes[dyeCursor + 1],
                    snap.FashionDyes[dyeCursor + 2], snap.FashionDyes[dyeCursor + 3]));
                dyeCursor += 4;
            }
            fashion.Add(new FashionEntry(snap.FashionSlots[i], snap.FashionIds[i], dyes.ToArray()));
        }

        return new FrozenEntity(
            Id: id,
            SessionLabel: sessionLabel,
            Name: snap.Name ?? string.Empty,
            FightPoint: snap.FightPoint,
            Hp: snap.Hp,
            MaxHp: snap.MaxHp,
            TeamId: snap.TeamId,
            Attributes: attrs,
            Gear: gear,
            Skills: skills,
            Fashion: fashion);
    }
}
