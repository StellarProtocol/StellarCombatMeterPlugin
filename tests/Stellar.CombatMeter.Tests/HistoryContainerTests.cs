using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Per-run history persistence (owner ask 2026-08-16): each archived run is its own file under
// plugindata history/, next to its replay/ file — NOT one 2.82MB blob in the settings config (which made
// every settings save re-serialize all of it and freeze the game). One history file holds the run's entry
// JSON + its upload-state JSON, split so neither has to be re-escaped. Mirrors ReUploadContainer.
public sealed class HistoryContainerTests
{
    [Fact]
    public void ContainerName_is_per_run_under_history_prefix()
        => Assert.Equal("history/12345-67890.histdoc", HistoryContainer.ContainerName(12345, 67890));

    [Fact]
    public void Roundtrips_entry_with_upload_state()
    {
        var entry = "{\"v\":10,\"scene\":\"6525\",\"luid\":42}";
        var up = "{\"luid\":42,\"arch\":9,\"up\":2,\"uurl\":\"/run/sea/abc\"}";
        var ok = HistoryContainer.TryDeserialize(HistoryContainer.Serialize(entry, up), out var gotEntry, out var gotUp);
        Assert.True(ok);
        Assert.Equal(entry, gotEntry);
        Assert.Equal(up, gotUp);
    }

    [Fact]
    public void Roundtrips_entry_without_upload_state()
    {
        var entry = "{\"v\":10,\"scene\":\"town\"}";
        var ok = HistoryContainer.TryDeserialize(HistoryContainer.Serialize(entry, null), out var gotEntry, out var gotUp);
        Assert.True(ok);
        Assert.Equal(entry, gotEntry);
        Assert.Null(gotUp);
    }

    [Fact]
    public void Entry_json_is_stored_raw_so_quotes_and_unicode_survive_verbatim()
    {
        // The entry is stored after the delimiter with no re-escaping, so a name with quotes/unicode is byte-exact.
        var entry = "{\"nm\":\"A\\\"Qéก\",\"x\":1}";
        Assert.True(HistoryContainer.TryDeserialize(HistoryContainer.Serialize(entry, null), out var got, out _));
        Assert.Equal(entry, got);
    }

    [Fact]
    public void TryDeserialize_rejects_empty_input()
    {
        Assert.False(HistoryContainer.TryDeserialize(null, out _, out _));
        Assert.False(HistoryContainer.TryDeserialize(System.Array.Empty<byte>(), out _, out _));
    }

    [Fact]
    public void OrphanContainerNames_returns_history_files_with_no_live_run_and_ignores_other_prefixes()
    {
        var existing = new[]
        {
            HistoryContainer.ContainerName(1, 100),   // live
            HistoryContainer.ContainerName(2, 200),   // orphan
            "replay/2-200.replaydoc",                 // not history/ — ignored
        };
        var live = new[] { (1L, 100L) };
        var orphans = HistoryContainer.OrphanContainerNames(existing, live);
        Assert.Contains(HistoryContainer.ContainerName(2, 200), orphans);
        Assert.DoesNotContain(HistoryContainer.ContainerName(1, 100), orphans);
        Assert.DoesNotContain("replay/2-200.replaydoc", orphans);
    }
}
