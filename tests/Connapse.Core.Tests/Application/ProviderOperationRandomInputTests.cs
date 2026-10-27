using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Providers;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope.RolesAnywhere;
using Connapse.Storage.Settings;
using CsCheck;
using FluentAssertions;
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

    private static OperationContext Context() => new(new ClaimsPrincipal(), OperationSurface.Rest);

    private static IOperationAuthorizer Authorizer(bool allowed)
    {
        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(false);
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.SettingsAdmin).Returns(allowed);
        return authorizer;
    }

    private static CustomProvider Provider(Guid id) =>
        new(id, "Saved", SavedUrl, true, 100, false, null, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);

    private static Connection Using(Guid providerId, string name) =>
        new(Guid.NewGuid(), name, ConnectionProvider.Custom, CustomConnectionConfig.ToJson(providerId), null, DateTime.UtcNow, DateTime.UtcNow);
}
