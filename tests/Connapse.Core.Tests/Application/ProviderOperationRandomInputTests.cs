using System.Net;
using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Providers;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope.RolesAnywhere;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Storage.Settings;
using CsCheck;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>The provider setup operations' rules over random callers (#783 Phase 4.4): who is refused, and that a refusal asks no provider.</summary>
[Trait("Category", "Unit")]
public class ProviderOperationRandomInputTests
{
    [Fact]
    [RandomInputTestOf(typeof(GetProviderStatus))]
    public Task Status_AnyCaller_ReadsProvidersOnlyForSettingsAdmins() =>
        Gen.Select(Gen.Bool, Gen.Enum<OperationSurface>(), Gen.Int[0, 4]).SampleAsync(async t =>
        {
            var (allowed, surface, count) = t;
            var reader = Substitute.For<IProviderSetupReader>();
            IReadOnlyList<ProviderSetup> setups = [.. Enumerable.Range(0, count).Select(i => new ProviderSetup($"p{i}", $"P{i}", []))];
            reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(setups);
            var authorizer = Substitute.For<IOperationAuthorizer>();
            authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(false);
            authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.SettingsAdmin).Returns(allowed);

            var read = await new GetProviderStatus(reader, authorizer).RunAsync(new OperationContext(new ClaimsPrincipal(), surface));

            if (!allowed)
            {
                read.Status.Should().Be(ProviderStatus.Forbidden);
                reader.ReceivedCalls().Should().BeEmpty("a refused caller must not make Connapse call AWS, Entra or GitHub");
                return;
            }
            read.Status.Should().Be(ProviderStatus.Success);
            read.Result.Should().BeSameAs(setups);
        }, iter: 200);

    private const string SavedUrl = "https://saved.example";

    private static readonly Gen<string?> GenUrl = Gen.OneOfConst<string?>(null, SavedUrl, "https://other.example", "", "not a url");

    private static readonly Gen<string?> GenToken = Gen.OneOfConst<string?>(null, "", "  ", "a-token");

    [Fact]
    [RandomInputTestOf(typeof(ListCustomProviders))]
    public Task ListCustom_AnyCaller_ListsEachProviderWithItsConnections() =>
        Gen.Select(Gen.Bool, Gen.Int[0, 3], Gen.Int[0, 3]).SampleAsync(async t =>
        {
            var (allowed, providerCount, connectionCount) = t;
            var (store, connections) = (Substitute.For<ICustomProviderStore>(), Substitute.For<IConnectionStore>());
            IReadOnlyList<CustomProvider> providers = [.. Enumerable.Range(0, providerCount).Select(_ => Provider(Guid.NewGuid()))];
            store.ListAsync(Arg.Any<CancellationToken>()).Returns(providers);
            IReadOnlyList<Connection> using_ = [.. Enumerable.Range(0, providerCount == 0 ? 0 : connectionCount)
                .Select(i => Using(providers[i % providerCount].Id, $"c{i}"))];
            connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(using_);

            var read = await new ListCustomProviders(store, connections, Authorizer(allowed)).RunAsync(Context());

            if (!allowed)
            {
                read.Status.Should().Be(CustomProviderStatus.Forbidden);
                store.ReceivedCalls().Should().BeEmpty();
                return;
            }
            read.Result!.Select(l => l.Provider).Should().Equal(providers);
            read.Result!.Sum(l => l.Connections.Count).Should().Be(using_.Count);
        }, iter: 200);

    [Fact]
    [RandomInputTestOf(typeof(CheckCustomProvider))]
    public Task CheckCustom_AnyRequest_SendsTheSavedTokenOnlyToTheSavedAddress() =>
        Gen.Select(Gen.Bool, Gen.Bool, GenUrl, GenToken).SampleAsync(async t =>
        {
            var (allowed, saved, url, token) = t;
            var store = Substitute.For<ICustomProviderStore>();
            Guid id = Guid.NewGuid();
            store.GetAsync(id, Arg.Any<CancellationToken>()).Returns(Provider(id));
            store.GetTokenAsync(id, Arg.Any<CancellationToken>()).Returns("saved-token");
            var checker = Substitute.For<ICustomProviderChecker>();
            checker.CheckAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new CustomProviderCheck(SavedUrl, true, true));

            var check = await new CheckCustomProvider(store, checker, Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context(), saved ? id : null, new CustomProviderCheckRequest(url, token));

            if (!allowed)
            {
                check.Status.Should().Be(CustomProviderStatus.Forbidden);
                checker.ReceivedCalls().Should().BeEmpty("a refused caller must not make Connapse call a server");
                return;
            }
            check.Status.Should().Be(CustomProviderStatus.Success);
            bool sentSaved = checker.ReceivedCalls().Any(c => Equals(c.GetArguments()[1], "saved-token"));
            if (sentSaved)
                checker.ReceivedCalls().Single().GetArguments()[0].Should().Be(SavedUrl, "the saved token goes to the saved address only");
            if (!saved || !string.IsNullOrEmpty(token))
                sentSaved.Should().BeFalse("a token was given, or there is no saved provider");
        }, iter: 300);

    [Fact]
    [RandomInputTestOf(typeof(CreateCustomProvider))]
    public Task CreateCustom_AnyRequest_SavesOnlyAfterAPassedCheck() =>
        Gen.Select(Gen.Bool, GenUrl, GenToken, Gen.Bool, Gen.OneOfConst<string?>(null, "", "Name", new string('n', 129))).SampleAsync(async t =>
        {
            var (allowed, url, token, passes, name) = t;
            var store = Substitute.For<ICustomProviderStore>();
            store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
            store.CreateAsync(Arg.Any<SaveCustomProviderRequest>(), Arg.Any<CancellationToken>())
                .Returns(c => Provider(Guid.NewGuid()) with { Name = c.Arg<SaveCustomProviderRequest>().Name });
            var checker = Substitute.For<ICustomProviderChecker>();
            checker.CheckAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(passes ? new CustomProviderCheck(SavedUrl, true, true) : new CustomProviderCheck(null, false, false, Error: "no", Field: "secret"));

            var created = await new CreateCustomProvider(store, checker, Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context(), new CreateCustomProviderRequest(url!, token!, name));

            bool saves = allowed && !string.IsNullOrWhiteSpace(token) && passes && name?.Length is not > 128;
            await store.Received(saves ? 1 : 0).CreateAsync(Arg.Any<SaveCustomProviderRequest>(), Arg.Any<CancellationToken>());
            created.Succeeded.Should().Be(saves);
            if (!allowed)
                checker.ReceivedCalls().Should().BeEmpty();
            else if (!saves)
                created.Field.Should().BeOneOf("token", "name");
        }, iter: 300);

    [Fact]
    [RandomInputTestOf(typeof(UpdateCustomProvider))]
    public Task UpdateCustom_AnyRequest_NeverSendsTheSavedTokenElsewhereOrMovesAProviderInUse() =>
        Gen.Select(Gen.Bool, GenUrl, GenToken, Gen.Bool).SampleAsync(async t =>
        {
            var (allowed, url, token, inUse) = t;
            var (store, connections) = (Substitute.For<ICustomProviderStore>(), Substitute.For<IConnectionStore>());
            Guid id = Guid.NewGuid();
            store.GetAsync(id, Arg.Any<CancellationToken>()).Returns(Provider(id));
            store.ListAsync(Arg.Any<CancellationToken>()).Returns([Provider(id)]);
            store.GetTokenAsync(id, Arg.Any<CancellationToken>()).Returns("saved-token");
            store.UpdateAsync(id, Arg.Any<SaveCustomProviderRequest>(), Arg.Any<CancellationToken>())
                .Returns(c => Provider(id) with { BaseUrl = c.Arg<SaveCustomProviderRequest>().BaseUrl });
            connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(inUse ? [Using(id, "uses it")] : []);
            var checker = Substitute.For<ICustomProviderChecker>();
            checker.CheckAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(c => new CustomProviderCheck(c.ArgAt<string?>(0) is { Length: > 0 } u && u.StartsWith("https") ? u : null, true, c.ArgAt<string?>(0) is { Length: > 0 }));

            var updated = await new UpdateCustomProvider(store, connections, checker, Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context(), id, new UpdateCustomProviderRequest(BaseUrl: url, Token: token));

            if (!allowed)
            {
                updated.Status.Should().Be(CustomProviderStatus.Forbidden);
                checker.ReceivedCalls().Should().BeEmpty();
                return;
            }
            foreach (var call in checker.ReceivedCalls().Where(c => Equals(c.GetArguments()[1], "saved-token")))
                call.GetArguments()[0].Should().Be(SavedUrl, "the saved token goes to the saved address only");
            if (updated.Succeeded && inUse)
                updated.Result!.BaseUrl.Should().Be(SavedUrl, "a provider in use keeps its address");
            if (!updated.Succeeded)
                await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<SaveCustomProviderRequest>(), Arg.Any<CancellationToken>());
            else
            {
                // The saved pair is the checked pair, even if another update replaced the token meanwhile.
                var checkedCall = checker.ReceivedCalls().Single().GetArguments();
                await store.Received(1).UpdateAsync(id, Arg.Is<SaveCustomProviderRequest>(s =>
                    s.Token == ((string?)checkedCall[1])!.Trim() && s.BaseUrl == (string?)checkedCall[0]), Arg.Any<CancellationToken>());
            }
        }, iter: 300);

    [Fact]
    [RandomInputTestOf(typeof(ReplaceCustomProviderLinkSecret))]
    public Task LinkSecretCustom_AnyCaller_GeneratesOnlyForSettingsAdminsAndKnownProviders() =>
        Gen.Select(Gen.Bool, Gen.Bool).SampleAsync(async t =>
        {
            var (allowed, exists) = t;
            var store = Substitute.For<ICustomProviderStore>();
            Guid id = Guid.NewGuid();
            store.GetAsync(id, Arg.Any<CancellationToken>()).Returns(exists ? Provider(id) : null);
            store.GenerateLinkSecretAsync(id, Arg.Any<CancellationToken>()).Returns(exists ? "secret" : null);

            var replaced = await new ReplaceCustomProviderLinkSecret(store, Substitute.For<IAuditLogger>(), Authorizer(allowed)).RunAsync(Context(), id);

            replaced.Status.Should().Be(!allowed ? CustomProviderStatus.Forbidden : exists ? CustomProviderStatus.Success : CustomProviderStatus.NotFound);
            if (!allowed)
                store.ReceivedCalls().Should().BeEmpty();
            if (replaced.Succeeded)
                replaced.Result!.LinkSecret.Should().Be("secret");
        }, iter: 100);

    [Fact]
    [RandomInputTestOf(typeof(DeleteCustomProvider))]
    public Task DeleteCustom_AnyCaller_NeverDeletesAProviderInUse() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Int[0, 2]).SampleAsync(async t =>
        {
            var (allowed, exists, uses) = t;
            var (store, connections) = (Substitute.For<ICustomProviderStore>(), Substitute.For<IConnectionStore>());
            Guid id = Guid.NewGuid();
            store.GetAsync(id, Arg.Any<CancellationToken>()).Returns(exists ? Provider(id) : null);
            store.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(exists);
            connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([.. Enumerable.Range(0, uses).Select(i => Using(id, $"c{i}"))]);

            var deleted = await new DeleteCustomProvider(store, connections, Substitute.For<IAuditLogger>(), Authorizer(allowed)).RunAsync(Context(), id);

            CustomProviderStatus expected = !allowed ? CustomProviderStatus.Forbidden
                : !exists ? CustomProviderStatus.NotFound
                : uses > 0 ? CustomProviderStatus.InUse
                : CustomProviderStatus.Success;
            deleted.Status.Should().Be(expected);
            await store.Received(expected == CustomProviderStatus.Success ? 1 : 0).DeleteAsync(id, Arg.Any<CancellationToken>());
        }, iter: 200);

    // ── AWS ────────────────────────────────────────────────────────────────────────────────────

    private static readonly RolesAnywhereConfig StoredAccess = new("cert",
        "arn:aws:rolesanywhere:us-east-1:111122223333:trust-anchor/ta1", "arn:aws:rolesanywhere:us-east-1:111122223333:profile/p1",
        "arn:aws:iam::111122223333:role/connapse", "us-east-1");

    private static readonly Gen<string> GenField = Gen.OneOfConst("", "  ", "value");

    private static readonly Gen<string> GenRegion = Gen.OneOfConst("", "us-east-1", "Not A Region", "eu-west-2");

    [Fact]
    [RandomInputTestOf(typeof(SetAwsAccess))]
    public Task SetAwsAccess_AnyRequest_SavesOnlyACompleteCredentialAwsAccepted() =>
        Gen.Select(Gen.Bool, Gen.Select(GenField, GenField, GenRegion), Gen.OneOfConst<string?>(null, "", "key"),
            Gen.Int[0, 2], Gen.Int[0, 2], Gen.Select(Gen.Bool, Gen.Bool)).SampleAsync(async t =>
        {
            var (allowed, (cert, arn, region), key, storedKind, expectedKind, (awsAccepts, raced)) = t;
            var credentials = Substitute.For<IProviderCredentialStore>();
            // 0: nothing stored, 1: the stored credential, 2: a stored key that can't be decrypted.
            if (storedKind == 2)
                credentials.GetRolesAnywhereMaterialAsync("aws", Arg.Any<CancellationToken>())
                    .Returns<RolesAnywhereCredentialMaterial?>(_ => throw new ProviderCredentialUnavailableException("aws", new InvalidOperationException()));
            else
                credentials.GetRolesAnywhereMaterialAsync("aws", Arg.Any<CancellationToken>())
                    .Returns(storedKind == 1 ? new RolesAnywhereCredentialMaterial(StoredAccess, "stored-key") : null);
            RolesAnywhereConfig? current = storedKind == 0 ? null : StoredAccess;
            credentials.GetRolesAnywhereAsync("aws", Arg.Any<CancellationToken>()).Returns(current);
            // Raced: someone else saved a credential while AWS was checking this one.
            credentials.ReplaceRolesAnywhereAsync(default!, default, default!, default!, default, default, default)
                .ReturnsForAnyArgs(raced ? null : new ProviderCredentialInfo("aws", null, DateTime.UtcNow));
            var validator = Substitute.For<IRolesAnywhereSetupValidator>();
            validator.ValidateAsync(Arg.Any<RolesAnywhereConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new RolesAnywhereValidationResult(awsAccepts, awsAccepts ? null : "denied"));
            var session = Substitute.For<IAwsSession>();
            // 0: none, 1: what is stored, 2: an older credential.
            RolesAnywhereConfig? expected = expectedKind switch { 1 => StoredAccess, 2 => StoredAccess with { RoleArn = "arn:old" }, _ => null };

            var saved = await new SetAwsAccess(credentials, validator, session, Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context(), new AwsAccessRequest(cert, arn, arn, arn, region, key, expected));

            bool complete = cert.Trim().Length > 0 && arn.Trim().Length > 0 && region.Length > 0;
            bool keeps = string.IsNullOrWhiteSpace(key);
            AwsSetupStatus status = !allowed ? AwsSetupStatus.Forbidden
                : !complete || region == "Not A Region" ? AwsSetupStatus.Invalid
                : keeps && storedKind != 1 ? AwsSetupStatus.Invalid
                : expected is not null && expected != current ? AwsSetupStatus.Changed
                : !awsAccepts ? AwsSetupStatus.Refused
                : raced ? AwsSetupStatus.Changed
                : AwsSetupStatus.Success;
            saved.Status.Should().Be(status);
            bool attempted = status == AwsSetupStatus.Success || (raced && status == AwsSetupStatus.Changed && awsAccepts
                && (expected is null || expected == current));
            await credentials.Received(attempted ? 1 : 0).ReplaceRolesAnywhereAsync("aws", Arg.Is(current), Arg.Any<RolesAnywhereConfig>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
            await credentials.DidNotReceiveWithAnyArgs().SaveRolesAnywhereAsync(default!, default!, default!, default, default, default);
            session.Received(status == AwsSetupStatus.Success ? 1 : 0).Clear();
            if (status == AwsSetupStatus.Success)
                await validator.Received(1).ValidateAsync(Arg.Any<RolesAnywhereConfig>(), keeps ? "stored-key" : "key", Arg.Any<CancellationToken>());
            if (!allowed)
                credentials.ReceivedCalls().Should().BeEmpty();
        }, iter: 400);

    [Fact]
    [RandomInputTestOf(typeof(GetAwsAccess))]
    public Task GetAwsAccess_AnyCaller_ReadsTheCredentialWithoutItsKeyAndOnlyForSettingsAdmins() =>
        Gen.Select(Gen.Bool, Gen.Bool).SampleAsync(async t =>
        {
            var (allowed, stored) = t;
            var credentials = Substitute.For<IProviderCredentialStore>();
            credentials.GetRolesAnywhereAsync("aws", Arg.Any<CancellationToken>()).Returns(stored ? StoredAccess : null);
            var saved = new ProviderCredentialStatus(DateTime.UtcNow, null);
            credentials.GetStatusAsync("aws", Arg.Any<CancellationToken>()).Returns(saved);

            var read = await new GetAwsAccess(credentials, Authorizer(allowed)).RunAsync(Context());

            read.Status.Should().Be(allowed ? AwsSetupStatus.Success : AwsSetupStatus.Forbidden);
            if (allowed)
                read.Result.Should().Be(new AwsAccessView(stored ? StoredAccess : null, stored ? saved : null));
            else
                credentials.ReceivedCalls().Should().BeEmpty();
            await credentials.DidNotReceiveWithAnyArgs().GetRolesAnywhereMaterialAsync(default!, default);
        }, iter: 50);

    [Fact]
    [RandomInputTestOf(typeof(ResetAwsAccess))]
    public Task ResetAwsAccess_AnyCaller_RemovesOnlyTheCredentialItReadAndOnlyForSettingsAdmins() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Bool).SampleAsync(async t =>
        {
            var (allowed, stored, raced) = t;
            var credentials = Substitute.For<IProviderCredentialStore>();
            RolesAnywhereConfig? read = stored ? StoredAccess : null;
            credentials.GetRolesAnywhereAsync("aws", Arg.Any<CancellationToken>()).Returns(read);
            // Raced: someone else saved a credential between the read and the delete, so nothing matches what was read.
            credentials.DeleteRolesAnywhereAsync("aws", Arg.Any<RolesAnywhereConfig?>(), Arg.Any<CancellationToken>()).Returns(stored && !raced);
            var session = Substitute.For<IAwsSession>();

            var reset = await new ResetAwsAccess(credentials, session, Substitute.For<IAuditLogger>(), Authorizer(allowed)).RunAsync(Context());

            AwsSetupStatus status = !allowed ? AwsSetupStatus.Forbidden : stored && raced ? AwsSetupStatus.Changed : AwsSetupStatus.Success;
            reset.Status.Should().Be(status);
            await credentials.Received(allowed ? 1 : 0).DeleteRolesAnywhereAsync("aws", Arg.Is(read), Arg.Any<CancellationToken>());
            await credentials.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
            session.Received(status == AwsSetupStatus.Success ? 1 : 0).Clear();
            if (status == AwsSetupStatus.Success)
                (reset.Result!.CleanupScript is not null).Should().Be(stored, "the cleanup command names the removed credential's ARNs");
        }, iter: 100);

    [Fact]
    [RandomInputTestOf(typeof(SetIdentityCenter))]
    public Task SetIdentityCenter_AnyRequest_SavesOnlyACompleteInstance() =>
        Gen.Select(Gen.Bool, GenRegion, GenField, GenField, Gen.Bool).SampleAsync(async t =>
        {
            var (allowed, region, arn, storeId, reloads) = t;
            var (store, reloader) = (Substitute.For<ISettingsStore>(), Substitute.For<ISettingsReloader>());
            reloader.Reload().Returns(reloads);

            var saved = await new SetIdentityCenter(store, reloader, Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context(), new IdentityCenterRequest(region, arn, storeId));

            bool valid = region is "us-east-1" or "eu-west-2" && arn.Trim().Length > 0 && storeId.Trim().Length > 0;
            saved.Status.Should().Be(!allowed ? AwsSetupStatus.Forbidden : valid ? AwsSetupStatus.Success : AwsSetupStatus.Invalid);
            await store.Received(saved.Succeeded ? 1 : 0).SaveAsync("identitycenter", Arg.Any<IdentityCenterSettings>(), Arg.Any<CancellationToken>());
            if (saved.Succeeded)
                saved.Result!.Applied.Should().Be(reloads);
        }, iter: 300);

    [Fact]
    [RandomInputTestOf(typeof(ResetIdentityCenter))]
    public Task ResetIdentityCenter_AnyCaller_StoresBlanksOnlyForSettingsAdmins() =>
        Gen.Bool.SampleAsync(async allowed =>
        {
            var store = Substitute.For<ISettingsStore>();

            var reset = await new ResetIdentityCenter(store, Substitute.For<ISettingsReloader>(), Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context());

            reset.Status.Should().Be(allowed ? AwsSetupStatus.Success : AwsSetupStatus.Forbidden);
            await store.Received(allowed ? 1 : 0).SaveAsync("identitycenter",
                Arg.Is<IdentityCenterSettings>(s => s.Region == "" && s.InstanceArn == "" && s.IdentityStoreId == ""), Arg.Any<CancellationToken>());
        }, iter: 50);

    private sealed class Enforcement : IConfiguredSettings
    {
        public PermissionEnforcementSettings Value = new();

        public T Current<T>() where T : class, new() => Value as T ?? new T();
    }

    [Fact]
    [RandomInputTestOf(typeof(SetAwsPermissions))]
    public Task SetAwsPermissions_AnyRequest_NeverSavesSignInWithoutEnforcement() =>
        Gen.Select(Gen.Bool, Gen.Select(GenField, GenField, GenField), Gen.OneOfConst("", "https://c.example/acs", "ftp://c", "nope"),
            Gen.OneOfConst("", "https://idp.example/sso", "nope"), Gen.Bool, Gen.Bool).SampleAsync(async t =>
        {
            var (allowed, (entity, idpEntity, cert), acs, sso, enforcing, latchTakes) = t;
            var store = Substitute.For<ISettingsStore>();
            var reloader = Substitute.For<ISettingsReloader>();
            var configured = new Enforcement { Value = new() { IsEnforcing = enforcing } };
            reloader.Reload().Returns(_ =>
            {
                if (latchTakes) configured.Value = new() { IsEnforcing = true };
                return true;
            });

            var saved = await new SetAwsPermissions(store, reloader, configured, Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context(), new AwsPermissionsRequest(entity, acs, idpEntity, sso, cert));

            bool addresses = acs is "" or "https://c.example/acs" && sso is "" or "https://idp.example/sso";
            bool complete = new[] { entity, idpEntity, cert, acs, sso }.All(v => v.Trim().Length > 0);
            AwsSetupStatus status = !allowed ? AwsSetupStatus.Forbidden
                : !addresses ? AwsSetupStatus.Invalid
                : complete && !enforcing && !latchTakes ? AwsSetupStatus.NotEnforcing
                : AwsSetupStatus.Success;
            saved.Status.Should().Be(status);
            await store.Received(status == AwsSetupStatus.Success ? 1 : 0).SaveAsync("samlsignin", Arg.Any<SamlSignInSettings>(), Arg.Any<CancellationToken>());
            if (status == AwsSetupStatus.Success && complete)
                configured.Value.IsEnforcing.Should().BeTrue("sign-in is never saved while this process shows every user every AWS document");
        }, iter: 400);

    [Fact]
    [RandomInputTestOf(typeof(ResetAwsPermissions))]
    public Task ResetAwsPermissions_AnyCaller_ClearsSignInButNeverEnforcement() =>
        Gen.Bool.SampleAsync(async allowed =>
        {
            var store = Substitute.For<ISettingsStore>();

            var reset = await new ResetAwsPermissions(store, Substitute.For<ISettingsReloader>(), Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context());

            reset.Status.Should().Be(allowed ? AwsSetupStatus.Success : AwsSetupStatus.Forbidden);
            await store.Received(allowed ? 1 : 0).SaveAsync("samlsignin", Arg.Is<SamlSignInSettings>(s => !s.IsConfigured), Arg.Any<CancellationToken>());
            await store.DidNotReceiveWithAnyArgs().UpdateAsync<PermissionEnforcementSettings>(default!, default!, default);
        }, iter: 50);

    private static IAzureBlobDiscovery Entra(bool accepts)
    {
        var discovery = Substitute.For<IAzureBlobDiscovery>();
        discovery.CheckAccessAsync(Arg.Any<AzureProviderSettings>(), Arg.Any<CancellationToken>())
            .Returns(accepts ? AzureProbe<string>.Ok("token") : AzureProbe<string>.Denied("no"));
        return discovery;
    }

    [Fact]
    [RandomInputTestOf(typeof(SetAzureAccess))]
    public Task SetAzureAccess_AnyRequest_SavesOnlyATenantEntraAccepted() =>
        Gen.Select(Gen.Bool, Gen.OneOfConst("", "  ", "t1", "t2"), Gen.Bool, Gen.Bool, Gen.Bool, Gen.Bool).SampleAsync(async t =>
        {
            var (allowed, tenant, hostManaged, onAzure, accepts, reloads) = t;
            var (store, reloader) = (Substitute.For<ISettingsStore>(), Substitute.For<ISettingsReloader>());
            reloader.Reload().Returns(reloads);
            var discovery = Entra(accepts);
            var host = Substitute.For<IAzureHostIdentity>();
            host.DetectAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(onAzure ? new AzureHostIdentityInfo("t1", "p", "c", null, null, false) : null);

            var saved = await new SetAzureAccess(store, reloader, new Enforcement(), discovery, host, Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context(), hostManaged
                    ? new AzureAccessRequest(tenant, UseHostManagedIdentity: true)
                    : new AzureAccessRequest(tenant, ClientId: "app", ClientCertificatePath: "c.pem", ClientCertificatePassword: "pw"));

            AzureSetupStatus status = !allowed ? AzureSetupStatus.Forbidden
                : tenant.Trim().Length == 0 ? AzureSetupStatus.Invalid
                : hostManaged && onAzure && tenant != "t1" ? AzureSetupStatus.Invalid
                : !accepts ? AzureSetupStatus.Refused
                : AzureSetupStatus.Success;
            saved.Status.Should().Be(status);
            await discovery.Received(status is AzureSetupStatus.Success or AzureSetupStatus.Refused ? 1 : 0)
                .CheckAccessAsync(Arg.Any<AzureProviderSettings>(), Arg.Any<CancellationToken>());
            await store.Received(saved.Succeeded ? 1 : 0).SaveAsync("azure", Arg.Any<AzureProviderSettings>(), Arg.Any<CancellationToken>());
            if (saved.Succeeded)
            {
                saved.Result!.Applied.Should().Be(reloads);
                saved.Result.Settings.ClientCertificatePassword.Should().BeNull("the password is never handed back");
            }
        }, iter: 400);

    [Fact]
    [RandomInputTestOf(typeof(ResetAzureAccess))]
    public Task ResetAzureAccess_AnyCaller_StoresBlanksOnlyForSettingsAdmins() =>
        Gen.Bool.SampleAsync(async allowed =>
        {
            var store = Substitute.For<ISettingsStore>();

            var reset = await new ResetAzureAccess(store, Substitute.For<ISettingsReloader>(), Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context());

            reset.Status.Should().Be(allowed ? AzureSetupStatus.Success : AzureSetupStatus.Forbidden);
            await store.Received(allowed ? 1 : 0).SaveAsync("azure", Arg.Is<AzureProviderSettings>(s => s.TenantId == null), Arg.Any<CancellationToken>());
        }, iter: 50);

    [Fact]
    [RandomInputTestOf(typeof(SetAzurePermissions))]
    public Task SetAzurePermissions_AnyRequest_NeverSavesSignInWithoutEnforcement() =>
        Gen.Select(Gen.Bool, Gen.Select(GenField, GenField, GenField),
            Gen.OneOfConst("", "https://c.example/api/v1/auth/cloud/azure/callback", "ftp://c", "nope"),
            Gen.Bool, Gen.Bool, Gen.Bool).SampleAsync(async t =>
        {
            var (allowed, (tenant, client, cert), redirect, accepts, enforcing, latchTakes) = t;
            var store = Substitute.For<ISettingsStore>();
            var reloader = Substitute.For<ISettingsReloader>();
            var configured = new Enforcement { Value = new() { AzureEnforcing = enforcing } };
            reloader.Reload().Returns(_ =>
            {
                if (latchTakes) configured.Value = new() { AzureEnforcing = true };
                return true;
            });
            var discovery = Entra(accepts);

            var saved = await new SetAzurePermissions(store, reloader, configured, discovery, Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context(), new AzurePermissionsRequest(tenant, client, redirect, cert));

            bool address = redirect is "" or "https://c.example/api/v1/auth/cloud/azure/callback";
            bool complete = new[] { tenant, client, cert, redirect }.All(v => v.Trim().Length > 0);
            AzureSetupStatus status = !allowed ? AzureSetupStatus.Forbidden
                : !address ? AzureSetupStatus.Invalid
                : complete && !accepts ? AzureSetupStatus.Refused
                : complete && !enforcing && !latchTakes ? AzureSetupStatus.NotEnforcing
                : AzureSetupStatus.Success;
            saved.Status.Should().Be(status);
            await store.Received(status == AzureSetupStatus.Success ? 1 : 0).SaveAsync("azuread", Arg.Any<AzureAdSignInSettings>(), Arg.Any<CancellationToken>());
            if (status == AzureSetupStatus.Success && complete)
                configured.Value.AzureEnforcing.Should().BeTrue("sign-in is never saved while this process shows every user every Azure document");
        }, iter: 400);

    private sealed class AzureAccessApp : IConfiguredSettings
    {
        public T Current<T>() where T : class, new() =>
            new AzureProviderSettings { TenantId = "t", ClientId = "app" } as T
            ?? new PermissionEnforcementSettings { AzureEnforcing = true } as T
            ?? new T();
    }

    [Theory]
    [InlineData(true, "https://login.microsoftonline.com/t/adminconsent?client_id=app&redirect_uri=https%3A%2F%2Fh.example%2Fadmin%2Fproviders%2Fazure")]
    [InlineData(false, null)]
    public async Task SetAzurePermissions_ConsentPending_AnswersWithTheConsentLinkBackToTheProvidersPage(bool pending, string? link)
    {
        var saved = await new SetAzurePermissions(Substitute.For<ISettingsStore>(), Substitute.For<ISettingsReloader>(), new AzureAccessApp(),
                Entra(accepts: true), Substitute.For<IAuditLogger>(), Authorizer(true))
            .RunAsync(Context(), new AzurePermissionsRequest("t", "signin", "https://h.example/api/v1/auth/cloud/azure/callback", "/c.pfx",
                AdminConsentPending: pending));

        saved.Status.Should().Be(AzureSetupStatus.Success);
        saved.Result!.AdminConsentUrl.Should().Be(link);
    }

    [Fact]
    [RandomInputTestOf(typeof(ResetAzurePermissions))]
    public Task ResetAzurePermissions_AnyCaller_ClearsSignInButNeverEnforcement() =>
        Gen.Bool.SampleAsync(async allowed =>
        {
            var store = Substitute.For<ISettingsStore>();

            var reset = await new ResetAzurePermissions(store, Substitute.For<ISettingsReloader>(), Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context());

            reset.Status.Should().Be(allowed ? AzureSetupStatus.Success : AzureSetupStatus.Forbidden);
            await store.Received(allowed ? 1 : 0).SaveAsync("azuread", Arg.Is<AzureAdSignInSettings>(s => !s.IsConfigured), Arg.Any<CancellationToken>());
            await store.DidNotReceiveWithAnyArgs().UpdateAsync<PermissionEnforcementSettings>(default!, default!, default);
        }, iter: 50);

    private static readonly string GitHubKey = System.Security.Cryptography.RSA.Create(2048).ExportRSAPrivateKeyPem();

    private static readonly GitHubAppRegistration StoredGitHubApp = new(42, "stored", "Iv1.client", "octo", "https://github.com/apps/stored");

    [Fact]
    [RandomInputTestOf(typeof(GetGitHubApp))]
    public Task GetGitHubApp_AnyCaller_ReadsTheAppWithoutItsSecretsAndOnlyForSettingsAdmins() =>
        Gen.Select(Gen.Bool, Gen.Bool).SampleAsync(async t =>
        {
            var (allowed, stored) = t;
            var credentials = Substitute.For<IProviderCredentialStore>();
            credentials.GetGitHubAppAsync(Arg.Any<CancellationToken>()).Returns(stored ? StoredGitHubApp : null);
            var saved = new ProviderCredentialStatus(DateTime.UtcNow, DateTime.UtcNow);
            credentials.GetStatusAsync("github", Arg.Any<CancellationToken>()).Returns(saved);

            var read = await new GetGitHubApp(credentials, Authorizer(allowed)).RunAsync(Context());

            read.Status.Should().Be(allowed ? GitHubAppStatus.Success : GitHubAppStatus.Forbidden);
            if (allowed)
                read.Result.Should().Be(new GitHubAppView(stored ? StoredGitHubApp : null, stored ? saved : null));
            else
                credentials.ReceivedCalls().Should().BeEmpty();
            await credentials.DidNotReceiveWithAnyArgs().GetGitHubAppMaterialAsync(default);
        }, iter: 50);

    [Fact]
    [RandomInputTestOf(typeof(SetGitHubApp))]
    public Task SetGitHubApp_AnyRequest_StoresOnlyValuesGitHubAccepted() =>
        Gen.Select(Gen.Bool, Gen.OneOfConst<long?>(null, 0, -1, 42, 7), Gen.OneOfConst<string?>(null, " ", GitHubKey),
            Gen.OneOfConst<string?>(null, "", "secret"), Gen.Bool, Gen.Bool, Gen.Bool, Gen.Bool).SampleAsync(async t =>
        {
            var (allowed, appId, key, secret, replace, hasStored, keyAccepted, secretAccepted) = t;
            var credentials = Substitute.For<IProviderCredentialStore>();
            credentials.GetGitHubAppMaterialAsync(Arg.Any<CancellationToken>())
                .Returns(hasStored ? new GitHubAppCredentialMaterial(StoredGitHubApp, GitHubKey, "old") : null);

            var saved = await new SetGitHubApp(credentials, GitHub(credentials, keyAccepted, secretAccepted), Substitute.For<IAuditLogger>(), Authorizer(allowed))
                .RunAsync(Context(), new GitHubAppRequest(appId, key, secret, replace));

            bool sameApp = hasStored && appId == 42;
            GitHubAppStatus status = !allowed ? GitHubAppStatus.Forbidden
                : appId is not > 0 ? GitHubAppStatus.Invalid
                : key != GitHubKey && !sameApp ? GitHubAppStatus.Invalid
                : hasStored && !sameApp && !replace ? GitHubAppStatus.ReplaceUnconfirmed
                : !keyAccepted || (secret == "secret" && !secretAccepted) ? GitHubAppStatus.Refused
                : GitHubAppStatus.Success;
            saved.Status.Should().Be(status);
            await credentials.Received(saved.Succeeded ? 1 : 0).SaveGitHubAppAsync(
                Arg.Any<GitHubAppRegistration>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
            if (saved.Succeeded)
                saved.Result!.CanSignIn.Should().Be(secret == "secret" || sameApp);
        }, iter: 400);

    private static ConnapseGitHubApp GitHub(IProviderCredentialStore credentials, bool keyAccepted, bool secretAccepted)
    {
        var services = new ServiceCollection().AddSingleton(credentials).BuildServiceProvider();
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new GitHubStub(keyAccepted, secretAccepted)));
        return new ConnapseGitHubApp(services.GetRequiredService<IServiceScopeFactory>(), http, NullLogger<ConnapseGitHubApp>.Instance)
        { ApiBaseUrl = "https://api.github.test" };
    }

    [Fact]
    [RandomInputTestOf(typeof(RemoveGitHubApp))]
    public Task RemoveGitHubApp_AnyCaller_DeletesOnlyForSettingsAdmins() =>
        Gen.Select(Gen.Bool, Gen.Bool).SampleAsync(async t =>
        {
            var (allowed, hasStored) = t;
            var credentials = Substitute.For<IProviderCredentialStore>();
            credentials.DeleteAsync("github", Arg.Any<CancellationToken>()).Returns(hasStored);
            var audit = Substitute.For<IAuditLogger>();

            var removed = await new RemoveGitHubApp(credentials, GitHub(credentials, true, true), audit, Authorizer(allowed)).RunAsync(Context());

            removed.Status.Should().Be(allowed ? GitHubAppStatus.Success : GitHubAppStatus.Forbidden);
            await credentials.Received(allowed ? 1 : 0).DeleteAsync("github", Arg.Any<CancellationToken>());
            if (allowed)
                removed.Result!.Removed.Should().Be(hasStored);
            await audit.Received(allowed && hasStored ? 1 : 0).LogAsync(
                "provider.credential.deleted", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
        }, iter: 50);

    [Fact]
    [RandomInputTestOf(typeof(StartGitHubAppCreation))]
    public Task StartGitHubAppCreation_AnyOwner_StartsOnlyForAPersonAndARealOrganisation() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.OneOfConst<string?>(null, " ", "octo-org", "a-person", "nobody", "not a name!"), Gen.Bool, Gen.Bool, Gen.Bool)
            .SampleAsync(async t =>
        {
            var (allowed, person, organisation, gitHubUp, hasStored, replace) = t;
            var credentials = Substitute.For<IProviderCredentialStore>();
            credentials.GetGitHubAppAsync(Arg.Any<CancellationToken>()).Returns(hasStored ? StoredGitHubApp : null);

            var started = await new StartGitHubAppCreation(Requests(), GitHub(credentials, gitHubUp, true), credentials, Authorizer(allowed))
                .RunAsync(person ? Person() : Context(), "https://h.example/", new GitHubAppCreationRequest(organisation, replace));

            bool own = string.IsNullOrWhiteSpace(organisation);
            GitHubAppStatus status = !allowed || !person ? GitHubAppStatus.Forbidden
                : !own && organisation == "not a name!" ? GitHubAppStatus.Invalid
                : !own && !gitHubUp ? GitHubAppStatus.Unavailable
                : !own && organisation != "octo-org" ? GitHubAppStatus.Invalid
                : hasStored && !replace ? GitHubAppStatus.ReplaceUnconfirmed
                : GitHubAppStatus.Success;
            started.Status.Should().Be(status);
            if (started.Succeeded)
            {
                started.Result!.FormAction.Should().StartWith(own ? "https://github.com/settings/apps/new" : "https://github.com/organizations/octo-org/");
                started.Result.SetupPageUrl.Should().Be("https://h.example/admin/providers/github");
            }
        }, iter: 300);

    [Fact]
    [RandomInputTestOf(typeof(CompleteGitHubAppCreation))]
    public Task CompleteGitHubAppCreation_AnyRedirect_StoresOnlyThePersonsOwnLiveCreation() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.OneOfConst("mine", "theirs", "made-up", ""), Gen.OneOfConst<string?>(null, " ", "code"), Gen.Bool,
                Gen.OneOfConst<long?>(null, 42), Gen.OneOfConst<long?>(null, 42, 7))
            .SampleAsync(async t =>
        {
            var (allowed, person, whose, code, gitHubUp, replacing, storedNow) = t;
            var credentials = Substitute.For<IProviderCredentialStore>();
            credentials.GetGitHubAppAsync(Arg.Any<CancellationToken>()).Returns(storedNow is { } id ? StoredGitHubApp with { AppId = id } : null);
            var requests = Requests();
            string state = whose switch
            {
                "mine" => requests.Start(PersonId, replacing),
                "theirs" => requests.Start(Guid.NewGuid(), replacing),
                _ => whose,
            };

            var completed = await new CompleteGitHubAppCreation(requests, GitHub(credentials, gitHubUp, true), credentials,
                    Substitute.For<IAuditLogger>(), Authorizer(allowed), NullLogger<CompleteGitHubAppCreation>.Instance)
                .RunAsync(person ? Person() : Context(), code, state);

            GitHubAppStatus status = !allowed || !person ? GitHubAppStatus.Forbidden
                : whose != "mine" ? GitHubAppStatus.Expired
                : code != "code" ? GitHubAppStatus.NoCode
                : storedNow is not null && storedNow != replacing ? GitHubAppStatus.ReplaceUnconfirmed
                : !gitHubUp ? GitHubAppStatus.Unavailable
                : GitHubAppStatus.Success;
            completed.Status.Should().Be(status);
            await credentials.Received(completed.Succeeded ? 1 : 0).SaveGitHubAppAsync(
                Arg.Any<GitHubAppRegistration>(), Arg.Any<string>(), Arg.Any<string?>(), PersonId, Arg.Any<CancellationToken>());
        }, iter: 300);

    [Fact]
    public async Task SetGitHubApp_StoredAppChangesWhileGitHubChecks_SavesNothing()
    {
        var credentials = Substitute.For<IProviderCredentialStore>();
        credentials.GetGitHubAppMaterialAsync(Arg.Any<CancellationToken>()).Returns(
            new GitHubAppCredentialMaterial(StoredGitHubApp, GitHubKey, "old"),
            new GitHubAppCredentialMaterial(StoredGitHubApp, GitHubKey, "rotated meanwhile"));

        var saved = await new SetGitHubApp(credentials, GitHub(credentials, true, true), Substitute.For<IAuditLogger>(), Authorizer(true))
            .RunAsync(Context(), new GitHubAppRequest(42, ClientSecret: "secret"));

        saved.Status.Should().Be(GitHubAppStatus.ReplaceUnconfirmed);
        await credentials.DidNotReceiveWithAnyArgs().SaveGitHubAppAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task CompleteGitHubAppCreation_AuditFailsAfterStoring_StillReportsTheAppCreated()
    {
        var credentials = Substitute.For<IProviderCredentialStore>();
        var audit = Substitute.For<IAuditLogger>();
        audit.LogAsync(default!).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("audit down")));
        var requests = Requests();

        var completed = await new CompleteGitHubAppCreation(requests, GitHub(credentials, true, true), credentials, audit, Authorizer(true),
                NullLogger<CompleteGitHubAppCreation>.Instance)
            .RunAsync(Person(), "code", requests.Start(PersonId));

        completed.Status.Should().Be(GitHubAppStatus.Success, "the App is stored and GitHub's code is spent, so starting again would be wrong");
    }

    [Fact]
    [RandomInputTestOf(typeof(RecordGitHubAppInstalled))]
    public Task RecordGitHubAppInstalled_AnyRedirect_AlwaysLandsOnConnections() =>
        Gen.Select(Gen.Bool, Gen.OneOfConst<long?>(null, 0, -3, 77), Gen.OneOfConst<string?>(null, "install", "update", "request", "<script>"))
            .SampleAsync(async t =>
        {
            var (allowed, installationId, setupAction) = t;

            var recorded = await new RecordGitHubAppInstalled(Substitute.For<IAuditLogger>(), Authorizer(allowed, Policies.Admin))
                .RunAsync(Context(), installationId, setupAction);

            recorded.Status.Should().Be(allowed ? GitHubAppStatus.Success : GitHubAppStatus.Forbidden);
            if (allowed)
                recorded.Result.Should().StartWith("/connections?").And.NotContain("<");
        }, iter: 100);

    private static readonly Guid PersonId = Guid.NewGuid();

    private static OperationContext Person() =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, PersonId.ToString())], "test")), OperationSurface.Rest);

    private static GitHubManifestRequests Requests() => new(new MemoryCache(new MemoryCacheOptions()));

    private sealed class GitHubStub(bool keyAccepted, bool secretAccepted) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/applications/"))
                // GitHub's token check: 404 for an unknown token when the client id and secret are right.
                return Task.FromResult(new HttpResponseMessage(secretAccepted ? HttpStatusCode.NotFound : HttpStatusCode.Unauthorized));

            // keyAccepted doubles as "GitHub answers" for the calls that take no key.
            if (!keyAccepted)
                return Task.FromResult(new HttpResponseMessage(path.StartsWith("/app") && !path.StartsWith("/app-manifests/")
                    ? HttpStatusCode.Unauthorized
                    : HttpStatusCode.BadGateway)
                { Content = new StringContent("""{"message":"Bad credentials"}""") });

            string? body = path switch
            {
                "/users/octo-org" => """{"login":"octo-org","type":"Organization"}""",
                "/users/a-person" => """{"login":"a-person","type":"User"}""",
                _ when path.StartsWith("/users/") => null,
                _ => """{"id":42,"slug":"app","client_id":"Iv1.client","client_secret":"s","pem":"key","owner":{"login":"octo"},"html_url":"https://github.com/apps/app"}""",
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static OperationContext Context() => new(new ClaimsPrincipal(), OperationSurface.Rest);

    private static IOperationAuthorizer Authorizer(bool allowed, string policy = Policies.SettingsAdmin)
    {
        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(false);
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), policy).Returns(allowed);
        return authorizer;
    }

    private static CustomProvider Provider(Guid id) =>
        new(id, "Saved", SavedUrl, true, 100, false, null, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);

    private static Connection Using(Guid providerId, string name) =>
        new(Guid.NewGuid(), name, ConnectionProvider.Custom, CustomConnectionConfig.ToJson(providerId), null, DateTime.UtcNow, DateTime.UtcNow);
}
