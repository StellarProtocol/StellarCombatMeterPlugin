using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

/// <summary>One equipped module captured from the local <c>IInventory</c> (self-only): slot, the
/// module's config id + quality, and its rolled parts (attrId, value). Plugin-internal capture shape —
/// distinct from any future upload-wire <c>ModuleEntry</c> DTO, which the assembler (Task 3 of the
/// per-class-loadout plan) maps this onto.</summary>
internal sealed record CapturedModule(int Slot, int ConfigId, int Quality, IReadOnlyList<int[]> Parts);

/// <summary>
/// A full snapshot of ONE played class's loadout — gear (ids + self-only rolled detail), modules,
/// skills, fashion, its project name, and active talent stage. Captured self-only, latest-wins per
/// <see cref="ProfessionId"/> (see <see cref="LoadoutCapture"/>). <see cref="GearDetail"/> and
/// <see cref="Fashion"/> reuse the existing upload-wire record shapes (<c>LogUpload/CombatLog.cs</c>)
/// so the future upload assembler can attach these with no conversion.
/// </summary>
internal sealed record CapturedLoadout(
    int ProfessionId,
    string? ProjectName,
    int TalentStageId,
    IReadOnlyList<int[]> Gear,               // [slot, itemId(=ConfigId)]
    IReadOnlyList<GearDetail> GearDetail,     // self-only rolled detail — same shape as CaptureSelfGearDetail
    IReadOnlyList<int[]> Skills,              // [skillId, level, tier]
    IReadOnlyList<Fashion> Fashion,
    IReadOnlyList<CapturedModule> Modules,
    IReadOnlyList<int>? TalentNodes = null);  // actual allocated talent-tree node ids (self-only)

/// <summary>
/// Pure per-class loadout accumulator — a plain latest-wins upsert keyed by professionId. Holds NO
/// service dependency (plain data in, plain data out), so it is unit-testable without an
/// IL2CPP/IPluginServices fake (see <c>LoadoutCaptureTests</c>). <see cref="Plugin"/>'s
/// <c>PollLocalProfession</c>/<c>CaptureActiveClassLoadout</c> below do the live-service reads and hand
/// the finished <see cref="CapturedLoadout"/> to <see cref="Capture"/> — that seam is deliberately thin
/// and untested; only in-game verification exercises IInventory/ILoadout.
/// </summary>
internal sealed class LoadoutCapture
{
    private readonly Dictionary<int, CapturedLoadout> _byProfession = new();

    /// <summary>Stores <paramref name="capture"/> under its <see cref="CapturedLoadout.ProfessionId"/> —
    /// a later capture of a class already seen this run REPLACES the earlier one (latest wins: e.g.
    /// returning to a class played earlier in the run, now with fresher gear/talents).</summary>
    public void Capture(CapturedLoadout capture) => _byProfession[capture.ProfessionId] = capture;

    /// <summary>Clears every captured class. Called at RUN START only — NOT on every archive
    /// (<see cref="Plugin.Clear"/> fires per encounter within the same run and must not drop classes
    /// captured earlier in that same run).</summary>
    public void ResetForRun() => _byProfession.Clear();

    /// <summary>One entry per distinct class played so far this run, for the upload assembler.</summary>
    public IReadOnlyList<CapturedLoadout> Snapshot() => new List<CapturedLoadout>(_byProfession.Values);
}

/// <summary>
/// Per-class loadout capture orchestration (owner design 2026-08-02): a class the player PLAYED was
/// ACTIVE at some point, and <c>IInventory</c> gives the ACTIVE class rich data (rolls via
/// <c>GetSelfGear</c>, modules via <c>GetEquipped</c>/<c>GetModules</c>) that the broadcast per-entity
/// APIs never carry once the player has swapped away from that class — even for self. So this polls
/// the local player's live profession (<c>IPlayerState.Profession</c>, attr 220) and, whenever it
/// changes to a new class, freezes THAT class's current loadout into <see cref="_loadoutCapture"/>,
/// keyed by professionId (latest-wins). Self-only; never touches teammates. The accumulator resets
/// only at RUN START (see <see cref="IsNewLoadoutRun"/>) — NOT by <see cref="Plugin.Clear"/>, which
/// fires on every archive within a run and must not lose classes captured earlier in it. This task
/// (per-class-loadout Task 2) only builds + wires the accumulator; nothing here touches the upload —
/// that is Task 3.
/// </summary>
public sealed partial class Plugin
{
    private readonly LoadoutCapture _loadoutCapture = new();

    // Last profession value POLLED this run (0 = none seen yet / just reset). Distinct from the
    // accumulator's map key set — this only gates re-capture on an unchanged live value.
    private int _lastPolledProfession;

    // Dungeon run-id last observed by the loadout run-boundary check — separate from Plugin.Replay.cs's
    // _replayRunId (different reset semantics; see IsNewLoadoutRun's doc for why).
    private long _loadoutRunId;

    /// <summary>Read-only view of every class captured so far this run, for the upload assembler.</summary>
    internal IReadOnlyList<CapturedLoadout> LoadoutSnapshot() => _loadoutCapture.Snapshot();

    // Throttled tick — called from OnUpdate at the existing ~10 Hz snapshot cadence (Plugin.cs's
    // _snapshotAccum block), not every frame: a profession swap is a rare, deliberate player action.
    private void TickLoadoutCapture()
    {
        TickLoadoutRunBoundary();
        PollLocalProfession();
    }

    /// <summary>True when <paramref name="newRunId"/> marks the START of a run the accumulator hasn't
    /// captured for yet: a non-zero id different from the one last observed. 0→A (entering a run,
    /// including straight from boot) resets a fresh accumulator so any pre-run town-swap captures
    /// don't leak into this run's data; A→B (different non-zero run, e.g. crash/re-enter) resets too.
    /// A→0 (leaving to town) deliberately does NOT reset — a dungeon→town archive still needs to read
    /// this run's captured classes. A→A (repeated poll, same run) is a no-op.</summary>
    internal static bool IsNewLoadoutRun(long previousRunId, long newRunId)
        => newRunId != 0 && newRunId != previousRunId;

    private void TickLoadoutRunBoundary()
    {
        var runId = _services.Dungeon.CurrentRunId;
        if (IsNewLoadoutRun(_loadoutRunId, runId))
        {
            _loadoutCapture.ResetForRun();
            _lastPolledProfession = 0;
        }
        _loadoutRunId = runId;
    }

    /// <summary>Reads the local player's live profession; on a new non-zero value (including the first
    /// one seen since a run-start reset) captures that class's active loadout (self-only).</summary>
    private void PollLocalProfession()
    {
        var current = _services.PlayerState.Profession;
        if (current == 0 || current == _lastPolledProfession) return;
        _lastPolledProfession = current;
        CaptureActiveClassLoadout(current);
    }

    // Purely additive and self-only: a no-op when the loadout API isn't up yet or we're not in world.
    private void CaptureActiveClassLoadout(int professionId)
    {
        if (!_services.Loadout.IsAvailable) return;
        var self = _services.CombatSnapshot.LocalEntityId;
        if (!self.IsPlayer) return;

        var (projectName, talentStageId, talentNodes) = ResolveActiveProject(professionId);
        var gearInstances = _services.Inventory.GetSelfGear();

        _loadoutCapture.Capture(new CapturedLoadout(
            ProfessionId:  professionId,
            ProjectName:   projectName,
            TalentStageId: talentStageId,
            Gear:          BuildGearPairs(gearInstances),
            GearDetail:    BuildLoadoutGearDetail(gearInstances),
            Skills:        BuildLoadoutSkills(self),
            Fashion:       BuildLoadoutFashion(self),
            Modules:       BuildLoadoutModules(),
            TalentNodes:   talentNodes));
    }

    // The active class's saved-loadout name + talent stage + allocated talent nodes (the enriched
    // LoadoutSlot) — the data IInventory cannot give. Absent (0/null) when no saved slot currently
    // matches this class.
    private (string? ProjectName, int TalentStageId, IReadOnlyList<int>? TalentNodes) ResolveActiveProject(int professionId)
    {
        foreach (var slot in _services.Loadout.GetSlots())
            if (slot.ProfessionId == professionId) return (slot.Name, slot.TalentStageId, slot.TalentNodes);
        return (null, 0, null);
    }

    private static List<int[]> BuildGearPairs(IReadOnlyList<GearInstance> gear)
    {
        var list = new List<int[]>(gear.Count);
        foreach (var g in gear) list.Add(new[] { g.Slot, g.ConfigId });
        return list;
    }

    // Reuses AppendRolls (EntitySnapshot.cs) — the SAME roll-resolution formula CaptureSelfGearDetail
    // uses for the live encounter snapshot — so a loadout's gear detail is flattened identically. Writes
    // straight into GearDetail records instead of EntitySnapshot's parallel Gd* arrays: there is no wire
    // format to match here, this feeds the accumulator directly.
    private List<GearDetail> BuildLoadoutGearDetail(IReadOnlyList<GearInstance> gear)
    {
        var list = new List<GearDetail>(gear.Count);
        foreach (var g in gear)
        {
            var rolls = new List<int>();
            AppendRolls(rolls, 0, g.Attrs.Basic);
            AppendRolls(rolls, 1, g.Attrs.Advanced);
            AppendRolls(rolls, 2, g.Attrs.Recast);
            AppendRolls(rolls, 3, g.Attrs.Rare);

            var enchantId = 0;
            var enchantLv = 0;
            if (g.Enchant is { } en)
            {
                enchantLv = en.Level;
                if (_services.GameData.Equip.GetEnchantItem(en.ItemTypeId, en.Level) is { } gem)
                {
                    enchantId = gem.GemItemId;
                    foreach (var eff in gem.Effects) { rolls.Add(4); rolls.Add(eff.AttrId); rolls.Add(eff.Value); rolls.Add(0); }
                }
                else enchantId = en.ItemTypeId;
            }

            var rollPairs = new int[rolls.Count / 4][];
            for (var i = 0; i * 4 < rolls.Count; i++)
                rollPairs[i] = new[] { rolls[i * 4], rolls[i * 4 + 1], rolls[i * 4 + 2], rolls[i * 4 + 3] };

            list.Add(new GearDetail(
                g.Slot, g.Quality, g.RefineLevel,
                g.Perfection.Value, g.Perfection.Max,
                enchantId, enchantLv, rollPairs,
                g.Perfection.Level, g.BreakThroughTime));
        }
        return list;
    }

    private List<int[]> BuildLoadoutSkills(EntityId self)
    {
        var skills = _services.CombatLookup.GetSkillLevels(self);
        var list = new List<int[]>(skills.Count);
        foreach (var s in skills) list.Add(new[] { s.SkillId, s.Level, s.Tier });
        return list;
    }

    private List<Fashion> BuildLoadoutFashion(EntityId self)
    {
        var fashion = _services.EntityDetail.GetFashion(self);
        var list = new List<Fashion>(fashion.Count);
        foreach (var f in fashion)
        {
            var dyes = f.Dyes ?? FashionEntry.NoDyes;
            var flat = new float[dyes.Length * 4];
            for (var i = 0; i < dyes.Length; i++)
            {
                flat[i * 4]     = dyes[i].R;
                flat[i * 4 + 1] = dyes[i].G;
                flat[i * 4 + 2] = dyes[i].B;
                flat[i * 4 + 3] = dyes[i].A;
            }
            list.Add(new Fashion(f.Slot, f.FashionId, flat));
        }
        return list;
    }

    private List<CapturedModule> BuildLoadoutModules()
    {
        var equipped = _services.Inventory.GetEquipped();
        var snapshot = _services.Inventory.GetModules();
        if (equipped is null || snapshot is null) return new List<CapturedModule>();

        var byUuid = new Dictionary<long, ModuleInfo>(snapshot.Modules.Count);
        foreach (var m in snapshot.Modules) byUuid[m.Uuid] = m;

        var modules = new List<CapturedModule>(equipped.ModuleUuidsBySlot.Count);
        foreach (var (slot, uuid) in equipped.ModuleUuidsBySlot)
        {
            if (!byUuid.TryGetValue(uuid, out var info)) continue;
            var parts = new int[info.Parts.Count][];
            for (var i = 0; i < info.Parts.Count; i++)
                parts[i] = new[] { info.Parts[i].AttrId, info.Parts[i].Value };
            modules.Add(new CapturedModule(slot, info.ConfigId, info.Quality, parts));
        }
        return modules;
    }
}
