using Connapse.Core;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Memory;
using FluentAssertions;

namespace Connapse.Storage.Tests.Memory;

/// <summary>
/// Which facts count as past their check-by date. The in-memory and SQL forms must agree, since one flags a fact
/// unconfirmed and the other picks the facts a due-only recall returns (CON-340).
/// </summary>
[Trait("Category", "Unit")]
public class MemoryCheckFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, DateTimeOffset?, DateTimeOffset?, DateTimeOffset?, bool> Cases => new()
    {
        { "past its date", null, null, Now.AddDays(-1), true },
        { "due this instant", null, null, Now, true },
        { "date still ahead", null, null, Now.AddDays(1), false },
        { "no date", null, null, null, false },
        { "superseded", Now.AddDays(-2), null, Now.AddDays(-1), false },
        { "ended", null, Now.AddDays(-2), Now.AddDays(-1), false },
        { "ends this instant", null, Now, Now.AddDays(-1), false },
        { "ends later", null, Now.AddDays(5), Now.AddDays(-1), true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsDue_AndChecked_Agree(string name, DateTimeOffset? superseded, DateTimeOffset? validTo, DateTimeOffset? checkBy, bool due)
    {
        MemoryFactEntity row = new() { Id = Guid.NewGuid(), Subject = name, Attribute = "a", Value = "v", SupersededAt = superseded, ValidTo = validTo, CheckBy = checkBy };
        IQueryable<MemoryFactEntity> rows = new[] { row }.AsQueryable();

        PostgresMemoryStore.IsDue(superseded, validTo, checkBy, Now).Should().Be(due, name);
        PostgresMemoryStore.Checked(rows, MemoryFactCheck.Due, Now).Any().Should().Be(due, name);
        PostgresMemoryStore.Checked(rows, MemoryFactCheck.Fresh, Now).Any().Should().Be(!due, name);
        PostgresMemoryStore.Checked(rows, MemoryFactCheck.Any, Now).Should().ContainSingle(name);
    }

    [Theory]
    [InlineData("Waiting_On", "waiting_on")]
    [InlineData("  status ", "status")]
    public void CheckKey_TrimsAndLowerCases(string attribute, string key) =>
        PostgresMemoryStore.CheckKey(attribute).Should().Be(key);
}
