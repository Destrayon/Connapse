using System.Collections.Specialized;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using Connapse.Core;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;

namespace Connapse.Identity.Services;

/// <summary>
/// Validates a SAML response from IAM Identity Center and reports which directory user it names.
/// </summary>
/// <remarks>
/// This is the whole of the trust. Connapse took over being the service provider from a Cognito
/// user pool, and a pool is a hardened SAML implementation that was doing this on our behalf; a
/// forged assertion accepted here is not a degraded search, it is one person searching as another.
/// <para>
/// <c>Unbind</c> validates the signature and the audience. Everything after it is checked here
/// rather than assumed, because the failure mode of assuming is silent: the issuer being the
/// Identity Center instance this deployment was configured against, the destination, the
/// assertion's own validity window, and single use. SAML's history of signature-wrapping attacks is
/// a history of implementations that validated one part of a document and read another.
/// </para>
/// <para>
/// Takes the raw form value rather than an <c>HttpRequest</c> so every rejection path can be
/// exercised with a locally minted assertion and a throwaway key, without an Identity Center
/// instance to produce a genuine one.
/// </para>
/// </remarks>
public static class SamlAssertionValidator
{
    /// <summary>How far a clock may drift before a valid assertion is refused.</summary>
    /// <remarks>
    /// Two minutes, matching the ID token validator this replaced. Identity Center stamps the
    /// window; a self-hosted Connapse whose clock has drifted further than this has a problem worth
    /// reporting rather than absorbing.
    /// </remarks>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Validates the base64 <c>SAMLResponse</c> a browser posted, and returns the directory user
    /// it names.
    /// </summary>
    /// <param name="samlResponse">The raw form value, still base64.</param>
    /// <param name="settings">What this deployment registered in the AWS console, and what came back.</param>
    /// <param name="replayGuard">Records assertion ids so one cannot be posted twice.</param>
    /// <param name="now">Supplied rather than read, so lifetime rejection is testable.</param>
    public static SamlAssertionResult Validate(
        string samlResponse,
        SamlSignInSettings settings,
        ISamlReplayGuard replayGuard,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(replayGuard);

        if (string.IsNullOrWhiteSpace(samlResponse))
            return SamlAssertionResult.Rejected("response_missing");
        if (!settings.IsConfigured)
            return SamlAssertionResult.Rejected("not_configured");

        Saml2Configuration configuration;
        try
        {
            configuration = BuildConfiguration(settings);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or UriFormatException)
        {
            // The stored certificate or a URL is unusable. A configuration fault rather than a bad
            // assertion, and worth its own reason so it is not investigated as an attack.
            return SamlAssertionResult.Rejected("configuration_invalid");
        }

        Saml2AuthnResponse response = new(configuration);
        ITfoxtec.Identity.Saml2.Http.HttpRequest request = new()
        {
            Method = "POST",
            Binding = new Saml2PostBinding(),
            Form = new NameValueCollection { { "SAMLResponse", samlResponse } },
        };

        try
        {
            request.Binding.ReadSamlResponse(request, response);
        }
        catch (Exception)
        {
            // Deliberately broad, and deliberately opaque. XML parsing failures surface as a wide
            // range of types and the message can quote the document — which is somebody's
            // assertion and must not reach a log or a page.
            return SamlAssertionResult.Rejected("response_malformed");
        }

        // Read before Unbind, matching the library's own sample: a failed sign-in is reported as a
        // status rather than as a signed assertion, and reading it first gives a usable reason.
        if (response.Status != Saml2StatusCodes.Success)
            return SamlAssertionResult.Rejected("status_not_success");

        try
        {
            // Signature and audience validation. Everything above this line is untrusted input.
            request.Binding.Unbind(request, response);
        }
        catch (Exception)
        {
            return SamlAssertionResult.Rejected("signature_invalid");
        }

        // Rule: the assertion came from the instance this deployment was configured against. The
        // signature proves who signed it, not that we meant to trust them.
        if (!string.Equals(response.Issuer, settings.IdpEntityId, StringComparison.Ordinal))
            return SamlAssertionResult.Rejected("issuer_mismatch");

        // Rule: the assertion was addressed to this endpoint. Without it, one captured at another
        // service provider in the same directory can be posted here.
        if (response.Destination is not null &&
            !string.Equals(response.Destination.AbsoluteUri, settings.AcsUrl, StringComparison.OrdinalIgnoreCase))
        {
            return SamlAssertionResult.Rejected("destination_mismatch");
        }

        // Rule: the assertion is inside its own validity window.
        DateTimeOffset validFrom = response.SecurityTokenValidFrom.ToUniversalTime();
        DateTimeOffset validTo = response.SecurityTokenValidTo.ToUniversalTime();
        if (now < validFrom.Add(-ClockSkew))
            return SamlAssertionResult.Rejected("assertion_not_yet_valid");
        if (now >= validTo.Add(ClockSkew))
            return SamlAssertionResult.Rejected("assertion_expired");

        // Rule: once only. A signed assertion is a bearer credential until it expires, so an
        // observer who obtains one may otherwise use it as often as they like within the window.
        string? assertionId = response.Saml2SecurityToken?.Assertion?.Id?.Value;
        if (string.IsNullOrWhiteSpace(assertionId))
            return SamlAssertionResult.Rejected("assertion_id_missing");
        if (!replayGuard.TryRegister(assertionId, validTo.Add(ClockSkew)))
            return SamlAssertionResult.Rejected("assertion_replayed");

        ClaimsIdentity? identity = response.ClaimsIdentity;
        if (identity is null)
            return SamlAssertionResult.Rejected("no_directory_user");

        // The join key. Identity Center's Subject row is mapped to ${user:subject}, which carries
        // the directory user name — not ${user:preferredUsername}, which resolves to the display
        // name and matches nobody.
        //
        // Not case-folded. Addresses are conventionally case-insensitive; user names are not, and
        // this one belongs to a directory Connapse does not own.
        string? userName = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
                           ?? identity.FindFirst("userName")?.Value;
        if (string.IsNullOrWhiteSpace(userName))
            return SamlAssertionResult.Rejected("no_directory_user");

        // Display only. Nothing may make an authorization decision from it.
        string? email = identity.FindFirst(ClaimTypes.Email)?.Value
                        ?? identity.FindFirst("email")?.Value;

        return SamlAssertionResult.Accepted(userName.Trim(), email);
    }

    /// <summary>
    /// Builds the library configuration from stored settings.
    /// </summary>
    /// <remarks>
    /// Chain validation is switched off deliberately. The certificate is pinned by having been
    /// pasted in from the application's own metadata, so it is trusted because it is that exact
    /// certificate rather than because an authority vouches for it — and requiring a chain would
    /// reject Identity Center's self-signed signing certificate outright.
    /// </remarks>
    private static Saml2Configuration BuildConfiguration(SamlSignInSettings settings)
    {
        Saml2Configuration configuration = new()
        {
            Issuer = settings.EntityId,
            SingleSignOnDestination = new Uri(settings.IdpSingleSignOnUrl),
            CertificateValidationMode = X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };

        configuration.AllowedAudienceUris.Add(settings.EntityId);
        configuration.SignatureValidationCertificates.Add(
            X509CertificateLoader.LoadCertificate(Convert.FromBase64String(settings.IdpSigningCertificate)));

        return configuration;
    }
}

/// <summary>Remembers assertion ids for as long as they could still be replayed.</summary>
/// <remarks>
/// An interface so the validator stays pure and the store can be swapped. A single-process cache is
/// enough for one Connapse; a deployment running several would need one they share, or an assertion
/// accepted by one could be posted to another.
/// </remarks>
public interface ISamlReplayGuard
{
    /// <summary>Records <paramref name="assertionId"/>, or reports that it was already used.</summary>
    /// <returns><see langword="false"/> when this assertion has been seen before.</returns>
    bool TryRegister(string assertionId, DateTimeOffset expiresAt);
}

/// <summary>
/// The outcome of validating a SAML response: the directory user it names, or why it names none.
/// </summary>
/// <remarks>
/// <see cref="Email"/> is display data. <see cref="DirectoryUserName"/> is the identifier the
/// Identity Store resolves into the UUID that access grants are held against.
/// </remarks>
public sealed record SamlAssertionResult(
    bool Success, string? DirectoryUserName, string? Email, string? FailureReason)
{
    public static SamlAssertionResult Accepted(string directoryUserName, string? email) =>
        new(true, directoryUserName, email, null);

    public static SamlAssertionResult Rejected(string reason) => new(false, null, null, reason);
}
