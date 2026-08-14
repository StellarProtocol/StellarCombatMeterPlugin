using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Reads the process-wide LogUploader.ApiBase — shares UploadApiBaseTests' collection so it can never
// observe that test's temporary override.
[Collection(ApiBaseCollection.Name)]
public class RawUploadUrlTests
{
    [Fact]
    public void Chunk_url_is_region_and_level_scoped()
    {
        Assert.Equal("https://api.stellarresonance.app/run/sea/42/events",
            ChunkUploader.BuildUrl(LogUploader.ApiBase, "sea", 42));
    }
}
