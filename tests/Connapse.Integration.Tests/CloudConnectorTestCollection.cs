namespace Connapse.Integration.Tests;

[CollectionDefinition("S3 Connector Tests")]
public class S3ConnectorTestCollection : ICollectionFixture<LocalStackFixture>
{
}

[CollectionDefinition("AzureBlobConnector")]
public class AzureBlobConnectorTestCollection : ICollectionFixture<AzuriteFixture>
{
}
