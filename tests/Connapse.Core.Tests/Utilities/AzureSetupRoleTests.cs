using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class AzureSetupRoleTests
{
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private const string Account = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct";

    [Fact]
    public void AssignmentsFor_OneCommandPerDistinctContainer_PrefixesCollapseToTheirContainer()
    {
        string s = AzureSetupRole.AssignmentsFor(Client, Account, ["docs/2024", "docs", "Images/raw"]);

        string[] lines = s.Split('\n');
        lines.Should().HaveCount(2);
        lines[0].Should().Contain($"--scope '{Account}/blobServices/default/containers/docs'");
        lines[1].Should().Contain($"--scope '{Account}/blobServices/default/containers/Images'");
        lines.Should().OnlyContain(l => l.Contains($"--assignee '{Client}'")
                                     && l.Contains($"--role '{AzureCloudShellSetup.BlobDataRoleName}'"));
    }

    [Fact]
    public void AssignmentsFor_NoLocations_GrantsTheWholeAccount() =>
        AzureSetupRole.AssignmentsFor(Client, Account + "/", [])
            .Should().Be($"az role assignment create --assignee '{Client}' --role '{AzureCloudShellSetup.BlobDataRoleName}' --scope '{Account}'");

    [Fact]
    public void AssignmentsFor_EscapesSingleQuotes() =>
        AzureSetupRole.AssignmentsFor(Client, Account, ["it's"])
            .Should().Contain("containers/it'\\''s'");
}
