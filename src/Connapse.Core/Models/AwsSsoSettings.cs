namespace Connapse.Core;

public class AwsSsoSettings
{
    public const string SectionName = "Identity:AwsSso";

    /// <summary>
    /// The IAM Identity Center Issuer URL (e.g., https://d-1234567890.awsapps.com/start).
    /// Admin-configured in global settings.
    /// </summary>
    public string IssuerUrl { get; set; } = string.Empty;

    /// <summary>
    /// AWS region where IAM Identity Center is deployed (e.g., us-east-1).
    /// </summary>
    public string Region { get; set; } = string.Empty;

    // Auto-populated by RegisterClient API — managed by AwsSsoClientRegistrar

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public long? ClientSecretExpiresAt { get; set; }
}
