using System.Linq;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Tests.Fakes;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// The disk-backed spool that replaced the two in-memory event rings: routing
/// (damage/skill → <c>dmg</c>, admitted buffs → <c>buff</c>), Rotate = the old Flush at an archive
/// boundary, Discard = the old Clear. The per-track sealing/gzip/cap mechanics are pinned separately by
/// <see cref="SpoolTrackTests"/>.
/// </summary>
public sealed class EventSpoolTests
{
    static readonly EntityId Self = new(0x0000_0001_0000_0280);
    static readonly EntityId Mate = new(0x0000_0002_0000_0280);
    static readonly EntityId Mob  = new(0x0000_0009_0000_0040);

    static CombatEvent Dmg(long ms) => new CombatEvent.DamageDealt(ms, Self, Mob, 1, 100, 100, 0, false, false, false, false, default(DamageElement), default(DamageSourceKind));
    static CombatEvent Buff(long ms, EntityId firer, EntityId tgt) => new CombatEvent.BuffChanged(ms, tgt, 1, 55333, BuffChangeKind.Applied, 1, 1, 5000, firer, 0, 2327);

    [Fact]
    public async Task Routes_damage_to_dmg_track_and_external_buffs_to_buff_track()
    {
        var store = new FakeDataStore();
        var spool = new EventSpool(store);
        spool.Add(Dmg(1), Self); spool.Add(Dmg(2), Self);
        spool.Add(Buff(3, Mate, Self), Self);      // external on self → sent
        spool.Add(Buff(4, Mate, Mate), Self);      // mate's self-proc → not sent
        var seg = spool.Rotate();
        await seg.Completion;
        Assert.Equal(2, seg.Dmg.Single().Count);
        Assert.Equal(1, seg.Buff.Single().Count);
        Assert.Equal(2, store.List(SpoolCodec.Prefix).Count);
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
