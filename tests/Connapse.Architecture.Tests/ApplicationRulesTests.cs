using Connapse.Application;
using Connapse.Architecture.Tests.Canaries;
using FluentAssertions;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Proof tests for #831 (epic #783 Phase 2.1): operations can't reach ASP.NET Core, and every
/// operation says what scope it needs and how risky it is. The canaries run the same rules over
/// planted violations, so a rule that goes blind fails instead of passing.
/// </summary>
[Trait("Category", "Unit")]
public class ApplicationRulesTests
{
    private static readonly System.Reflection.Assembly Application = typeof(OperationAttribute).Assembly;

    private static string ApplicationProject => File.ReadAllText(
        Path.Combine(RepoPaths.Root, "src", "Connapse.Application", "Connapse.Application.csproj"));

    [Fact]
    public void Application_HasNoAspNetCoreDependency()
    {
        ApplicationRules.AspNetCoreDependencies(Application, ApplicationProject).Should().BeEmpty(
            "operations run behind every surface, so they must not depend on HTTP or Razor types");
    }

    [Fact]
    public void EveryOperation_HasScopeAndRisk()
    {
        // Operations live in Connapse.Application; Web is scanned too, so one declared there is
        // still held to the rule.
        var types = Application.GetTypes().Concat(typeof(Program).Assembly.GetTypes());
        ApplicationRules.OperationsMissingMetadata(types).Should().BeEmpty();
    }

    [Fact]
    public void ArchRule_DetectsAspNetCoreReachingTheApplication()
    {
        const string project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
                <PackageReference Include="Microsoft.AspNetCore.Http.Abstractions" Version="2.3.0" />
              </ItemGroup>
            </Project>
            """;

        // Connapse.Web references ASP.NET Core assemblies, so it stands in for a polluted Application.
        ApplicationRules.AspNetCoreDependencies(typeof(Program).Assembly, project).Should().Contain(
        [
            "references Microsoft.AspNetCore.Http.Abstractions",
            "FrameworkReference Microsoft.AspNetCore.App",
            "PackageReference Microsoft.AspNetCore.Http.Abstractions",
            "DisableTransitiveFrameworkReferences is not true, so the ASP.NET Core framework flows in from Identity",
        ]);
    }

    [Fact]
    public void ArchRule_DetectsOperationsMissingIdScopeOrRisk()
    {
        ApplicationRules.OperationsMissingMetadata(typeof(PlantedOperationWithoutScope).Assembly.GetTypes()).Should().BeEquivalentTo(
        [
            "Connapse.Architecture.Tests.Canaries.PlantedOperationWithoutScope: no Scope",
            "Connapse.Architecture.Tests.Canaries.PlantedOperationWithoutRisk: no Risk",
            "Connapse.Architecture.Tests.Canaries.PlantedOperationWithUndefinedRisk: no Risk",
            "Connapse.Architecture.Tests.Canaries.PlantedOperationWithoutId: no id",
            "Connapse.Architecture.Tests.Canaries.PlantedAbstractOperation: abstract, so it is never registered",
            "planted.shared: on Connapse.Architecture.Tests.Canaries.PlantedSharedIdA, Connapse.Architecture.Tests.Canaries.PlantedSharedIdB",
        ]);
    }
}
