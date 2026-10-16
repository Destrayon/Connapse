namespace Connapse.Storage.Data.Entities;

/// <summary>An unlink owed to a custom provider's middle man for a deleted Connapse user.</summary>
public class CustomLinkRemovalEntity
{
    public Guid Id { get; set; }

    /// <summary>The provider it was queued for. Not a foreign key: the entry outlives the provider.</summary>
    public Guid ProviderId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>The middle man's address when queued.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The provider's token ciphertext when queued (purpose <c>CustomProvider.v1</c>).</summary>
    public string? TokenProtected { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Attempts { get; set; }

    /// <summary>Null while the user's deletion has not been confirmed: nothing is sent until then.</summary>
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>When the failure was first reported to administrators; null until then.</summary>
    public DateTime? AlertedAt { get; set; }
}
