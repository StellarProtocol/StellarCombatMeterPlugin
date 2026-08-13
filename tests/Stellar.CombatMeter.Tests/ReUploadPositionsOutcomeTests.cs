using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Manual re-upload positions leg outcome logging (fix 2026-08-14): the leg used to pass NO
/// completion callback to <c>PositionUploader.PostRawFireAndForget</c>, so neither success nor
/// failure was ever logged — a failed manual positions re-send was invisible. The pure line
/// builder below is what the callback logs (Info on OK, Warning on FAILED), matching the live
/// path's positions OK/FAILED lines (UploadReplayDoc, Plugin.Replay.cs). Both outcomes MUST keep
/// producing a distinguishable, greppable line.
/// </summary>
public class ReUploadPositionsOutcomeTests
{
    [Fact]
    public void Success_produces_ok_line_with_status_and_run()
    {
        var line = Plugin.ReUploadPositionsOutcomeLine(ok: true, status: 200, err: null, levelUuid: 88);
        Assert.Contains("positions OK", line);
        Assert.Contains("HTTP 200", line);
        Assert.Contains("levelUuid=88", line);
    }

    [Fact]
    public void Failure_produces_failed_line_with_status_error_and_run()
    {
        var line = Plugin.ReUploadPositionsOutcomeLine(ok: false, status: 503, err: "overloaded", levelUuid: 88);
        Assert.Contains("positions FAILED", line);
        Assert.Contains("HTTP 503", line);
        Assert.Contains("levelUuid=88", line);
        Assert.Contains("overloaded", line);
    }
}
