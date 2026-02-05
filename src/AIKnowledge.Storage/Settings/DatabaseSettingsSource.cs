using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AIKnowledge.Storage.Settings;

/// <summary>
/// Configuration source for database-backed settings.
/// </summary>
public class DatabaseSettingsSource : IConfigurationSource
{
    private readonly Action<DbContextOptionsBuilder> _optionsAction;

    public DatabaseSettingsSource(Action<DbContextOptionsBuilder> optionsAction)
    {
        _optionsAction = optionsAction;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new DatabaseSettingsProvider(_optionsAction);
    }
}
