using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.AutoArchive;

// Which run-END flow states cut an archive — split out of AutoArchiveEngine.cs to keep that file under the
// 500-LoC major threshold (docs/coding-standards.md); adding this feature took it 480 -> 511.
internal sealed partial class AutoArchiveEngine
{
    /// <summary>The run-END flow states, in the order the game visits them — the only states a transition
    /// INTO may arm the stage trigger (owner ruling 2026-07-20). Values mirror zproto EDungeonState; the
    /// enum tolerates unknown future wire values (cast), so this is an explicit allow-list, not a
    /// "not-an-entry-state" negation. Also the list the settings pane offers as chips.
    ///
    /// <para>MEASURED 2026-07-30 (owner's client, two consecutive runs): a run steps
    /// <c>Playing -&gt; End -&gt; Settlement -&gt; Vote -&gt; None</c> and ALL THREE run-end states armed the
    /// trigger, so all three are genuine user choices rather than dead controls.</para></summary>
    internal static readonly DungeonFlowState[] SelectableStages =
        { DungeonFlowState.End, DungeonFlowState.Settlement, DungeonFlowState.Vote };

    private static bool IsRunEndState(DungeonFlowState state) =>
        state is DungeonFlowState.End or DungeonFlowState.Settlement or DungeonFlowState.Vote;

    // WHICH run-end states cut an archive. Before this was selectable, a transition into ANY of the three
    // armed the trigger, so one run end cut 1-3 archives depending purely on whether the shared Min-gap
    // cooldown happened to have expired between them (owner report 2026-07-30: bosskill + 2x stage at
    // cooldownS=5, and only 1 at cooldownS=10 — same build, same content, different count). Selecting one
    // stage makes the count deterministic. Default End: the first run-end state, and the only one that
    // fired in BOTH measured runs.
    private readonly HashSet<DungeonFlowState> _stageStates = new() { DungeonFlowState.End };

    /// <summary>Whether a transition into <paramref name="state"/> cuts an archive.</summary>
    internal bool IsStageSelected(DungeonFlowState state) => _stageStates.Contains(state);

    /// <summary>Selects/clears one run-end stage. Non-run-end states are rejected outright — arming on an
    /// entry-side state (Active/Ready/Playing) would cut an archive of just the opener.</summary>
    internal void SetStageSelected(DungeonFlowState state, bool selected)
    {
        if (!IsRunEndState(state)) return;
        if (selected) _stageStates.Add(state);
        else _stageStates.Remove(state);
    }
}
