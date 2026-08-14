// Per-boss/per-elite statistics — the ARCHIVE-TIME latch half (Spec B,
// docs/superpowers/specs/2026-08-14-per-boss-statistics-design.md §4.1). Capture/routing lives in
// Plugin.Capture.cs; emission in LogUpload/DerivedBucketBuilder.cs. Split out of Plugin.History.cs
// (536 LoC, already over the 500-LoC guardrail) so this feature adds no size there — the file-size
// rule forbids growing a pre-existing violation (CLAUDE.md § SOLID guardrails).

using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    internal sealed partial class EncounterHistoryEntry
    {
        // Per-(player, target-bucket) statistics snapshotted from the live capture stores at archive
        // time. Bucket key = the boss/elite monster config id, or TargetBucketStats.OtherKey (0) for
        // damage not attributed to a tracked target — so the buckets always SUM to this entry's
        // whole-fight Stats totals (§7.1); "Other" is a real bucket, never a drop (§7.2).
        //
        // NOT persisted to history JSON — same rollback-safe rule as StageBosses/Elites (process rules
        // §6): the entry JSON stays byte-identical to what older builds wrote, so rolling back to a
        // prior DLL can never read these entries as malformed and wipe the owner's history. A manual
        // re-upload of an old entry therefore carries buckets only for entries archived in-process, or
        // via the byte-exact `.replaydoc` container (2026-08-14 container-always-written fix).
        //
        // Unlike StageBosses there is NO sticky latch here: the live stores are cleared only by
        // Clear() (Plugin.cs), which runs AFTER BuildHistoryEntry has read them, and a SUPPRESSED
        // archive never calls Clear() at all — so a plain snapshot always sees exactly this segment's
        // buckets and a suppressed archive wipes nothing (buckets carry forward with _stats).
        public IReadOnlyDictionary<EntityId, IReadOnlyDictionary<int, TargetBucketStats.BucketSnapshot>> BossBuckets =
            EmptyBuckets;
        public IReadOnlyDictionary<EntityId, IReadOnlyDictionary<int, TargetBucketStats.BucketSnapshot>> EliteBuckets =
            EmptyBuckets;

        private static readonly IReadOnlyDictionary<EntityId, IReadOnlyDictionary<int, TargetBucketStats.BucketSnapshot>> EmptyBuckets =
            new Dictionary<EntityId, IReadOnlyDictionary<int, TargetBucketStats.BucketSnapshot>>();
    }

    /// <summary>Freezes the live per-boss/per-elite bucket stores onto the entry being archived —
    /// the same "snapshot at archive, never re-read live at upload time" discipline every other
    /// per-segment field follows. Called from BuildHistoryEntry (Plugin.History.cs) alongside
    /// ApplyAttrRanges/ApplyClassSpans. CAPTURE-ONLY: nothing here feeds the verdict, bossId,
    /// junk suppression, or run identity.</summary>
    private void ApplyBucketStats(EncounterHistoryEntry entry)
    {
        entry.BossBuckets  = _bossBuckets.Snapshot();
        entry.EliteBuckets = _eliteBuckets.Snapshot();
    }
}
