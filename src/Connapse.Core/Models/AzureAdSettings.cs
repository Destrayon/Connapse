namespace Connapse.Core;

public class AzureAdSettings
{
    public const string SectionName = "Identity:AzureAd";

    public string ClientId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}
