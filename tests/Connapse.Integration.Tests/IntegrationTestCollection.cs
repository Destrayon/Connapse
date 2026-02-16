namespace Connapse.Integration.Tests;

/// <summary>
/// Test collection to ensure integration tests run sequentially rather than in parallel.
/// This prevents conflicts when multiple WebApplicationFactory instances would otherwise
/// try to start simultaneously and compete for resources.
/// </summary>
[CollectionDefinition("Integration Tests", DisableParallelization = true)]
public class IntegrationTestCollection
{
}
