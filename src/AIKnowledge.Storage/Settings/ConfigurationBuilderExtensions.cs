using AIKnowledge.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AIKnowledge.Storage.Settings;

/// <summary>
/// Extension methods for adding database-backed configuration.
/// </summary>
public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds database-backed settings that override appsettings.json values.
    /// </summary>
    public static IConfigurationBuilder AddDatabaseSettings(
        this IConfigurationBuilder builder,
        Action<DbContextOptionsBuilder> optionsAction)
    {
        return builder.Add(new DatabaseSettingsSource(optionsAction));
    }

    /// <summary>
    /// Adds database-backed settings using a connection string.
    /// </summary>
    public static IConfigurationBuilder AddDatabaseSettings(
        this IConfigurationBuilder builder,
        string connectionString)
    {
        return builder.AddDatabaseSettings(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
    }
}
