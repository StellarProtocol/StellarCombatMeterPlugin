using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class BuffUploadFilterTests
{
    static readonly EntityId Self    = new(0x0000_0001_0000_0280);   // low16 = 640 → player
    static readonly EntityId Mate    = new(0x0000_0002_0000_0280);
    static readonly EntityId Mate2   = new(0x0000_0003_0000_0280);
    static readonly EntityId Monster = new(0x0000_0009_0000_0040);   // low16 = 64 → monster

    [Fact] public void Self_target_always_uploads()            => Assert.True(BuffUploadFilter.ShouldUpload(Self, Self, Self));
    [Fact] public void External_on_self_uploads()              => Assert.True(BuffUploadFilter.ShouldUpload(Mate, Self, Self));
    [Fact] public void Mate_buffs_mate2_uploads()              => Assert.True(BuffUploadFilter.ShouldUpload(Mate, Mate2, Self));
    [Fact] public void Mate_self_proc_does_not_upload()        => Assert.False(BuffUploadFilter.ShouldUpload(Mate, Mate, Self));
    [Fact] public void Player_debuff_on_monster_uploads()      => Assert.True(BuffUploadFilter.ShouldUpload(Mate, Monster, Self));
    [Fact] public void Monster_self_buff_does_not_upload()     => Assert.False(BuffUploadFilter.ShouldUpload(Monster, Monster, Self));
    [Fact] public void Monster_debuff_on_mate_does_not_upload()=> Assert.False(BuffUploadFilter.ShouldUpload(Monster, Mate, Self));
    [Fact] public void Unknown_firer_on_mate_does_not_upload() => Assert.False(BuffUploadFilter.ShouldUpload(EntityId.None, Mate, Self));
}
