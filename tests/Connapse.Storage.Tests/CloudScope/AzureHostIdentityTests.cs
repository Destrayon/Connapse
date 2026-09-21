using Azure.Identity;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope;

/// <summary>
/// Detecting the host's managed identity from the token Azure issues it. The claims are what the
/// guided setup names the identity by, so each one has to be read from where Azure puts it.
/// </summary>
[Trait("Category", "Unit")]
public class AzureHostIdentityTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Principal = "22222222-2222-2222-2222-222222222222";
    private const string Client = "33333333-3333-3333-3333-333333333333";
    private const string Subscription = "44444444-4444-4444-4444-444444444444";

    private static string SystemAssignedToken() => AzureHostIdentity.EncodePayload(new
    {
        tid = Tenant, oid = Principal, appid = Client,
        xms_mirid = $"/subscriptions/{Subscription}/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
    });

    [Fact]
    public void Describe_SystemAssignedToken_ReadsTenantPrincipalClientAndSubscription()
    {
        AzureHostIdentityInfo? info = AzureHostIdentity.Describe(SystemAssignedToken());

        info.Should().NotBeNull();
        info!.TenantId.Should().Be(Tenant);
        info.PrincipalId.Should().Be(Principal);
        info.ClientId.Should().Be(Client);
        info.SubscriptionId.Should().Be(Subscription);
        info.IsUserAssigned.Should().BeFalse();
    }

    [Fact]
    public void Describe_UserAssignedToken_IsMarkedUserAssigned()
    {
        string token = AzureHostIdentity.EncodePayload(new
        {
            tid = Tenant, oid = Principal, appid = Client,
            xms_mirid = $"/subscriptions/{Subscription}/resourcegroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/connapse",
        });

        AzureHostIdentity.Describe(token)!.IsUserAssigned.Should().BeTrue();
    }

    [Fact]
    public void Describe_WithoutTheNamingClaims_IsNull()
    {
        AzureHostIdentity.Describe(AzureHostIdentity.EncodePayload(new { tid = Tenant })).Should().BeNull();
        AzureHostIdentity.Describe("not.a.token").Should().BeNull();
        AzureHostIdentity.Describe("garbage").Should().BeNull();
    }

    [Fact]
    public void Describe_WithoutAResourceId_StillNamesTheIdentity_WithoutASubscription()
    {
        AzureHostIdentityInfo? info = AzureHostIdentity.Describe(
            AzureHostIdentity.EncodePayload(new { tid = Tenant, oid = Principal, appid = Client }));

        info!.SubscriptionId.Should().BeNull();
        info.ResourceId.Should().BeNull();
    }

    [Theory]
    [InlineData("/subscriptions/44444444-4444-4444-4444-444444444444/resourceGroups/rg/providers/x", "44444444-4444-4444-4444-444444444444")]
    [InlineData("/SUBSCRIPTIONS/44444444-4444-4444-4444-444444444444/x", "44444444-4444-4444-4444-444444444444")]
    [InlineData("/subscriptions/not-a-guid/x", null)]
    [InlineData("", null)]
    public void SubscriptionFrom_ReadsTheSegmentAfterSubscriptions(string resourceId, string? expected) =>
        AzureHostIdentity.SubscriptionFrom(resourceId).Should().Be(expected);

    [Fact]
    public async Task DetectAsync_NoMetadataService_IsNull_NotAnException()
    {
        var probe = new AzureHostIdentity(NullLogger<AzureHostIdentity>.Instance)
        {
            AcquireToken = _ => throw new CredentialUnavailableException("ManagedIdentityCredential authentication unavailable"),
        };

        (await probe.DetectAsync()).Should().BeNull();
    }

    [Fact]
    public async Task DetectAsync_FoundOnce_IsRememberedWithoutAskingAgain()
    {
        int calls = 0;
        var probe = new AzureHostIdentity(NullLogger<AzureHostIdentity>.Instance)
        {
            AcquireToken = _ => { calls++; return Task.FromResult(SystemAssignedToken()); },
        };

        var first = await probe.DetectAsync();
        var second = await probe.DetectAsync();

        first.Should().NotBeNull();
        second.Should().BeSameAs(first);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task DetectAsync_Refresh_AsksAgain_AndForgetsAnIdentityThatIsGone()
    {
        // Recheck asks with refresh: an identity re-created since the process cached it comes back
        // new, and one that was removed is not handed out from the cache any more.
        int calls = 0;
        var probe = new AzureHostIdentity(NullLogger<AzureHostIdentity>.Instance)
        {
            AcquireToken = _ =>
            {
                calls++;
                if (calls == 1) return Task.FromResult(SystemAssignedToken());
                throw new CredentialUnavailableException("gone");
            },
        };

        (await probe.DetectAsync()).Should().NotBeNull();
        (await probe.DetectAsync(refresh: true)).Should().BeNull();
        (await probe.DetectAsync()).Should().BeNull("the cache must not resurrect a removed identity");
        calls.Should().Be(3);
    }

    [Fact]
    public async Task DetectAsync_NotFound_AsksAgainNextTime()
    {
        // A host that had no identity may gain one (or the metadata service may have been slow);
        // only a found identity is worth remembering.
        int calls = 0;
        var probe = new AzureHostIdentity(NullLogger<AzureHostIdentity>.Instance)
        {
            AcquireToken = _ => { calls++; throw new CredentialUnavailableException("none"); },
        };

        await probe.DetectAsync();
        await probe.DetectAsync();

        calls.Should().Be(2);
    }
}
