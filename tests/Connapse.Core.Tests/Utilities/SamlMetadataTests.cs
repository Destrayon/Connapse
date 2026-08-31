using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// Reading the three values Connapse needs out of an identity provider's SAML metadata.
/// </summary>
[Trait("Category", "Unit")]
public class SamlMetadataTests
{
    /// <summary>
    /// Shaped like what IAM Identity Center publishes: both bindings advertised, the certificate
    /// indented across several lines, and the signing key named by <c>use</c>.
    /// </summary>
    private const string IdentityCenterMetadata = """
        <?xml version="1.0" encoding="UTF-8"?>
        <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata"
                             entityID="https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE">
          <md:IDPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
            <md:KeyDescriptor use="signing">
              <ds:KeyInfo xmlns:ds="http://www.w3.org/2000/09/xmldsig#">
                <ds:X509Data>
                  <ds:X509Certificate>
                    MIICertificateFirstLine
                    SecondLine
                  </ds:X509Certificate>
                </ds:X509Data>
              </ds:KeyInfo>
            </md:KeyDescriptor>
            <md:SingleSignOnService
                Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST"
                Location="https://portal.sso.us-west-1.amazonaws.com/saml/assertion/POST" />
            <md:SingleSignOnService
                Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect"
                Location="https://portal.sso.us-west-1.amazonaws.com/saml/assertion/REDIRECT" />
          </md:IDPSSODescriptor>
        </md:EntityDescriptor>
        """;

    [Fact]
    public void Parse_ReadsAllThreeValues()
    {
        var values = SamlMetadata.Parse(IdentityCenterMetadata);

        values.Should().NotBeNull();
        values!.EntityId.Should()
            .Be("https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE");
    }

    [Fact]
    public void Parse_TakesTheRedirectEndpoint_NotThePostOne()
    {
        // Connapse starts sign-in with a redirect binding. Identity Center advertises both, and
        // storing the POST endpoint would only fail at the first sign-in attempt.
        SamlMetadata.Parse(IdentityCenterMetadata)!.SingleSignOnUrl
            .Should().EndWith("/REDIRECT");
    }

    [Fact]
    public void Parse_StripsTheIndentationOutOfTheCertificate()
    {
        // The document pretty-prints the base64 across indented lines, and none of that whitespace
        // is part of it. Left in, the certificate fails to decode at sign-in rather than here.
        SamlMetadata.Parse(IdentityCenterMetadata)!.SigningCertificate
            .Should().Be("MIICertificateFirstLineSecondLine");
    }

    [Fact]
    public void Parse_IgnoresAKeyThatIsNotForSigning()
    {
        // An encryption key would be stored happily and then fail every signature check.
        string xml = IdentityCenterMetadata.Replace(
            """<md:KeyDescriptor use="signing">""",
            """
            <md:KeyDescriptor use="encryption">
              <ds:KeyInfo xmlns:ds="http://www.w3.org/2000/09/xmldsig#">
                <ds:X509Data><ds:X509Certificate>WrongKey</ds:X509Certificate></ds:X509Data>
              </ds:KeyInfo>
            </md:KeyDescriptor>
            <md:KeyDescriptor use="signing">
            """);

        SamlMetadata.Parse(xml)!.SigningCertificate.Should().NotBe("WrongKey");
    }

    [Fact]
    public void Parse_AcceptsAKeyWithNoStatedUse()
    {
        // A descriptor with no `use` serves every purpose, so it is the signing key too.
        string xml = IdentityCenterMetadata.Replace("""<md:KeyDescriptor use="signing">""",
            "<md:KeyDescriptor>");

        SamlMetadata.Parse(xml)!.SigningCertificate.Should().Be("MIICertificateFirstLineSecondLine");
    }

    [Fact]
    public void Parse_ReadsADocumentWrappedInAnEntitiesDescriptor()
    {
        // A federation may publish its providers wrapped in one. A single entity inside is still
        // unambiguous.
        string xml = $"""
            <md:EntitiesDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata">
            {IdentityCenterMetadata.Replace("""<?xml version="1.0" encoding="UTF-8"?>""", "")}
            </md:EntitiesDescriptor>
            """;

        SamlMetadata.Parse(xml).Should().NotBeNull();
    }

    [Fact]
    public void Parse_WithSeveralProvidersInOneDocument_RefusesRatherThanGuessing()
    {
        // Connapse points at exactly one application, and picking the first would be a coin flip
        // that surfaces as everyone being sent to the wrong sign-in page.
        string body = IdentityCenterMetadata.Replace("""<?xml version="1.0" encoding="UTF-8"?>""", "");
        string xml = $"""
            <md:EntitiesDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata">
            {body}
            {body}
            </md:EntitiesDescriptor>
            """;

        SamlMetadata.Parse(xml).Should().BeNull();
    }

    [Fact]
    public void Parse_WithNoIdpDescriptor_ReturnsNull()
    {
        // Service provider metadata, which is the document travelling the other way. Pasting
        // Connapse's own back into Connapse is an easy mistake and should say nothing was found.
        string xml = """
            <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" entityID="https://x/saml">
              <md:SPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol" />
            </md:EntityDescriptor>
            """;

        SamlMetadata.Parse(xml).Should().BeNull();
    }

    [Fact]
    public void Parse_MissingAnyOneValue_ReturnsNullRatherThanFillingSome()
    {
        // A partial fill leaves the administrator to work out which field the document did not
        // cover, which is harder than copying all three.
        string xml = IdentityCenterMetadata.Replace("HTTP-Redirect", "HTTP-Artifact");

        SamlMetadata.Parse(xml).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // A page copied out of a browser rather than the file itself.
    [InlineData("Download metadata file  Copy URL")]
    [InlineData("<md:EntityDescriptor")]
    public void Parse_WithNothingUsable_ReturnsNull(string? xml)
    {
        SamlMetadata.Parse(xml).Should().BeNull();
    }
}
