using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Connapse.Storage.Settings;

/// <summary>
/// Wrapper that allows us to use a pre-created DatabaseSettingsProvider as a configuration source.
/// </summary>
internal class DatabaseSettingsSourceWrapper(DatabaseSettingsProvider provider) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
}

/// <summary>
/// Extension methods for adding database-backed configuration.
/// </summary>
public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds database-backed settings that override appsettings.json values.
    /// Returns the DatabaseSettingsProvider instance for later use in DI registration.
    /// </summary>
    public static DatabaseSettingsProvider AddDatabaseSettings(
        this IConfigurationBuilder builder,
        Action<DbContextOptionsBuilder> optionsAction)
    {
        var provider = new DatabaseSettingsProvider(optionsAction, DeploymentValues(builder));
        builder.Add(new DatabaseSettingsSourceWrapper(provider));
        return provider;
    }

    /// <summary>
    /// Reads a key from the environment variables and command line added to <paramref name="builder"/> so far, the
    /// last added first, as configuration does. A blank value counts as not set: compose passes an unset
    /// variable through as an empty one.
    /// </summary>
    private static Func<string, string?> DeploymentValues(IConfigurationBuilder builder)
    {
        List<IConfigurationProvider> deployment = builder.Sources
            .Where(source => source is EnvironmentVariablesConfigurationSource or CommandLineConfigurationSource)
            .Select(source => source.Build(builder))
            .ToList();
        deployment.Reverse();
        deployment.ForEach(source => source.Load());

        return key =>
        {
            foreach (IConfigurationProvider source in deployment)
                if (source.TryGet(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                    return value;
            return null;
        };
    }

    /// <summary>
    /// Adds database-backed settings using a connection string.
    /// Returns the DatabaseSettingsProvider instance for later use in DI registration.
    /// </summary>
    public static DatabaseSettingsProvider AddDatabaseSettings(
        this IConfigurationBuilder builder,
        string connectionString)
    {
        return builder.AddDatabaseSettings(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
    }
}
