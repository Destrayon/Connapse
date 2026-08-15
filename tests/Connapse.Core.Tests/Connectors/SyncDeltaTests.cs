using Connapse.Core.Interfaces;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class SyncDeltaTests
{
    [Fact]
    public void SyncDelta_Empty_IsNotAResyncRequest()
    {
        var delta = new SyncDelta([], [], "cursor-1", RequiresFullResync: false);

        delta.Upserted.Should().BeEmpty();
        delta.DeletedPaths.Should().BeEmpty();
        delta.NextCursor.Should().Be("cursor-1");
        delta.RequiresFullResync.Should().BeFalse();
    }

    [Fact]
    public void SyncDelta_Resync_CarriesNoCursor()
    {
        // Graph answers a stale delta token with HTTP 410 Gone and Dropbox with a 409
        // reset. Both mean "start over": the caller must clear the stored cursor, so a
        // resync response must not also hand back a cursor to persist.
        var delta = new SyncDelta([], [], NextCursor: null, RequiresFullResync: true);

        delta.RequiresFullResync.Should().BeTrue();
        delta.NextCursor.Should().BeNull();
    }

    [Fact]
    public void ISyncCursorConnector_ExtendsIConnector()
    {
        typeof(IConnector).IsAssignableFrom(typeof(ISyncCursorConnector)).Should().BeTrue();
    }

    [Fact]
    public void ISyncCursorConnector_IsOptional()
    {
        // Connectors without a delta API must not be forced to implement it; the sync
        // engine falls back to list-and-diff for those.
        typeof(ISyncCursorConnector).IsAssignableFrom(typeof(Connapse.Storage.Connectors.S3Connector))
            .Should().BeFalse();
    }

    [Fact]
    public void ISyncCursorConnector_DoesNotImplyWriteAccess()
    {
        // Delta sync is a read capability. A source that can report changes must not
        // thereby become mutable.
        typeof(IWritableConnector).IsAssignableFrom(typeof(ISyncCursorConnector)).Should().BeFalse();
    }
}
