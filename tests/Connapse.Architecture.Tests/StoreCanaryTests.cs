using Connapse.Architecture.Tests.Canaries;
using FluentAssertions;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Proves the store rules can still see: planted violations must be reported. If a rule is weakened
/// or a scan goes blind, the production lists would keep passing; these fail instead.
/// </summary>
[Trait("Category", "Unit")]
public class StoreCanaryTests
{
    private static readonly string[] CanaryNamespace = [typeof(PlantedEndpoints).Namespace!];
    private static System.Reflection.Assembly TestAssembly => typeof(PlantedEndpoints).Assembly;

    [Fact]
    public void ArchRule_DetectsPlantedStoreInjection()
    {
        Rules.ComponentStoreInjections(TestAssembly)
            .Should().Contain("Connapse.Architecture.Tests.Canaries.PlantedComponent -> ISourceStore");
    }

    [Fact]
    public void ArchRule_DetectsPlantedStoreInsideAsyncLambda()
    {
        Rules.HandlerStoreDependencies(TestAssembly, CanaryNamespace)
            .Should().Contain("PlantedEndpoints -> ISourceStore");
    }
}
