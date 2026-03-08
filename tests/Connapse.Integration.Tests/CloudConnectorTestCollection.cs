namespace Connapse.Integration.Tests;

[CollectionDefinition("S3 Connector Tests")]
public class S3ConnectorTestCollection : ICollectionFixture<LocalStackFixture>
{
}

[CollectionDefinition("Azure Blob Connector Tests")]
public class AzureBlobConnectorTestCollection : ICollectionFixture<AzuriteFixture>
{
}
