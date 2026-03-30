namespace Connapse.Identity.Data.Entities;

public class OAuthClientEntity
{
    public Guid Id { get; set; }
    public string ClientId { get; set; } = "";
    public string? ClientSecretHash { get; set; }
    public string ClientName { get; set; } = "";
    public string RedirectUris { get; set; } = "[]";
    public string ApplicationType { get; set; } = "native";
    public DateTime CreatedAt { get; set; }
}
