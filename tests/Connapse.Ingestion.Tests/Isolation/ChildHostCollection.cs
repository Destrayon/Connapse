namespace Connapse.Ingestion.Tests.Isolation;

/// <summary>
/// The test classes that start parser and inference hosts run one at a time (CON-159). A host the
/// OS refuses memory or a thread at start-up aborts with "Out of memory." before it is ready; on CI
/// that happened while 1.4 GB inference hosts started beside the test whose first host fails on
/// purpose and so has no retry left.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ChildHostCollection
{
    public const string Name = "Child hosts";
}
