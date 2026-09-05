using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Sources;

[Trait("Category", "Unit")]
public class SourceModelTests
{
    [Fact]
    public void ConnectionProvider_Values_MatchConnectorTypeForBackfill()
    {
        // Phase 2 backfills existing containers by casting ConnectorType to
        // ConnectionProvider. The numeric values must line up or the cast silently
        // mislabels every migrated connection.
        ((int)ConnectionProvider.Filesystem).Should().Be((int)ConnectorType.Filesystem);
        ((int)ConnectionProvider.S3).Should().Be((int)ConnectorType.S3);
    }

    [Fact]
    public void ConnectionProvider_DoesNotContainManagedStorage()
    {
        // Managed storage is Connapse's own backend, never an external system
        // it authenticates to, so it must not be expressible as a connection.
        Enum.GetNames<ConnectionProvider>().Should().NotContain("ManagedStorage");
    }

    [Fact]
    public void Source_NewInstance_DefaultsToNeverSynced()
    {
        var source = new Source(
            Id: Guid.NewGuid(),
            Name: "docs-bucket",
            Description: null,
            ConnectionId: Guid.NewGuid(),
            ScopeJson: """{"prefix":"docs/"}""",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        source.LastSyncStatus.Should().Be(SyncStatus.Never);
        source.SyncCursor.Should().BeNull();
        source.LastSyncedAt.Should().BeNull();
        source.Enabled.Should().BeTrue();
        source.DocumentCount.Should().Be(0);
    }

    [Fact]
    public void Connection_NeverExposesSecret()
    {
        // The Connection read model is returned to callers, so the secret VALUE must
        // not be a property on it. HasSecret is fine — it is a bool flag telling the
        // UI whether a credential is stored, and carries none of the credential.
        var propertyNames = typeof(Connection).GetProperties().Select(p => p.Name).ToList();

        propertyNames.Should().NotContain(["Secret", "SecretProtected"]);
        typeof(Connection).GetProperty(nameof(Connection.HasSecret))!
            .PropertyType.Should().Be<bool>();
    }
}
