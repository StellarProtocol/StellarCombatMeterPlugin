using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>
/// Frozen per-player entity snapshot captured at archive time — the IDs (only) of everything the Entity Inspector
/// shows for a player, stored as parallel primitive arrays so it serialises through the hand-rolled
/// <see cref="HistoryStore"/> the same way <see cref="SourceSeries"/> does. Names / icons / quality / GS are
/// re-resolved LIVE from the static tables at render time — this never freezes a display string.
///
/// Parallel-array contract (each pair/triple is index-aligned and equal length on capture; the reader clamps to
/// the shortest on load so a truncated file degrades instead of throwing):
///   AttrIds[i]      ↔ AttrValues[i]                                    — non-zero broadcast attrs only
///   GearSlots[i]    ↔ GearItemIds[i]                                   — equipped slot → item id
///   SkillIds[i]     ↔ SkillLevels[i] ↔ SkillTiers[i]                   — equipped loadout
///   FashionSlots[i] ↔ FashionIds[i] ↔ FashionDyeCounts[i]             — worn cosmetics; dyes flattened
///   FashionDyes is the flattened RGBA dye stream (4 floats per colour); FashionDyeCounts[i] colours belong to
///   fashion entry i, consumed in order.
/// </summary>
internal sealed class EntitySnapshot
{
    public string? Name;
    public long FightPoint;
    public long Hp;
    public long MaxHp;
    public long TeamId;

    public int[]  AttrIds    = System.Array.Empty<int>();
    public long[] AttrValues = System.Array.Empty<long>();

    public int[] GearSlots   = System.Array.Empty<int>();
    public int[] GearItemIds = System.Array.Empty<int>();

    public int[] SkillIds    = System.Array.Empty<int>();
    public int[] SkillLevels = System.Array.Empty<int>();
    public int[] SkillTiers  = System.Array.Empty<int>();

    public int[]   FashionSlots     = System.Array.Empty<int>();
    public int[]   FashionIds       = System.Array.Empty<int>();
    public int[]   FashionDyeCounts = System.Array.Empty<int>();
    public float[] FashionDyes      = System.Array.Empty<float>();   // flattened RGBA, 4 per colour
}

public sealed partial class Plugin
{
    // Archive-time (cold) capture: snapshot every archived PLAYER source from the live services. Allocation is
    // fine here — it mirrors DeepCopyStats/FreezeTimelines and fires only on a scene change or the Archive button.
    private Dictionary<EntityId, EntitySnapshot> SnapshotEntities()
    {
        var snaps = new Dictionary<EntityId, EntitySnapshot>();
        foreach (var id in _stats.Keys)
        {
            if (!id.IsPlayer) continue;
            snaps[id] = CaptureEntity(id);
        }
        return snaps;
    }

    private EntitySnapshot CaptureEntity(EntityId id)
    {
        var vitals = _services.CombatLookup.GetVitals(id);
        // Capture the display name via the SAME resolution chain the history rows use (PlayerState → roster →
        // combat cache → synthesized), so a frozen snapshot header matches what the table shows ("Ribery") instead
        // of "Unknown" — GetEntityName alone returns "Unknown" for the local player / weak cases. Captured at
        // archive time while the entity is still live, which is correct.
        EntityId self = _services.CombatSnapshot.LocalEntityId;
        var snap = new EntitySnapshot
        {
            Name       = EntityLabel.Resolve(id, self, _services.PlayerState, _services.CombatLookup, _services.PartyRoster.Members),
            FightPoint = _services.CombatLookup.GetFightPoint(id),
            Hp         = vitals.IsKnown ? vitals.Hp : 0,
            MaxHp      = vitals.IsKnown ? vitals.MaxHp : 0,
            TeamId     = _services.CombatLookup.GetTeamId(id),
        };
        CaptureAttributes(id, snap);
        CaptureGear(id, snap);
        CaptureSkills(id, snap);
        CaptureFashion(id, snap);
        return snap;
    }

    // Non-zero broadcast attrs only (self ~130 ids, others fewer).
    private void CaptureAttributes(EntityId id, EntitySnapshot snap)
    {
        var attrs = _services.EntityDetail.GetAttributes(id);
        var ids = new List<int>(attrs.Count);
        var values = new List<long>(attrs.Count);
        foreach (var (attrId, value) in attrs)
        {
            if (value == 0) continue;
            ids.Add(attrId);
            values.Add(value);
        }
        snap.AttrIds = ids.ToArray();
        snap.AttrValues = values.ToArray();
    }

    private void CaptureGear(EntityId id, EntitySnapshot snap)
    {
        var gear = _services.EntityDetail.GetEquipment(id);
        snap.GearSlots = new int[gear.Count];
        snap.GearItemIds = new int[gear.Count];
        for (var i = 0; i < gear.Count; i++)
        {
            snap.GearSlots[i] = gear[i].Slot;
            snap.GearItemIds[i] = gear[i].ItemId;
        }
    }

    private void CaptureSkills(EntityId id, EntitySnapshot snap)
    {
        var skills = _services.CombatLookup.GetSkillLevels(id);
        snap.SkillIds = new int[skills.Count];
        snap.SkillLevels = new int[skills.Count];
        snap.SkillTiers = new int[skills.Count];
        for (var i = 0; i < skills.Count; i++)
        {
            snap.SkillIds[i] = skills[i].SkillId;
            snap.SkillLevels[i] = skills[i].Level;
            snap.SkillTiers[i] = skills[i].Tier;
        }
    }

    private void CaptureFashion(EntityId id, EntitySnapshot snap)
    {
        var fashion = _services.EntityDetail.GetFashion(id);
        snap.FashionSlots = new int[fashion.Count];
        snap.FashionIds = new int[fashion.Count];
        snap.FashionDyeCounts = new int[fashion.Count];
        var dyes = new List<float>();
        for (var i = 0; i < fashion.Count; i++)
        {
            var f = fashion[i];
            snap.FashionSlots[i] = f.Slot;
            snap.FashionIds[i] = f.FashionId;
            var fdyes = f.Dyes ?? FashionEntry.NoDyes;
            snap.FashionDyeCounts[i] = fdyes.Length;
            foreach (var c in fdyes) { dyes.Add(c.R); dyes.Add(c.G); dyes.Add(c.B); dyes.Add(c.A); }
        }
        snap.FashionDyes = dyes.ToArray();
    }
}
