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

    // Written by restore, which every build runs first.
    private static string ApplicationAssets => File.ReadAllText(
        Path.Combine(RepoPaths.Root, "src", "Connapse.Application", "obj", "project.assets.json"));

    [Fact]
    public void Application_HasNoAspNetCoreDependency()
    {
        ApplicationRules.AspNetCoreDependencies(Application, ApplicationProject, ApplicationAssets).Should().BeEmpty(
            "operations run behind every surface, so they must not depend on HTTP or Razor types");
    }

    [Fact]
    public void EveryOperation_HasScopeAndRisk()
    {
        // Operations live in Connapse.Application; Web is scanned too, so one declared there fails.
        var types = Application.GetTypes().Concat(typeof(Program).Assembly.GetTypes());
        ApplicationRules.OperationsMissingMetadata(types, Application).Should().BeEmpty();
    }

    private static ISet<string> DocumentedSlugs => ApplicationRules.DocumentedSlugs(
        File.ReadAllText(Path.Combine(RepoPaths.Root, "docs", "problems.md")));

    [Fact]
    public void EveryOperationFailure_MapsToADocumentedProblem()
    {
        ApplicationRules.FailuresWithoutProblem(Application.GetTypes(), DocumentedSlugs).Should().BeEmpty(
            "REST answers each failure with its ProblemDetails type, which must resolve to an entry in docs/problems.md");
    }

    [Fact]
    public void ArchRule_DetectsFailuresWithoutAProblem()
    {
        var planted = typeof(PlantedOperationWithFailures).Assembly;
        ApplicationRules.FailuresWithoutProblem(planted.GetTypes(), new HashSet<string> { "planted-not-found", "planted-ok" }).Should().BeEquivalentTo(
        [
            "Connapse.Architecture.Tests.Canaries.PlantedStatus.Unmapped: no [Failure]",
            "Connapse.Architecture.Tests.Canaries.PlantedStatus.NotAFailure: HTTP 200 is not a failure status",
            "Connapse.Architecture.Tests.Canaries.PlantedStatus.Undocumented: slug planted-undocumented has no entry in docs/problems.md",
            "Connapse.Architecture.Tests.Canaries.PlantedStatus.Conflicting: slug planted-not-found already means HTTP 404 \"Planted not found\" on Connapse.Architecture.Tests.Canaries.PlantedStatus.NotFound",
        ]);
    }

    [Fact]
    public void ArchRule_DetectsAspNetCoreReachingTheApplication()
    {
        const string project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
                <PackageReference Include="Microsoft.AspNetCore.Http.Abstractions" Version="2.3.0" />
                <PackageReference Include="Microsoft.AspNetCore.DataProtection" Version="10.0.7" ExcludeAssets="compile" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """;
        // JwtBearer reaches the compiler; DataProtection is kept from it, so only JwtBearer counts.
        const string assets = """
            {
              "libraries": {
                "Microsoft.AspNetCore.Authentication.JwtBearer/10.0.7": { "type": "package" },
                "Microsoft.AspNetCore.DataProtection/10.0.7": { "type": "package" },
                "Connapse.Core/1.0.0": { "type": "project" }
              },
              "project": { "frameworks": { "net10.0": { "dependencies": {
                "Microsoft.AspNetCore.DataProtection": { "include": "Runtime, Build, Native, ContentFiles, Analyzers, BuildTransitive" }
              } } } }
            }
            """;

        // Connapse.Web references ASP.NET Core assemblies, so it stands in for a polluted Application.
        var found = ApplicationRules.AspNetCoreDependencies(typeof(Program).Assembly, project, assets).ToList();

        found.Should().Contain(
        [
            "references Microsoft.AspNetCore.Http.Abstractions",
            "FrameworkReference Microsoft.AspNetCore.App",
            "PackageReference Microsoft.AspNetCore.Http.Abstractions without ExcludeAssets=\"compile\"",
            "DisableTransitiveFrameworkReferences is not true, so the ASP.NET Core framework flows in from Identity",
            "compiles against package Microsoft.AspNetCore.Authentication.JwtBearer",
        ]);
        // Web itself references DataProtection; what must not be reported is the excluded package.
        found.Should().NotContain(
        [
            "PackageReference Microsoft.AspNetCore.DataProtection without ExcludeAssets=\"compile\"",
            "compiles against package Microsoft.AspNetCore.DataProtection",
        ]);
    }

    [Fact]
    public void ArchRule_DetectsOperationsMissingIdScopeOrRisk()
    {
        var planted = typeof(PlantedOperationWithoutScope).Assembly;
        ApplicationRules.OperationsMissingMetadata(planted.GetTypes(), planted).Should().BeEquivalentTo(
        [
            "Connapse.Architecture.Tests.Canaries.PlantedOperationWithoutScope: no Scope",
            "Connapse.Architecture.Tests.Canaries.PlantedOperationWithoutRisk: no Risk",
            "Connapse.Architecture.Tests.Canaries.PlantedOperationWithUndefinedRisk: no Risk",
            "Connapse.Architecture.Tests.Canaries.PlantedOperationWithoutId: no id",
            "Connapse.Architecture.Tests.Canaries.PlantedAbstractOperation: abstract, so it is never registered",
            "planted.shared: on Connapse.Architecture.Tests.Canaries.PlantedSharedIdA, Connapse.Architecture.Tests.Canaries.PlantedSharedIdB",
        ]);
    }

    [Fact]
    public void ArchRule_DetectsAnOperationOutsideTheApplication()
    {
        ApplicationRules.OperationsMissingMetadata([typeof(PlantedCompleteOperation)], Application).Should().Equal(
            "Connapse.Architecture.Tests.Canaries.PlantedCompleteOperation: outside Connapse.Application, so AddApplication never registers it");
    }
}
