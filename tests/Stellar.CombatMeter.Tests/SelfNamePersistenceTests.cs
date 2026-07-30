using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Owner repro 2026-07-29: relaunching WHILE MOUNTED shows the local player's meter row as the literal
/// "Self"; dismounting resolves it ("Revette · Frost Mage"). While mounted, `IPlayerState.Name` is blank,
/// so `EntityLabel.Resolve` falls through its whole chain — roster (empty when solo) and combat lookup
/// included — to the `"Self"` fallback. The rescue in Plugin.List.cs uses `_lastKnownSelfName`, which is
/// SESSION-ONLY, so a fresh launch has nothing cached.
///
/// A character's name is stable across sessions, so it is persisted and restored — but only for the SAME
/// character, otherwise switching characters would show the previous one's name.
/// </summary>
public class SelfNamePersistenceTests
{
    [Fact]
    public void RestoresTheCachedName_ForTheSameCharacter()
    {
        Assert.Equal("Revette", Plugin.RestoreSelfName(storedCharId: 1959717569152, storedName: "Revette",
                                                       currentCharId: 1959717569152));
    }

    [Fact]
    public void DoesNotRestore_ForADifferentCharacter()
    {
        // Switching characters must never show the previous character's name.
        Assert.Null(Plugin.RestoreSelfName(1959717569152, "Revette", currentCharId: 42));
    }

    [Fact]
    public void DoesNotRestore_WhenTheLocalCharacterIsNotKnownYet()
    {
        // Pre-login / not in world: LocalEntityId is None, so there is nothing to match against.
        Assert.Null(Plugin.RestoreSelfName(1959717569152, "Revette", currentCharId: 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DoesNotRestore_AnEmptyStoredName(string? stored)
        => Assert.Null(Plugin.RestoreSelfName(1959717569152, stored, currentCharId: 1959717569152));

    [Fact]
    public void DoesNotRestore_WhenNothingWasStored()
        => Assert.Null(Plugin.RestoreSelfName(storedCharId: 0, storedName: null, currentCharId: 1959717569152));

    [Fact]
    public void CharIdIsFullWidth_NotTruncatedToInt()
    {
        // Real char ids exceed int32 (the owner's is 1959717569152). EntityId.Uid casts to int, which
        // truncates — so the persistence key must be derived as `Value >> 16` in FULL width, exactly as
        // Plugin.PartyFocus.cs already does. 1959717569152 truncates to 1212482176 as an int32.
        Assert.Equal(1959717569152L, Plugin.SelfCharIdOf(1959717569152L << 16 | 640));
        Assert.NotEqual(1212482176L, Plugin.SelfCharIdOf(1959717569152L << 16 | 640));
    }
}
