namespace Connapse.Storage.Data.Entities;

public class CustomProviderEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>DataProtection ciphertext (purpose <c>CustomProvider.v1</c>) of the middle man's bearer token.</summary>
    public string? TokenProtected { get; set; }
    public int MaxCheckBatch { get; set; }
    public bool SupportsAccessKeys { get; set; }
    public string? LinkUrl { get; set; }

    /// <summary>DataProtection ciphertext (purpose <c>CustomProviderLink.v1</c>) of the secret link tokens are signed with.</summary>
    public string? LinkSecretProtected { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
