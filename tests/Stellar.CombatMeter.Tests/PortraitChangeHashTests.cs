// tests/Stellar.CombatMeter.Tests/PortraitChangeHashTests.cs
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class PortraitChangeHashTests
{
    private static PortraitEntry Base() => new(
        Uid: 100, ProfileUrl: "https://cdn/p.jpg", HalfbodyUrl: "https://cdn/h.jpg",
        Name: "Aria", Level: 60, ProfessionId: 2, Guild: "GreenTea",
        MasterScore: 4000, TitleId: 9061221, FightPoint: 28000,
        FashionCollect: 10, RideCollect: 5, WeaponSkinCollect: 3);

    [Fact]
    public void MasterScoreChangeAlone_DoesNotChangeHash()
    {
        var a = Base();
        var b = a with { MasterScore = 9999 };
        Assert.Equal(PortraitReport.ChangeHash(a), PortraitReport.ChangeHash(b));
    }

    [Fact]
    public void NameChange_ChangesHash()
    {
        Assert.NotEqual(PortraitReport.ChangeHash(Base()), PortraitReport.ChangeHash(Base() with { Name = "Aria2" }));
    }

    [Fact]
    public void PortraitUrlChange_ChangesHash()
    {
        Assert.NotEqual(PortraitReport.ChangeHash(Base()), PortraitReport.ChangeHash(Base() with { ProfileUrl = "https://cdn/new.jpg" }));
    }

    [Fact]
    public void IdenticalEntries_SameHash()
    {
        Assert.Equal(PortraitReport.ChangeHash(Base()), PortraitReport.ChangeHash(Base()));
    }
}
