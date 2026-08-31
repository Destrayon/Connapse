using FluentAssertions;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;

namespace Connapse.Identity.Tests;

/// <summary>
/// The AuthnRequest id Connapse records, and whether it is still the id that goes to AWS.
/// </summary>
/// <remarks>
/// The consumer refuses an assertion whose <c>InResponseTo</c> does not name the request this
/// deployment sent, and the expectation is recorded at the moment the request is built — before
/// <c>Bind</c> serialises it. If binding assigned a fresh id, the recorded one would name a request
/// that never existed and <b>every</b> sign-in would fail, in production only, with a reason code
/// that reads like an attack. That is worth a test rather than an assumption.
/// </remarks>
[Trait("Category", "Unit")]
public class SamlAuthnRequestIdTests
{
    private static Saml2Configuration Configuration() => new()
    {
        Issuer = "https://connapse.example.com/saml/connapse",
        SingleSignOnDestination =
            new Uri("https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE"),
    };

    private static Saml2AuthnRequest Request() =>
        new(Configuration())
        {
            AssertionConsumerServiceUrl =
                new Uri("https://connapse.example.com/api/v1/auth/cloud/aws/acs"),
        };

    [Fact]
    public void AnAuthnRequest_HasAnIdBeforeItIsBound()
    {
        // Read at this moment by the connect endpoint, so it has to exist at this moment.
        Request().Id.Value.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void BindingDoesNotChangeTheId()
    {
        // The one that matters. The endpoint records Id.Value, then binds; if these disagree, the
        // recorded expectation names nothing and no assertion can ever satisfy it.
        var request = Request();
        string before = request.Id.Value;

        new Saml2RedirectBinding { RelayState = "nonce" }.Bind(request);

        request.Id.Value.Should().Be(before);
    }

    [Fact]
    public void TheBoundRequestCarriesThatSameIdToTheIdentityProvider()
    {
        // What AWS actually receives is the deflated, base64 SAMLRequest in the redirect. Reading
        // the id back out of it proves the value Connapse stored is the value the assertion will be
        // echoing, rather than an internal handle that never left the process.
        var request = Request();
        string recorded = request.Id.Value;

        var binding = new Saml2RedirectBinding { RelayState = "nonce" };
        binding.Bind(request);

        binding.XmlDocument.DocumentElement!.GetAttribute("ID").Should().Be(recorded);
    }

    [Fact]
    public void IdsAreNotReusedBetweenRequests()
    {
        var ids = Enumerable.Range(0, 20).Select(_ => Request().Id.Value).ToList();

        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheIdIsAValidXmlIdentifier()
    {
        // SAML ids are xs:ID, which may not begin with a digit -- which is why the libraries prefix
        // a GUID with an underscore. A value that failed this would be rejected by the identity
        // provider rather than by anything here.
        string id = Request().Id.Value;

        id[0].Should().Match<char>(c => char.IsLetter(c) || c == '_');
        id.Should().NotContain(" ");
    }
}
