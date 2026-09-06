using Azure.Core;
using Connapse.Core;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Connapse's own Azure identity: an explicit certificate-or-ambient-managed-identity chain,
/// rebuilt when settings reload. The single TokenCredential every Azure SDK client uses.
/// </summary>
public sealed class ConnapseAzureCredentials : TokenCredential, IDisposable
{
    public const string ProviderKey = "azure";

    private readonly IOptionsMonitor<AzureProviderSettings> _options;
    private readonly IDisposable? _reload;
    private readonly object _gate = new();
    private TokenCredential _current;

    public ConnapseAzureCredentials(IOptionsMonitor<AzureProviderSettings> options)
    {
        _options = options;
        _current = Build(options.CurrentValue);
        _reload = options.OnChange(settings =>
        {
            lock (_gate) { _current = Build(settings); }
        });
    }

    private TokenCredential Current { get { lock (_gate) { return _current; } } }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken ct) =>
        Current.GetToken(requestContext, ct);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken ct) =>
        Current.GetTokenAsync(requestContext, ct);

    private static TokenCredential Build(AzureProviderSettings settings) =>
        AzureCredentialChainFactory.Create(settings, LoadCertificate);

    private static X509Certificate2? LoadCertificate(AzureProviderSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.ClientCertificatePath)) return null;
        if (!File.Exists(s.ClientCertificatePath)) return null;

        string ext = Path.GetExtension(s.ClientCertificatePath).ToLowerInvariant();
        return ext is ".pem" or ".crt"
            ? X509Certificate2.CreateFromPemFile(s.ClientCertificatePath)
            : X509CertificateLoader.LoadPkcs12FromFile(s.ClientCertificatePath, s.ClientCertificatePassword);
    }

    public void Dispose() => _reload?.Dispose();
}
