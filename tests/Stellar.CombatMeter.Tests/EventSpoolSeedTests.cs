using System.Linq;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Tests.Fakes;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Spec upload design 2026-09-26 items 3 + 4 — owner ruling: "uploaded buff rows and the upload/skip decision must
/// not change". Framework ≥ 2.11.0 (#98) delivers an entity's pre-existing buffs as ONE
/// <see cref="CombatEvent.EntityBuffsSeeded"/> and then reports that buff's first live delta as <c>Refreshed</c>
/// (2.10 said <c>Applied</c>: it had never held it) and its expiry as <c>Removed</c> (2.10 emitted nothing: an
/// unknown uuid). Each test runs the NEW framework's event sequence and the 2.10 sequence for the same wire
/// traffic through the spool and compares the uploaded track bytes and the archive-decision counters.
/// </summary>
public sealed class EventSpoolSeedTests
{
    static readonly EntityId Self = new(0x0000_0001_0000_0280);
    static readonly EntityId Mate = new(0x0000_0002_0000_0280);

    static ActiveBuff Held(int uuid) => new(uuid, 55333, 1, Mate, 1, 1, 900, 10_000, 0, 2327);
    static CombatEvent Seed(EntityId tgt, params ActiveBuff[] buffs) => new CombatEvent.EntityBuffsSeeded(500, tgt, buffs);
    static CombatEvent Buff(long ms, BuffChangeKind kind, int uuid, EntityId tgt, int stacks = 2, int dur = 8_000) =>
        new CombatEvent.BuffChanged(ms, tgt, uuid, 55333, kind, stacks, 1, dur, Mate, 0, 2327);

    sealed record Run(SpoolSegment Seg, string BuffJson, string BuffxJson, int Skipped);

    static async Task<Run> Feed(params CombatEvent[] events)
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store);
        foreach (var e in events) spool.Add(e, Self);
        var skipped = spool.SkippedUnknownEvents;
        var seg = spool.Rotate();
        await seg.Completion;
        string Read(System.Collections.Generic.IReadOnlyList<SpoolChunkRef> refs) =>
            string.Concat(refs.Select(r => SpoolCodec.Gunzip(store.Read(r.BlobName)!)));
        return new Run(seg, Read(seg.Buff), Read(seg.BuffRejected), skipped);
    }

    // (a) A seed is capture STATE, not a row: zero spool rows on every track, zero GameEventRows, so a segment that
    // saw only seeds still takes the "No events captured" retain-only branch exactly as on 2.14.2.
    [Fact]
    public async Task A_seed_adds_no_spool_row_and_no_game_event()
    {
        var run = await Feed(Seed(Self, Held(7), Held(8)), Seed(Mate, Held(9)));
        Assert.Empty(run.Seg.Dmg); Assert.Empty(run.Seg.Buff); Assert.Empty(run.Seg.Sheet); Assert.Empty(run.Seg.BuffRejected);
        Assert.Equal(0, run.Seg.Counts.GameEventRows);
        Assert.False(run.Seg.HasGameEvents);
        Assert.Equal(0, run.Skipped);
    }

    // (b) seed → Refreshed is uploaded as the `applied` row 2.10 produced for the same delta — same bytes, same
    // GameEventRows, and it still opens the segment's keyframe like any live row.
    [Fact]
    public async Task A_seed_then_Refreshed_uploads_the_same_applied_row_as_2_14_2()
    {
        var now = await Feed(Seed(Self, Held(7)), Buff(1000, BuffChangeKind.Refreshed, 7, Self));
        var old = await Feed(Buff(1000, BuffChangeKind.Applied, 7, Self));
        Assert.Contains("\"kind\":\"applied\"", now.BuffJson);
        Assert.Equal(old.BuffJson, now.BuffJson);
        Assert.Equal(old.BuffxJson, now.BuffxJson);
        Assert.Equal(old.Seg.Counts.GameEventRows, now.Seg.Counts.GameEventRows);
        Assert.Equal(1, now.Seg.Counts.GameEventRows);
    }

    // Only the FIRST delta converts: afterwards the buff is an ordinary held buff (2.10 had held it since its
    // Applied), so Refreshed stays refreshed and its expiry is an ordinary uploaded + counted Removed.
    [Fact]
    public async Task After_the_first_delta_the_buff_behaves_exactly_like_2_14_2()
    {
        var now = await Feed(Seed(Self, Held(7)),
                             Buff(1000, BuffChangeKind.Refreshed, 7, Self),
                             Buff(2000, BuffChangeKind.Refreshed, 7, Self, stacks: 3),
                             Buff(3000, BuffChangeKind.Removed, 7, Self, dur: 0));
        var old = await Feed(Buff(1000, BuffChangeKind.Applied, 7, Self),
                             Buff(2000, BuffChangeKind.Refreshed, 7, Self, stacks: 3),
                             Buff(3000, BuffChangeKind.Removed, 7, Self, dur: 0));
        Assert.Equal(old.BuffJson, now.BuffJson);
        Assert.Equal(3, now.Seg.Counts.GameEventRows);
        Assert.Equal(old.Seg.Counts.GameEventRows, now.Seg.Counts.GameEventRows);
    }

    // (c) seed → Removed (expiry of a buff 2.10 never knew — it emitted nothing): captured to the disk-only buffx
    // track, never uploaded, never counted, and it does NOT trigger the segment keyframe.
    [Fact]
    public async Task A_seed_then_Removed_goes_to_buffx_only()
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store);
        spool.Add(Seed(Self, Held(7)), Self);
        spool.Add(Buff(1000, BuffChangeKind.Removed, 7, Self, dur: 0), Self);
        Assert.True(spool.NeedsBuffKeyframe);                          // 2.14.2 saw no row here → no keyframe either
        var seg = spool.Rotate(); await seg.Completion;

        Assert.Empty(seg.Buff);
        var buffx = SpoolCodec.Gunzip(store.Read(seg.BuffRejected.Single().BlobName)!);
        Assert.Contains("\"kind\":\"removed\"", buffx);
        Assert.Equal(0, seg.Counts.GameEventRows);
        Assert.False(seg.HasGameEvents);                               // retain-only branch unchanged vs 2.14.2
        Assert.Equal(0, seg.ChunkCount);                               // nothing uploadable
    }

    // (d) Without a seed nothing changes: Applied/Refreshed/Removed are written as-is, each counted once.
    [Fact]
    public async Task Live_deltas_without_a_seed_are_unchanged()
    {
        var run = await Feed(Buff(1000, BuffChangeKind.Applied, 7, Self),
                             Buff(2000, BuffChangeKind.Refreshed, 7, Self),
                             Buff(3000, BuffChangeKind.Removed, 7, Self, dur: 0));
        var kinds = System.Text.RegularExpressions.Regex.Matches(run.BuffJson, "\"kind\":\"(\\w+)\"")
                    .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "applied", "refreshed", "removed" }, kinds);
        Assert.Equal(3, run.Seg.Counts.GameEventRows);
    }

    // A seed for a DIFFERENT uuid/target leaves an unrelated live delta untouched.
    [Fact]
    public async Task A_seed_does_not_convert_deltas_of_unseeded_uuids()
    {
        var now = await Feed(Seed(Self, Held(8)), Seed(Mate, Held(7)), Buff(1000, BuffChangeKind.Refreshed, 7, Self));
        Assert.Contains("\"kind\":\"refreshed\"", now.BuffJson);
    }

    // (e) Neither new event is an "unrecognized combat event" — the forward-compat counter behind the per-segment
    // "[CombatMeter.SP1] Skipped N unrecognized" warning stays at zero, and SpecChanged writes no row.
    [Fact]
    public async Task SpecChanged_and_EntityBuffsSeeded_are_not_unknown_events()
    {
        var run = await Feed(new CombatEvent.SpecChanged(100, Self, 0, 50001, true),
                             Seed(Self, Held(1)),
                             new CombatEvent.SpecChanged(200, Mate, 110002, 130002, false));
        Assert.Equal(0, run.Skipped);
        Assert.Equal(0, run.Seg.DiskChunkCount);
        Assert.Equal(0, run.Seg.Counts.GameEventRows);
    }

    // Keyframes still restate live buffs: the converted-applied buff is live and opens the next segment's keyframe,
    // exactly as the 2.14.2 Applied row did; a seed-only buff (no live delta yet) is not restated.
    [Fact]
    public async Task Keyframes_restate_the_converted_buff_but_not_seed_only_ones()
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store);
        spool.Add(Seed(Self, Held(7), Held(8)), Self);
        spool.Add(Buff(1000, BuffChangeKind.Refreshed, 7, Self), Self);
        var seg1 = spool.Rotate(); await seg1.Completion;
        spool.AddBuffKeyframe(3000, Self);
        var seg2 = spool.Rotate(); await seg2.Completion;
        var json = SpoolCodec.Gunzip(store.Read(seg2.Buff.Single().BlobName)!);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"kf\":1"));
        Assert.Contains("\"uuid\":7,", json);
        Assert.DoesNotContain("\"uuid\":8,", json);
        Assert.False(seg2.HasGameEvents);
    }
}
