using System.Linq;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Tests.Fakes;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// The disk-backed spool that replaced the two in-memory event rings: routing
/// (damage/skill → <c>dmg</c>, admitted buffs → <c>buff</c>, REJECTED buffs → the disk-only
/// <c>buffx</c>), Rotate = the old Flush at an archive boundary, Discard = the old Clear. The
/// per-track sealing/gzip/cap mechanics are pinned separately by <see cref="SpoolTrackTests"/>.
/// </summary>
public sealed class EventSpoolTests
{
    static readonly EntityId Self = new(0x0000_0001_0000_0280);
    static readonly EntityId Mate = new(0x0000_0002_0000_0280);
    static readonly EntityId Mob  = new(0x0000_0009_0000_0040);

    static CombatEvent Dmg(long ms) => new CombatEvent.DamageDealt(ms, Self, Mob, 1, 100, 100, 0, false, false, false, false, default(DamageElement), default(DamageSourceKind));
    static CombatEvent Buff(long ms, EntityId firer, EntityId tgt) => new CombatEvent.BuffChanged(ms, tgt, 1, 55333, BuffChangeKind.Applied, 1, 1, 5000, firer, 0, 2327);

    // Capture is unconditional (spec § 4.2 / § 9 invariant 2, owner doctrine "capture is default-on"):
    // a buff row the send filter REJECTS still reaches disk — in the third, disk-only `buffx` track —
    // so the local capture is complete even though only dmg+buff are ever uploaded.
    [Fact]
    public async Task Routes_damage_to_dmg_track_and_external_buffs_to_buff_track()
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store);
        spool.Add(Dmg(1), Self); spool.Add(Dmg(2), Self);
        spool.Add(Buff(3, Mate, Self), Self);      // external on self → sent
        spool.Add(Buff(4, Mate, Mate), Self);      // mate's self-proc → captured, never sent
        var seg = spool.Rotate();
        await seg.Completion;
        Assert.Equal(2, seg.Dmg.Single().Count);
        Assert.Equal(1, seg.Buff.Single().Count);
        Assert.Equal(1, seg.BuffRejected.Single().Count);          // captured to disk, not dropped
        Assert.Equal(2, seg.ChunkCount);                           // uploadable chunks only
        Assert.Equal(3, seg.DiskChunkCount);
        Assert.Equal(3, store.List(SpoolCodec.Prefix).Count);      // three blobs on disk
    }

    // The rejected track is disk-only: no upload path may ever post it. The split every upload leg runs
    // its refs through drops `buffx` outright — pinned here on the pure function so it holds for the
    // live segment upload AND the container re-upload without an HTTP fake.
    [Fact]
    public void Rejected_buff_rows_are_never_uploaded()
    {
        var refs = new[]
        {
            new SpoolChunkRef("dmg",   0, 1, 2, 3, "spool/s-dmg-000.gz"),
            new SpoolChunkRef("buffx", 0, 1, 2, 9, "spool/s-buffx-000.gz"),
            new SpoolChunkRef("buff",  0, 1, 2, 1, "spool/s-buff-000.gz"),
            new SpoolChunkRef("buffx", 1, 3, 4, 7, "spool/s-buffx-001.gz"),
        };
        var (dmg, buff, sheet) = ChunkUploader.SplitUploadable(refs);
        Assert.Equal(new[] { "spool/s-dmg-000.gz" }, dmg.Select(r => r.BlobName));
        Assert.Equal(new[] { "spool/s-buff-000.gz" }, buff.Select(r => r.BlobName));
        Assert.Empty(sheet);
    }

    // Truncation is PER TRACK: a buff flood fills (and flags) only the buff track. If buff volume could
    // flag TruncatedDmg the summary would claim the DAMAGE stream was clipped — a false "incomplete run"
    // on every heavy-buff fight, and the flag gates rDPS server-side (spec § 4.3).
    [Fact]
    public async Task Buff_volume_never_flags_the_damage_track()
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store, chunkEvents: 1);
        for (var i = 0; i < SpoolTrack.MaxChunksPerTrack + 2; i++) spool.Add(Buff(10 + i, Mate, Self), Self);
        spool.Add(Dmg(1), Self);
        var seg = spool.Rotate();
        await seg.Completion;
        Assert.True(seg.TruncatedBuff);
        Assert.False(seg.TruncatedDmg);
        Assert.Equal(1, seg.Dmg.Single().Count);
        Assert.Equal(SpoolTrack.MaxChunksPerTrack, seg.Buff.Count);
    }

    [Fact]
    public async Task Rotate_starts_a_fresh_segment_with_a_new_id()
    {
        var spool = new EventSpool(new FakeDataStore());
        spool.Add(Dmg(1), Self);
        var a = spool.Rotate(); await a.Completion;
        spool.Add(Dmg(2), Self);
        var b = spool.Rotate(); await b.Completion;
        Assert.NotEqual(a.SegmentId, b.SegmentId);
        Assert.Equal(1, b.Dmg.Single().Count);
    }

    [Fact]
    public async Task Discard_deletes_this_segments_blobs()
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store, chunkEvents: 1);
        spool.Add(Dmg(1), Self); spool.Add(Dmg(2), Self);   // two sealed blobs already
        await spool.DiscardAsync();
        Assert.Empty(store.List(SpoolCodec.Prefix));
        var seg = spool.Rotate(); await seg.Completion;
        Assert.Empty(seg.Dmg);
    }

    [Fact]
    public void Empty_rotate_yields_empty_segment()
    {
        var seg = new EventSpool(new FakeDataStore()).Rotate();
        Assert.Equal(0, seg.ChunkCount);
        Assert.False(seg.TruncatedDmg); Assert.False(seg.TruncatedBuff);
    }

    // Spec § 6.8: a Battle Imagine's buff on a TEAMMATE used to be routed to buffx (firer is a monster-typed
    // summon); resolving the firer to its owner admits it — that row is a PLAYER's external buff.
    [Fact]
    public async Task Summon_fired_buff_on_a_teammate_uploads_when_the_summon_belongs_to_a_player()
    {
        var owners = new SummonOwnerMap();
        var tina = new EntityId(0x0000_0007_0000_0040);
        owners.Record(tina, Mate);
        var store = new FakeDataStore();
        var spool = new EventSpool(store, owners);
        spool.Add(Buff(1, tina, new EntityId(0x0000_0003_0000_0280)), Self);   // Tina → another player
        spool.Add(Buff(2, Mob, new EntityId(0x0000_0003_0000_0280)), Self);    // unknown monster → still rejected
        var seg = spool.Rotate();
        await seg.Completion;
        Assert.Equal(1, seg.Buff.Single().Count);
        Assert.Equal(1, seg.BuffRejected.Single().Count);
    }

    static CombatEvent Attrs(long ms, EntityId who, params (int id, long v)[] pairs)
    {
        var list = new System.Collections.Generic.List<AttrValue>();
        foreach (var (id, v) in pairs) list.Add(new AttrValue(id, v));
        return new CombatEvent.EntityAttributesChanged(ms, who, list);
    }
    static System.Collections.Generic.IReadOnlyDictionary<int, long> LiveSheet() =>
        new System.Collections.Generic.Dictionary<int, long> { [11710] = 3000, [12670] = 1000, [11320] = 99 };

    [Fact]
    public async Task Self_attr_events_land_in_the_sheet_track_behind_one_keyframe()
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store, null, LiveSheet);
        spool.Add(Attrs(10, Self, (11710, 3350)), Self);
        spool.Add(Attrs(20, Self, (11320, 5)), Self);            // untracked only → no row
        spool.Add(Attrs(30, Self, (12670, 1200), (13100, 50)), Self);
        var seg = spool.Rotate();
        await seg.Completion;
        Assert.Equal(3, seg.Sheet.Single().Count);               // keyframe + 2 delta rows
        Assert.Equal(1, seg.ChunkCount);                         // sheet counts as UPLOADABLE
        var json = SpoolCodec.Gunzip(store.Read(seg.Sheet[0].BlobName)!);
        Assert.StartsWith("[{\"t\":\"sheet\",\"ms\":10,\"k\":1,", json);   // keyframe first, stamped like the first row
        Assert.Equal(0, spool.SkippedUnknownEvents);
    }

    [Fact]
    public async Task Other_players_attr_events_are_ignored_and_not_counted_as_unknown()
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store, null, LiveSheet);
        spool.Add(Attrs(10, Mate, (11710, 1)), Self);
        var seg = spool.Rotate();
        await seg.Completion;
        Assert.Empty(seg.Sheet);
        Assert.Equal(0, spool.SkippedUnknownEvents);
    }

    [Fact]
    public async Task Keyframe_can_be_requested_by_the_tick_and_is_written_once_per_segment()
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store, null, LiveSheet);
        Assert.True(spool.NeedsSheetKeyframe);
        spool.AddSheetKeyframe(5);
        Assert.False(spool.NeedsSheetKeyframe);
        spool.AddSheetKeyframe(6);                               // no-op
        spool.Add(Attrs(10, Self, (11710, 3350)), Self);         // no second keyframe
        var seg = spool.Rotate();
        await seg.Completion;
        Assert.Equal(2, seg.Sheet.Single().Count);
        Assert.True(spool.NeedsSheetKeyframe);                   // fresh segment wants its own
    }

    [Fact]
    public async Task Without_a_sheet_reader_no_keyframe_is_written_but_delta_rows_still_are()
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store);
        spool.Add(Attrs(10, Self, (11710, 3350)), Self);
        var seg = spool.Rotate();
        await seg.Completion;
        Assert.Equal(1, seg.Sheet.Single().Count);
    }

    // The ARCHIVE decision counts GAME events only (final review, I1). Every segment gets a sheet keyframe,
    // so gating Plugin.LogUpload's "No events captured — skipping auto-upload" retain-only branch on
    // ChunkCount would make that branch UNREACHABLE and start uploading the no-damage tail archives the owner
    // had purged server-side (kill-board P2 ruling 2026-09-02). GameEventChunkCount is what that branch reads;
    // ChunkCount still answers "is there anything to POST", and the keyframe chunk does still upload.
    [Fact]
    public async Task A_sheet_only_segment_has_no_game_event_chunks_but_still_uploads_its_keyframe()
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store, null, LiveSheet);
        spool.AddSheetKeyframe(5);                               // the per-segment tick keyframe, nothing else
        var seg = spool.Rotate();
        await seg.Completion;
        Assert.Equal(0, seg.GameEventChunkCount);                // archive decision: nothing captured
        Assert.Equal(1, seg.ChunkCount);                         // upload decision: one sheet chunk to POST
        Assert.Empty(seg.Dmg);
        Assert.Empty(seg.Buff);
        Assert.Single(seg.Sheet);
    }

    // A keyframe read that finds NO tracked attr writes nothing and leaves the request PENDING — the tick
    // asks again on its next pass, as the live sheet fills in. Deferred, never silently skipped, and still
    // exactly once per segment when it finally lands.
    [Fact]
    public async Task A_keyframe_with_no_tracked_attr_is_deferred_then_written_once()
    {
        var store = new FakeDataStore();
        var sheet = new System.Collections.Generic.Dictionary<int, long> { [11320] = 99 };   // untracked only
        var spool = new EventSpool(store, null, () => sheet);
        spool.AddSheetKeyframe(5);
        Assert.True(spool.NeedsSheetKeyframe);                   // nothing written — still wanted
        sheet[11710] = 3000;                                     // a tracked attr reaches the live sheet
        spool.AddSheetKeyframe(6);
        Assert.False(spool.NeedsSheetKeyframe);
        spool.AddSheetKeyframe(7);                               // … and never a second time
        var seg = spool.Rotate();
        await seg.Completion;
        Assert.Equal(1, seg.Sheet.Single().Count);
        Assert.Equal(0, seg.GameEventChunkCount);
    }

    // rDPS phase 2 (decision D1): EVERY player's AttrSkillId write is a cast row in the dmg track — self AND
    // AOI teammates (the framework emits attr events for player entities only). The self-only sheet gate
    // below it is untouched: a teammate's other attrs still reach no track.
    [Fact]
    public async Task Any_players_AttrSkillId_write_lands_as_a_cast_row_in_the_dmg_track()
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store, null, LiveSheet);
        spool.Add(Attrs(10, Mate, (100, 2313)), Self);                      // teammate cast
        spool.Add(Attrs(11, Self, (100, 2310), (11710, 3350)), Self);       // self cast + a sheet delta
        spool.Add(Attrs(12, Mate, (100, 0)), Self);                         // not a cast
        spool.Add(Attrs(13, Mate, (101, 2), (11710, 1)), Self);             // stage change / teammate attr: nothing
        var seg = spool.Rotate();
        await seg.Completion;
        Assert.Equal(2, seg.Dmg.Single().Count);
        Assert.Equal(2, seg.Sheet.Single().Count);                          // keyframe + the one self delta
        var json = SpoolCodec.Gunzip(store.Read(seg.Dmg[0].BlobName)!);
        Assert.StartsWith("[{\"t\":\"skill\",\"ms\":10,\"src\":\"" + Mate.Value + "\",\"skill\":2313,\"phase\":101}", json);
        Assert.Equal(0, spool.SkippedUnknownEvents);
    }

    [Fact]
    public async Task CastRows_counts_the_segments_casts_and_resets_on_rotate()
    {
        var spool = new EventSpool(new FakeDataStore(), null, LiveSheet);
        spool.Add(Attrs(10, Mate, (100, 2313)), Self);
        spool.Add(Attrs(20, Mate, (100, 2313)), Self);
        Assert.Equal(2, spool.CastRows);
        var seg = spool.Rotate(); await seg.Completion;
        Assert.Equal(0, spool.CastRows);
    }
}
