using BoldMigrate.Core.Enums;
using BoldMigrate.Engine.Upload;
using Xunit;

namespace BoldMigrate.Engine.Tests.Upload;

public class IdempotencyKeyTests
{
    [Fact]
    public void ComputeIdempotencyKey_SameInputs_ReturnsIdenticalKey()
    {
        var runId = Guid.NewGuid();
        var itemType = ItemType.Report;
        var path = "/Finance/Monthly/Summary";

        var key1 = UploadLayer.ComputeIdempotencyKey(runId, itemType, path);
        var key2 = UploadLayer.ComputeIdempotencyKey(runId, itemType, path);

        Assert.Equal(key1, key2);
        Assert.Equal(64, key1.Length); // 64 hex chars for SHA256
    }

    [Fact]
    public void ComputeIdempotencyKey_DifferentRuns_ProduceDifferentKeys()
    {
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();
        var path = "/Sales/Q4";

        var key1 = UploadLayer.ComputeIdempotencyKey(run1, ItemType.Report, path);
        var key2 = UploadLayer.ComputeIdempotencyKey(run2, ItemType.Report, path);

        Assert.NotEqual(key1, key2);
    }
}
