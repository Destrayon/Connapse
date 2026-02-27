namespace Connapse.Integration.Tests;

/// <summary>
/// Test collection that provides a shared <see cref="SharedWebAppFixture"/> to all integration
/// test classes — one PostgreSQL container, one MinIO container, and one WebApplicationFactory
/// for the entire suite. Tests within this collection run sequentially to avoid contention on
/// the shared database and storage.
/// </summary>
[CollectionDefinition("Integration Tests")]
public class IntegrationTestCollection : ICollectionFixture<SharedWebAppFixture>
{
}
