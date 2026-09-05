using System;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Container-custody pins (fix 2026-08-14). Two auto-path branches used to bank an archive WITHOUT
/// writing its .replaydoc container: the zero-events early return (AssembleAndUpload returned
/// before PersistReUpload) and the region-unknown refusal (which even destroyed the buffered
/// events by clearing the capture buffer). In both, the prepared replay doc's only custody was the
/// one-shot positions POST in FinalizeAndMaybeUploadReplay — a failure there was permanent loss of
/// a banked replay window (P0: replay covers dungeon entry → run end). Both branches now retain via
/// the RetainWithoutUpload shape, gated on the pure <c>ShouldRetainUnsentArchive</c> seam below.
/// NEVER weaken these.
/// </summary>
public class RetainUnsentArchiveTests
{
    [Fact]
    public void Addressable_run_is_retained()
        => Assert.True(Plugin.ShouldRetainUnsentArchive(643789110607085568));

    [Fact]
    public void Field_fight_has_nothing_addressable_to_retain()
        => Assert.False(Plugin.ShouldRetainUnsentArchive(0));

    [Fact]
    public void Zero_event_container_roundtrips_positions_with_no_chunks()
    {
        // The zero-events branch persists (summary, zero chunk envelopes, positions). Pin that such
        // a container round-trips with its positions custody intact, so a later manual push
        // (TryLoadReUpload → ReplayReUpload) can re-send the replay verbatim.
        var payload = new ReUploadPayload(ReUploadContainer.Version, "sea", 88, "cm-z",
            "{\"s\":1}", Array.Empty<string>(), "{\"p\":1}", Array.Empty<SpoolChunkRef>());
        var bytes = ReUploadContainer.Serialize(payload);

        Assert.True(ReUploadContainer.TryDeserialize(bytes, out var back));
        Assert.Empty(back.Chunks);
        Assert.Equal("{\"p\":1}", back.Positions);   // positions survive with zero chunks
        Assert.Equal("{\"s\":1}", back.Summary);
    }
}
