using System.Xml.Linq;

namespace Connapse.Core.Utilities;

/// <summary>The three values Connapse needs from an identity provider's SAML metadata.</summary>
/// <param name="EntityId">The issuer every assertion carries.</param>
/// <param name="SingleSignOnUrl">Where a person is sent to sign in.</param>
/// <param name="SigningCertificate">
/// Base64 DER, without PEM header lines. The public half of the key that signs assertions.
/// </param>
public record SamlMetadataValues(string EntityId, string SingleSignOnUrl, string SigningCertificate);

/// <summary>
/// Reads an identity provider's SAML metadata document so an administrator does not have to copy
/// three values out of it by hand.
/// </summary>
/// <remarks>
/// A paste rather than a URL Connapse fetches. Handing a server an address to retrieve makes it a
/// request-forgery surface, and it would undo the property the settings page states: these values
/// are stored so a self-hosted deployment needs no outbound access to AWS. The same copy-paste
/// round trip carries every other step of this setup.
/// <para>
/// Parsed with <see cref="XDocument"/> rather than the SAML library's own metadata types, so this
/// stays in Core, which has no dependencies — the same place the other setup parsers live. The
/// three values are at fixed, specified locations in the document; nothing here needs a full
/// metadata object model.
/// </para>
/// <para>
/// Deliberately not validating. It reads a document an administrator downloaded from their own
/// console into three form fields they can then see and correct. What makes an assertion
/// trustworthy is checked at sign-in, against whatever ends up stored.
/// </para>
/// </remarks>
public static class SamlMetadata
{
    private static readonly XNamespace Metadata = "urn:oasis:names:tc:SAML:2.0:metadata";

    private static readonly XNamespace Signature = "http://www.w3.org/2000/09/xmldsig#";

    /// <summary>Binding for the sign-in URL Connapse sends people to.</summary>
    /// <remarks>
    /// Redirect, not POST. Connapse starts sign-in with a redirect binding, so the POST endpoint —
    /// which Identity Center also advertises — is the wrong one of the two to store, and the
    /// mistake would only surface at the first sign-in attempt.
    /// </remarks>
    private const string RedirectBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect";

    /// <summary>
    /// Reads <paramref name="xml"/>. Returns null when it is not metadata, or is missing any of the
    /// three values.
    /// </summary>
    /// <remarks>
    /// All three or nothing. A partial fill would leave the administrator to work out which field
    /// the document did not cover, which is harder than copying all three.
    /// </remarks>
    public static SamlMetadataValues? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        XElement root;
        try
        {
            // DTD processing is off by default on this overload, which is what keeps a pasted
            // document from declaring an external entity and having the parser go and fetch it.
            root = XDocument.Parse(xml).Root!;
        }
        catch (System.Xml.XmlException)
        {
            // Not XML at all — usually a page copied out of a browser rather than the file.
            return null;
        }

        // A federation may publish several providers in one document. Connapse points at exactly
        // one application, so anything else is ambiguous and is refused rather than guessed at.
        var descriptors = root.Name == Metadata + "EntitiesDescriptor"
            ? root.Elements(Metadata + "EntityDescriptor").ToList()
            : [root];

        if (descriptors is not [{ } entity] || entity.Name != Metadata + "EntityDescriptor")
            return null;

        var idp = entity.Element(Metadata + "IDPSSODescriptor");
        if (idp is null)
            return null;

        string? entityId = (string?)entity.Attribute("entityID");

        string? ssoUrl = idp.Elements(Metadata + "SingleSignOnService")
            .Where(e => (string?)e.Attribute("Binding") == RedirectBinding)
            .Select(e => (string?)e.Attribute("Location"))
            .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

        // use="signing" names the key that signs assertions. A descriptor with no use at all serves
        // every purpose, so it counts too; one marked for encryption does not, and storing it would
        // fail every signature check.
        string? certificate = idp.Elements(Metadata + "KeyDescriptor")
            .Where(k => (string?)k.Attribute("use") is null or "signing")
            .Descendants(Signature + "X509Certificate")
            .Select(c => c.Value)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

        if (string.IsNullOrWhiteSpace(entityId)
            || string.IsNullOrWhiteSpace(ssoUrl)
            || string.IsNullOrWhiteSpace(certificate))
            return null;

        return new SamlMetadataValues(entityId.Trim(), ssoUrl.Trim(), Compact(certificate));
    }

    /// <summary>
    /// Strips the whitespace XML indentation adds inside a certificate's base64.
    /// </summary>
    /// <remarks>
    /// The value is pretty-printed across several indented lines in the document, and the newlines
    /// and leading spaces are not part of the base64. Left in, the certificate fails to decode at
    /// the first sign-in rather than here.
    /// </remarks>
    private static string Compact(string certificate) =>
        new(certificate.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
