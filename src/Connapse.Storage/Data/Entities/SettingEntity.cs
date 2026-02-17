using System.Text.Json;

namespace Connapse.Storage.Data.Entities;

public class SettingEntity
{
    public string Category { get; set; } = string.Empty;
    public JsonDocument Values { get; set; } = null!;
    public DateTime UpdatedAt { get; set; }
}
