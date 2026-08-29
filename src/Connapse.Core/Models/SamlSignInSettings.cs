namespace Connapse.Core;

/// <summary>
/// The IAM Identity Center SAML application a person signs in through to prove which directory
/// user they are.
/// </summary>
/// <remarks>
/// Two halves that travel in opposite directions. <see cref="EntityId"/> and <see cref="AcsUrl"/>
/// are Connapse's, and an administrator types them into the AWS console when creating the
/// application. The three <c>Idp</c> values are Identity Center's, and come back the other way out
/// of that application's metadata.
/// <para>
/// The identity provider's metadata is stored decomposed rather than as a URL Connapse fetches at
/// sign-in. A self-hosted deployment need not have outbound access to AWS — the assertion reaches
/// it through the person's browser, not over the network — and a metadata fetch would put an
/// outbound dependency in the one path that has none. It would also turn an AWS outage into a
/// sign-in outage.
/// </para>
/// <para>
/// Holds no secret. A signing certificate is a public key, and the two Connapse values are printed
/// on the setup page for an administrator to copy. Nothing here is withheld from the settings API.
/// </para>
/// </remarks>
public class SamlSignInSettings
{
    public const string SectionName = "Identity:SamlSignIn";

    /// <summary>
    /// Connapse's SAML entity id, which the application registers as its audience.
    /// </summary>
    /// <remarks>
    /// Checked against every assertion's <c>AudienceRestriction</c>. An assertion minted for some
    /// other service provider is refused here, which is what stops one issued for a different
    /// application in the same directory from being replayed at Connapse.
    /// </remarks>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Where Identity Center POSTs the assertion — Connapse's assertion consumer service.</summary>
    /// <remarks>
    /// Also checked against the assertion's <c>Destination</c>. It has to match what was registered
    /// in the console exactly, so moving a deployment to a new URL means editing the application.
    /// </remarks>
    public string AcsUrl { get; set; } = string.Empty;

    /// <summary>Identity Center's entity id, the <c>Issuer</c> every assertion must carry.</summary>
    public string IdpEntityId { get; set; } = string.Empty;

    /// <summary>Where the sign-in request is sent, from the application's metadata.</summary>
    public string IdpSingleSignOnUrl { get; set; } = string.Empty;

    /// <summary>
    /// The base64 X.509 certificate assertions are signed with, from the same metadata.
    /// </summary>
    /// <remarks>
    /// The whole of the trust. Identity Center rotates it, and a rotation that is not reflected
    /// here fails every sign-in until the metadata is pasted again.
    /// </remarks>
    public string IdpSigningCertificate { get; set; } = string.Empty;

    /// <summary>True once both halves have been exchanged and sign-in can be attempted.</summary>
    /// <remarks>
    /// All five, because a partial record is worse than an empty one: without the certificate
    /// there is nothing to validate against, and a sign-in that got that far would have to either
    /// fail late or trust an unverified assertion.
    /// </remarks>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(EntityId)
        && !string.IsNullOrWhiteSpace(AcsUrl)
        && !string.IsNullOrWhiteSpace(IdpEntityId)
        && !string.IsNullOrWhiteSpace(IdpSingleSignOnUrl)
        && !string.IsNullOrWhiteSpace(IdpSigningCertificate);
}
