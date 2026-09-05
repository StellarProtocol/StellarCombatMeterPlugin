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
        var (dmg, buff) = ChunkUploader.SplitUploadable(refs);
        Assert.Equal(new[] { "spool/s-dmg-000.gz" }, dmg.Select(r => r.BlobName));
        Assert.Equal(new[] { "spool/s-buff-000.gz" }, buff.Select(r => r.BlobName));
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
}
