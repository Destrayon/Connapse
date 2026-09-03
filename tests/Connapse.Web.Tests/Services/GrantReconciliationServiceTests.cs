using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Connapse.Web.Tests.Services;

[Trait("Category", "Unit")]
public class GrantReconciliationServiceTests
{
    private static readonly DateTime When = new(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);

    private static Connection S3(string? configJson) =>
        new(Guid.NewGuid(), "s3-conn", ConnectionProvider.S3, configJson, null, When, When);

    private static AccessGrantDetail GroupGrant(string scope, string id, string grp = "grp-1") =>
        new(AccessGrantId: id, AccessGrantArn: "arn:" + id,
            Grantee: new AccessGrantee(IsGroup: true, Id: grp),
            GrantScope: scope, Permission: "READ", AccessGrantsLocationId: "default");

    private sealed record Harness(
        GrantReconciliationService Service,
        IConnectionStore Connections,
        IAwsGrantRegions Regions,
        IAccessGrantsReader Reader,
        IAccessGrantsWriter Writer);

    private static Harness Build(
        int maxDeletePerTick = 50, string groupId = "grp-1")
    {
        var connections = Substitute.For<IConnectionStore>();
        var regions = Substitute.For<IAwsGrantRegions>();
        var reader = Substitute.For<IAccessGrantsReader>();
        var writer = Substitute.For<IAccessGrantsWriter>();

        regions.ListAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<string>>(_ => ["us-east-1"]);

        var service = new GrantReconciliationService(
            connections, regions, reader, writer,
            Options.Create(new SamlSignInSettings { GrantGroupId = groupId }).AsMonitor(),
            Options.Create(new GrantReconciliationSettings { MaxDeletePerTick = maxDeletePerTick }).AsMonitor(),
            NullLogger<GrantReconciliationService>.Instance);

        return new Harness(service, connections, regions, reader, writer);
    }

    [Fact]
    public async Task Reconcile_ConnectionListThrows_AbortsWithoutDeleting()
    {
        var h = Build();
        h.Connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));

        var report = await h.Service.ReconcileAsync(enforce: true);

        report.Aborted.Should().NotBeEmpty();
        report.Deleted.Should().Be(0);
        await h.Writer.DidNotReceive().RevokeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconcile_UnrestrictedConnection_AbortsWithoutDeleting()
    {
        // A connection that declares no allowed-locations could index any bucket, so no grant is
        // provably orphaned -> fail closed.
        var h = Build();
        h.Connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Connection>>(_ => [S3("{\"region\":\"us-east-1\"}")]);

        var report = await h.Service.ReconcileAsync(enforce: true);

        report.Aborted.Should().NotBeEmpty();
        await h.Writer.DidNotReceive().RevokeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconcile_MalformedConnectionConfig_AbortsWithoutDeleting()
    {
        var h = Build();
        h.Connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Connection>>(_ => [S3("not json{")]);

        var report = await h.Service.ReconcileAsync(enforce: true);

        report.Aborted.Should().NotBeEmpty();
        await h.Writer.DidNotReceive().RevokeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconcile_ImplausiblyManyCandidates_TripsCircuitBreaker()
    {
        var h = Build(maxDeletePerTick: 1);
        h.Connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Connection>>(_ => [S3("{\"allowedLocations\":[\"kept-bucket\"]}")]);
        h.Reader.ListAllAsync("us-east-1", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AccessGrantDetail>>(_ =>
                [GroupGrant("s3://gone-a/*", "g1"), GroupGrant("s3://gone-b/*", "g2")]);

        var report = await h.Service.ReconcileAsync(enforce: true);

        report.Aborted.Should().NotBeEmpty();
        report.Deleted.Should().Be(0);
        // The breaker fires before provenance is even read.
        await h.Writer.DidNotReceive().FilterManagedAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await h.Writer.DidNotReceive().RevokeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconcile_TaggedOrphan_IsDeleted()
    {
        var h = Build();
        h.Connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Connection>>(_ => [S3("{\"allowedLocations\":[\"kept-bucket\"]}")]);
        h.Reader.ListAllAsync("us-east-1", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AccessGrantDetail>>(_ => [GroupGrant("s3://gone-bucket/*", "g1")]);
        h.Writer.FilterManagedAsync("us-east-1", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(_ => ["arn:g1"]);
        h.Writer.RevokeAsync("us-east-1", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new GrantRevokeResult(["g1"], [], [], AccessDenied: false));

        var report = await h.Service.ReconcileAsync(enforce: true);

        report.Deleted.Should().Be(1);
        await h.Writer.Received().RevokeAsync(
            "us-east-1",
            Arg.Is<IReadOnlyList<string>>(ids => ids.Contains("g1")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconcile_UntaggedOrphan_IsNeverDeleted()
    {
        var h = Build();
        h.Connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Connection>>(_ => [S3("{\"allowedLocations\":[\"kept-bucket\"]}")]);
        h.Reader.ListAllAsync("us-east-1", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AccessGrantDetail>>(_ => [GroupGrant("s3://gone-bucket/*", "g1")]);
        // Provenance says it is not ours.
        h.Writer.FilterManagedAsync("us-east-1", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(_ => []);

        var report = await h.Service.ReconcileAsync(enforce: true);

        report.Orphaned.Should().Be(1);
        report.Deleted.Should().Be(0);
        await h.Writer.DidNotReceive().RevokeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
