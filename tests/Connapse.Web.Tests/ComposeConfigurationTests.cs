using Connapse.Web;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Connapse.Web.Tests;

[Trait("Category", "Unit")]
public class ComposeConfigurationTests
{
    private static ConfigurationManager Configuration(params (string Key, string Value)[] values)
    {
        ConfigurationManager configuration = new();
        configuration.AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)));
        return configuration;
    }

    [Theory]
    [InlineData("with-reranker")]
    [InlineData("other, with-reranker")]
    public void AddComposeSetup_RerankerSwitchedOn_TurnsRerankingOn(string extras)
    {
        ConfigurationManager configuration = Configuration(("Connapse:Extras", extras));

        configuration.AddComposeSetup();

        configuration["Knowledge:Search:Reranker"].Should().Be("CrossEncoder");
    }

    [Theory]
    [InlineData("")]
    [InlineData("with-reranker-gpu")]
    public void AddComposeSetup_RerankerNotSwitchedOn_LeavesRerankingAlone(string extras)
    {
        ConfigurationManager configuration = Configuration(("Connapse:Extras", extras));

        configuration.AddComposeSetup();

        configuration["Knowledge:Search:Reranker"].Should().BeNull();
    }

    [Fact]
    public void AddComposeSetup_RerankerAlreadySet_KeepsThatChoice()
    {
        ConfigurationManager configuration = Configuration(
            ("Connapse:Extras", "with-reranker"), ("Knowledge:Search:Reranker", "None"));

        configuration.AddComposeSetup();

        configuration["Knowledge:Search:Reranker"].Should().Be("None");
    }

    [Fact]
    public void AddComposeSetup_SecretsFolder_ReadsFilesAsNestedSettings()
    {
        string folder = Directory.CreateTempSubdirectory("con263-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "ConnectionStrings__DefaultConnection"), "Host=postgres");
            File.WriteAllText(Path.Combine(folder, "Knowledge__Storage__MinIO__SecretKey"), "abc");
            ConfigurationManager configuration = Configuration(("CONNAPSE_SECRETS_DIR", folder));

            configuration.AddComposeSetup();

            configuration.GetConnectionString("DefaultConnection").Should().Be("Host=postgres");
            configuration["Knowledge:Storage:MinIO:SecretKey"].Should().Be("abc");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AddComposeSetup_SameSettingInEnvironment_EnvironmentWins()
    {
        const string variable = "CON263__FROM_BOTH";
        string folder = Directory.CreateTempSubdirectory("con263-").FullName;
        Environment.SetEnvironmentVariable(variable, "environment");
        try
        {
            File.WriteAllText(Path.Combine(folder, variable), "file");
            File.WriteAllText(Path.Combine(folder, "CON263__ONLY_FILE"), "file");
            ConfigurationManager configuration = new();
            configuration.AddInMemoryCollection([new("CON263:ONLY_FILE", "appsettings")]);
            configuration.AddEnvironmentVariables();
            configuration.AddInMemoryCollection([new("CONNAPSE_SECRETS_DIR", folder)]);

            configuration.AddComposeSetup();

            configuration["CON263:FROM_BOTH"].Should().Be("environment");
            configuration["CON263:ONLY_FILE"].Should().Be("file");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AddComposeSetup_NothingFromCompose_AddsNothing()
    {
        ConfigurationManager configuration = Configuration();

        configuration.AddComposeSetup();

        configuration.AsEnumerable().Should().BeEmpty();
    }
}
