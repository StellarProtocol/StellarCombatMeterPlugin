namespace Stellar.CombatMeter;

/// <summary>
/// Pure predicate for the calibrated <c>TeamMemberFastSyncData.state</c> preference (spec A2).
/// Extracted from Plugin.List.cs's IsDead/IsOffline/ScanRosterVitals purely so the mapping is
/// unit-testable — Plugin cannot be headless-instantiated (the ReplayCapture/ObserveBurstHit
/// precedent).
/// </summary>
internal static class FastSyncStateMapper
{
    /// <summary>
    /// Null = inert — <paramref name="calibratedConst"/> is 0 (uncalibrated) or <paramref name="state"/>
    /// is 0 (no signal this tick); caller falls back to its pre-existing HP/online_status inference.
    /// Non-null = the calibrated signal's verdict (state matches the calibrated constant).
    /// </summary>
    internal static bool? TryMap(int state, int calibratedConst)
        => calibratedConst != 0 && state != 0 ? state == calibratedConst : (bool?)null;
}
