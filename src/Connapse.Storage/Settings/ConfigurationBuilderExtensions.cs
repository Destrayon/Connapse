using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
        var provider = new DatabaseSettingsProvider(optionsAction);
        builder.Add(new DatabaseSettingsSourceWrapper(provider));
        return provider;
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
