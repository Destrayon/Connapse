namespace AIKnowledge.Storage.Data.Entities;

public class SettingEntity
{
    public string Category { get; set; } = string.Empty;
    public Dictionary<string, object> Values { get; set; } = new();
    public DateTime UpdatedAt { get; set; }
}
