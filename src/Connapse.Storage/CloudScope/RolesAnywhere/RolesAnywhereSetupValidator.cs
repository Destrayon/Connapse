using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.CloudScope.RolesAnywhere;

/// <summary>The outcome of a pre-save Roles Anywhere check: whether AWS issued credentials, and why not.</summary>
public sealed record RolesAnywhereValidationResult(bool Ok, string? Error);

/// <summary>
/// Proves a Roles Anywhere config works before it is persisted.
/// </summary>
/// <remarks>
/// <see cref="IProviderCredentialStore.SaveRolesAnywhereAsync"/> overwrites irreversibly, so a bad
/// cert, a mistyped ARN, or a wrong region must be caught before it destroys the last working
/// credential. This performs the same <c>CreateSession</c> the runtime would — pairing the cert with
/// its key and asking AWS for temporary credentials — and reports success or the AWS reason, writing
/// nothing. The caller saves only on <see cref="RolesAnywhereValidationResult.Ok"/>.
/// </remarks>
public interface IRolesAnywhereSetupValidator
{
    Task<RolesAnywhereValidationResult> ValidateAsync(
        RolesAnywhereConfig config, string privateKeyPem, CancellationToken ct = default);
}

/// <inheritdoc cref="IRolesAnywhereSetupValidator"/>
public sealed class RolesAnywhereSetupValidator(IHttpClientFactory httpClientFactory)
    : IRolesAnywhereSetupValidator
{
    public async Task<RolesAnywhereValidationResult> ValidateAsync(
        RolesAnywhereConfig config, string privateKeyPem, CancellationToken ct = default)
    {
        X509Certificate2? certificate = null;
        try
        {
            // CreateFromPem throws if the private key does not match the certificate's public key, so
            // this line alone catches a mismatched cert/key pair before any network call.
            certificate = X509Certificate2.CreateFromPem(config.CertificatePem, privateKeyPem);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return new RolesAnywhereValidationResult(
                false, $"The certificate and private key are not a valid matching pair: {ex.Message}");
        }

        try
        {
            var client = new RolesAnywhereClient(
                httpClientFactory.CreateClient(ConnapseAwsCredentials.RolesAnywhereHttpClientName));
            var parameters = new RolesAnywhereParameters(
                config.TrustAnchorArn, config.ProfileArn, config.RoleArn, config.Region);

            await client.CreateSessionAsync(certificate, parameters, DateTimeOffset.UtcNow, ct);
            return new RolesAnywhereValidationResult(true, null);
        }
        catch (RolesAnywhereException ex)
        {
            // AWS answered — the status + body carry the actual reason (bad ARN, untrusted anchor,
            // clock skew). Surface it verbatim rather than a generic failure.
            return new RolesAnywhereValidationResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            return new RolesAnywhereValidationResult(false, ex.Message);
        }
        finally
        {
            certificate?.Dispose();
        }
    }
}
