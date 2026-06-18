using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>A resolved raid move: which char goes to which 1-based group / 0-based slot.</summary>
public readonly record struct RaidMove(long CharId, int Group, int Slot);

/// <summary>
/// Pure drop-key math for the 20-player raid grid (Unity-free, so it is unit-tested).
/// Keys are the flat grid index <c>group*5 + slot</c> (0–19). Group is reported 1-based
/// (the game's <c>AsyncUpdateTeamGroup</c> contract); slot stays 0-based.
/// </summary>
public static class RaidGridMove
{
    private const int Slots = MeterAggregator.SlotsPerGroup;   // 5
    private const int Cells = MeterAggregator.Groups * Slots;  // 20

    /// <summary>
    /// Resolve a drag from <paramref name="fromKey"/> to <paramref name="toKey"/> for the
    /// dragged <paramref name="source"/> entity. Returns false (no move) when keys are out
    /// of range, identical, or the source is not a player.
    /// </summary>
    public static bool Resolve(int fromKey, int toKey, EntityId source, out RaidMove move)
    {
        move = default;
        if (fromKey < 0 || fromKey >= Cells || toKey < 0 || toKey >= Cells) return false;
        if (fromKey == toKey) return false;
        if (!source.IsPlayer) return false;
        long charId = source.Value >> 16;
        if (charId == 0) return false;
        move = new RaidMove(charId, toKey / Slots + 1, toKey % Slots);
        return true;
    }
}

// Drop handler for the raid grid drag (DragSlotElement.OnDrop). Resolves the dragged cell → (charId, group,
// slot) and issues the game's own move; no optimistic update — the grid follows the NotifyTeamGroupUpdate
// (m29) broadcast, exactly as it already follows live party state.
public sealed partial class Plugin
{
    private void OnGridDrop(int fromKey, int toKey)
    {
        if (fromKey < 0 || fromKey >= _gridRows.Length) return;
        var source = _gridRows[fromKey].Id;   // EntityId of the dragged occupant (default for an empty slot)
        if (!RaidGridMove.Resolve(fromKey, toKey, source, out var move)) return;
        _services.PartyControl.MoveMember(move.CharId, move.Group, move.Slot);
    }
}
