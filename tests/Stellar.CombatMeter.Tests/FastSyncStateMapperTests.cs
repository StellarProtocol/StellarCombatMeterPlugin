using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class FastSyncStateMapperTests
{
    [Fact] public void Uncalibrated_constant_is_inert() => Assert.Null(FastSyncStateMapper.TryMap(3, 0));
    [Fact] public void No_signal_state_is_inert() => Assert.Null(FastSyncStateMapper.TryMap(0, 2));
    [Fact] public void Matching_calibrated_value_maps_true() => Assert.True(FastSyncStateMapper.TryMap(2, 2));
    [Fact] public void NonMatching_calibrated_value_maps_false() => Assert.False(FastSyncStateMapper.TryMap(1, 2));
}
