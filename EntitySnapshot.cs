using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Inventory;

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

    // Per-piece instance detail — SELF ONLY (IInventory.GetSelfGear; other players broadcast ids only).
    // Index-aligned across the Gd* arrays; GdRollCounts[i] quads of GdRolls ([kind, attrId, value, pct])
    // belong to piece i, consumed in order. Rolls are RESOLVED at capture via the equip attr-lib tables
    // (value = pct·(Max−Min)/100 + Min) so consumers never need the lib tables.
    // Kind: 0 basic, 1 advanced, 2 recast, 3 rare, 4 gem effect (flat; pct = 0).
    // GdEnchantId is the RESOLVED gem ITEM id (name carries the display level, "… Sigil Lv.2") —
    // the wire enchant typeId/level pair is an internal index that reads as the wrong gem/level.
    public int[] GdSlots      = System.Array.Empty<int>();
    public int[] GdQuality    = System.Array.Empty<int>();
    public int[] GdRefine     = System.Array.Empty<int>();
    public int[] GdItemLv     = System.Array.Empty<int>();           // perfection_level (semantics uncertain)
    public int[] GdBt         = System.Array.Empty<int>();           // breakthrough stage — display Lv = stage EquipGs
    public int[] GdPerfVal    = System.Array.Empty<int>();
    public int[] GdPerfMax    = System.Array.Empty<int>();
    public int[] GdEnchantId  = System.Array.Empty<int>();
    public int[] GdEnchantLv  = System.Array.Empty<int>();           // raw wire level — fallback only
    public int[] GdRollCounts = System.Array.Empty<int>();
    public int[] GdRolls      = System.Array.Empty<int>();           // flattened quads
}

public sealed partial class Plugin
{
    // Archive-time capture: hand each archived PLAYER source its sticky last-known-good snapshot (frozen while
    // the player was live in AOI — see Plugin.EntitySnapshotSticky). Reading the live services HERE is wrong:
    // on a scene change the framework tears down the AOI caches before ManualArchive runs, so a live capture
    // returns empty for everyone but self. Fall back to a live capture only for a source we somehow never stuck
    // (best effort). Ownership transfers to the history entry — ManualArchive() calls Clear() immediately after,
    // dropping the live refs, so there's no aliasing.
    private Dictionary<EntityId, EntitySnapshot> SnapshotEntities()
    {
        var snaps = new Dictionary<EntityId, EntitySnapshot>();
        foreach (var id in _stats.Keys)
        {
            if (!id.IsPlayer) continue;
            snaps[id] = _entitySnaps.TryGetValue(id, out var sticky) ? sticky : CaptureEntity(id);
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
        if (id == self) CaptureSelfGearDetail(snap);
        CaptureSkills(id, snap);
        CaptureFashion(id, snap);
        return snap;
    }

    // Self-only per-piece instance detail (rolls / refine / perfection / gem) from the local
    // inventory — other players never broadcast it. Feeds the upload's `gearDetail` block.
    private void CaptureSelfGearDetail(EntitySnapshot snap)
    {
        var gear = _services.Inventory.GetSelfGear();
        var n = gear.Count;
        snap.GdSlots = new int[n]; snap.GdQuality = new int[n]; snap.GdRefine = new int[n];
        snap.GdItemLv = new int[n]; snap.GdBt = new int[n];
        snap.GdPerfVal = new int[n]; snap.GdPerfMax = new int[n];
        snap.GdEnchantId = new int[n]; snap.GdEnchantLv = new int[n];
        snap.GdRollCounts = new int[n];
        var rolls = new List<int>();
        for (var i = 0; i < n; i++)
        {
            var g = gear[i];
            snap.GdSlots[i] = g.Slot;
            snap.GdQuality[i] = g.Quality;
            snap.GdRefine[i] = g.RefineLevel;
            snap.GdItemLv[i] = g.Perfection.Level;
            snap.GdBt[i] = g.BreakThroughTime;
            snap.GdPerfVal[i] = g.Perfection.Value;
            snap.GdPerfMax[i] = g.Perfection.Max;
            var count = 0;
            count += AppendRolls(rolls, 0, g.Attrs.Basic);
            count += AppendRolls(rolls, 1, g.Attrs.Advanced);
            count += AppendRolls(rolls, 2, g.Attrs.Recast);
            count += AppendRolls(rolls, 3, g.Attrs.Rare);
            // Gem: resolve (typeId, internal level) → gem ITEM (display name carries "Lv.<n>") +
            // its flat effects, mirroring EntityInspector's Item Detail. Raw pair kept as fallback.
            if (g.Enchant is { } en)
            {
                snap.GdEnchantLv[i] = en.Level;
                if (_services.GameData.Equip.GetEnchantItem(en.ItemTypeId, en.Level) is { } gem)
                {
                    snap.GdEnchantId[i] = gem.GemItemId;
                    foreach (var eff in gem.Effects)
                    {
                        rolls.Add(4); rolls.Add(eff.AttrId); rolls.Add(eff.Value); rolls.Add(0);
                        count++;
                    }
                }
                else snap.GdEnchantId[i] = en.ItemTypeId;
            }
            snap.GdRollCounts[i] = count;
        }
        snap.GdRolls = rolls.ToArray();
    }

    // Expand each (lib ROW id, percentile) roll into resolved (attrId, value) quads — the game's own
    // formula (floor(pct·(Max−Min)/100 + Min), verified against the live gear sheet by EntityInspector).
    // School-sourced rolls resolve against the v2 school row table (row-id spaces collide).
    private int AppendRolls(List<int> into, int kind, IReadOnlyList<GearAttrRoll> src)
    {
        var n = 0;
        foreach (var r in src)
        {
            var entries = r.School
                ? _services.GameData.Equip.GetSchoolAttrLibRow(r.LibRowId)
                : _services.GameData.Equip.GetAttrLibRow(r.LibRowId);
            foreach (var e in entries)
            {
                var value = r.Percentile * (long)(e.Max - e.Min) / 100 + e.Min;
                into.Add(kind); into.Add(e.AttrId); into.Add((int)value); into.Add(r.Percentile);
                n++;
            }
        }
        return n;
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
