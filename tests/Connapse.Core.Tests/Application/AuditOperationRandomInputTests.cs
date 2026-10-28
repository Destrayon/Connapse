using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Audit;
using Connapse.Application.Ports;
using Connapse.Core.Interfaces;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>audit.list's rules over random queries (#880): who is refused, which pages and ranges are refused, and what is read.</summary>
[Trait("Category", "Unit")]
public class AuditOperationRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Gen<DateTime?> AnyTime = Gen.Select(Gen.Int[-3, 3], Gen.Enum<DateTimeKind>(),
        (hours, kind) => DateTime.SpecifyKind(Epoch.AddHours(hours), kind)).Nullable();

    private static readonly Gen<AuditQuery> AnyQuery = Gen.Select(
        Gen.OneOf(Gen.Const((string?)null), Gen.Const((string?)"source.created")), Gen.Guid.Nullable(),
        AnyTime, AnyTime, Gen.Bool, Gen.Int[-2, 203],
        (action, user, since, until, paged, take) => new AuditQuery(
            action, user, Since: since, Until: until, Before: paged ? new AuditCursor(Epoch, Guid.Empty) : null, Take: take));

    private static DateTime? Utc(DateTime? time) => time switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } t => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        { } t => t.ToUniversalTime(),
    };

    [Fact]
    [RandomInputTestOf(typeof(ListAuditLog))]
    public Task List_AnyQuery_ReadsOnlyValidQueriesForAuditReaders() =>
        Gen.Select(Gen.Bool, AnyQuery, Gen.Enum<OperationSurface>()).SampleAsync(async t =>
        {
            var (allowed, query, surface) = t;
            var reader = Substitute.For<IAuditLogReader>();
            var page = new Connapse.Core.AuditPage([], null);
            AuditQuery? asked = null;
            reader.ListAsync(Arg.Any<AuditQuery>(), Arg.Any<CancellationToken>()).Returns(c =>
            {
                asked = c.ArgAt<AuditQuery>(0);
                return page;
            });
            var authorizer = new PolicyAuthorizer(policy => policy == Policies.AuditReader && allowed);

            var listed = await new ListAuditLog(reader, authorizer).RunAsync(new OperationContext(new ClaimsPrincipal(), surface), query);

            var expected = !allowed ? AuditReadStatus.Forbidden
                : query.Take is < 1 or > ListAuditLog.MaxTake ? AuditReadStatus.InvalidPage
                : query.Since is { } since && query.Until is { } until && since >= until ? AuditReadStatus.InvalidFilter
                : AuditReadStatus.Success;
            listed.Status.Should().Be(expected);
            if (expected != AuditReadStatus.Success)
            {
                reader.ReceivedCalls().Should().BeEmpty();
                return;
            }
            listed.Result.Should().BeSameAs(page);
            // Everything is read as asked, with the range's ends in UTC.
            asked.Should().Be(query with { Since = Utc(query.Since), Until = Utc(query.Until) });
            asked!.Since?.Kind.Should().Be(DateTimeKind.Utc);
            asked.Until?.Kind.Should().Be(DateTimeKind.Utc);
        }, iter: 2000);
}
