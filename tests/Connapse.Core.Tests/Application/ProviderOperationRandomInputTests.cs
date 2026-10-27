using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Providers;
using Connapse.Core.Interfaces;
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
