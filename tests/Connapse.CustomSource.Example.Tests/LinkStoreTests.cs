using FluentAssertions;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class LinkStoreTests
{
    [Fact]
    public void Remove_WhenSavingFails_KeepsTheLinkSoARetrySavesIt()
    {
        string folder = Directory.CreateTempSubdirectory("connapse-links-").FullName;
        try
        {
            ExampleDataDirectory data = new(folder);
            ExampleConfig config = ExampleServer.DefaultConfig() with { PresetLinks = [] };
            File.WriteAllText(Path.Combine(folder, "links.json"), """{ "connapse-carol": "bob" }""");
            LinkStore store = new(data, config, TimeProvider.System);

            // A directory where the temporary file must go makes the save fail.
            string blocker = Path.Combine(folder, "links.json.tmp");
            Directory.CreateDirectory(blocker);
            Action failing = () => store.Remove("connapse-carol");
            failing.Should().Throw<Exception>();
            store.ExternalUser("connapse-carol").Should().Be("bob", "an unlink that was not saved must not look done");

            Directory.Delete(blocker);
            store.Remove("connapse-carol");

            new LinkStore(data, config, TimeProvider.System).ExternalUser("connapse-carol")
                .Should().BeNull("the retried unlink must survive a restart");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
