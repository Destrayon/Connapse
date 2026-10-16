namespace Connapse.Storage.Data.Entities;

/// <summary>An unlink owed to a custom provider's middle man for a deleted Connapse user.</summary>
public class CustomLinkRemovalEntity
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>When the failure was first reported to administrators; null until then.</summary>
    public DateTime? AlertedAt { get; set; }
}
