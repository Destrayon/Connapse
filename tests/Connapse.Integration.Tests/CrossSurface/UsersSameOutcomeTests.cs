namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// The user rows of <see cref="SameOutcomeTests"/>: the same check, run from a class of its own so
/// the shared host's tests spread across CI's class shards.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class UsersSameOutcomeTests(SharedWebAppFixture fixture)
{
    [Theory]
    [MemberData(nameof(SameOutcomeTests.UserRows), MemberType = typeof(SameOutcomeTests))]
    public Task Operation_SameCase_SameOutcomeOnEverySurface(string operationName, string @case, string surface) =>
        SameOutcomeTests.CheckAsync(fixture, operationName, @case, surface);
}
