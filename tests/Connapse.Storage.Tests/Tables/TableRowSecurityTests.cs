using Connapse.Storage.Tables;
using FluentAssertions;

namespace Connapse.Storage.Tests.Tables;

/// <summary>CON-324: the generated names, policy SQL and version floors behind row-level security on tables.</summary>
[Trait("Category", "Unit")]
public class TableRowSecurityTests
{
    private static readonly Guid ContainerId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void ContainerRoleName_IsAQuotableNameUnderTheLimit()
    {
        string name = TableDdl.ContainerRoleName("connapse", ContainerId);

        name.Should().StartWith(TableDdl.ContainerRolePrefix).And.EndWith(ContainerId.ToString("N"));
        name.Should().HaveLength(53);
        TableDdl.Quote(name).Should().Be("\"" + name + "\"");
    }

    [Fact]
    public void ContainerRoleName_DiffersByDatabase()
    {
        TableDdl.ContainerRoleName("connapse", ContainerId).Should().NotBe(TableDdl.ContainerRoleName("connapse_test", ContainerId),
            "roles are cluster-wide, so two databases on one server must not share them");
    }

    [Fact]
    public void ProtectSql_WithDocument_EnablesForcesAndChecksTheDocument()
    {
        Guid document = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        IReadOnlyList<string> sql = TableDdl.ProtectSql(ContainerId, "t_1", document);

        sql.Should().HaveCount(3);
        sql[0].Should().EndWith("ENABLE ROW LEVEL SECURITY");
        sql[1].Should().EndWith("FORCE ROW LEVEL SECURITY");
        sql[2].Should().Contain("AS PERMISSIVE FOR SELECT TO PUBLIC")
            .And.EndWith($"USING ((SELECT '{document:D}'::uuid = ANY (tables_auth.readable_documents())))");
    }

    [Fact]
    public void ProtectSql_WithoutDocument_ShowsNoRows()
    {
        TableDdl.ProtectSql(ContainerId, "t_1", null)[2].Should().EndWith("USING (false)");
    }

    [Theory]
    [InlineData("16.14", false)]
    [InlineData("16.15", true)]
    [InlineData("17.10", false)]
    [InlineData("17.11", true)]
    [InlineData("18.5", false)]
    [InlineData("18.6", true)]
    [InlineData("19.0", true)]
    public void IsRowSecurityPatched_ComparesToEachMajorsFloor(string version, bool patched)
    {
        TableReaderLogin.IsRowSecurityPatched(Version.Parse(version)).Should().Be(patched);
    }
}
