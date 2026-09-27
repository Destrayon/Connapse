using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using FluentAssertions;

namespace Connapse.Eval.Tests.Datasets;

[Trait("Category", "Unit")]
public class ManifestTests
{
    private static readonly EvalManifest Manifest =
        EvalManifest.Load(RepoPaths.Find(AppContext.BaseDirectory).ManifestPath);

    [Fact]
    public void Suites_DevAndV1_ShareNoDatasets()
    {
        // The dev suite exists to tune against; the harness scores it like a test set, so the only
        // thing keeping tuning off the headline numbers is that no dataset sits in both suites.
        Manifest.Suites["dev"].Should().NotIntersectWith(Manifest.Suites["v1"]);
    }

    [Fact]
    public void Datasets_EverySuiteMemberIsPinned()
    {
        foreach (string name in Manifest.Suites.Values.SelectMany(s => s))
            Manifest.Datasets[name].Files.Should().OnlyContain(f => f.Sha256 != null, $"{name} must be pinned");
    }
}
