using System.Security.Claims;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// An authorizer that answers each policy from <see cref="Allows"/>. Random-input tests build one per
/// sample, and a plain class costs far less than an NSubstitute proxy configured per policy.
/// </summary>
internal sealed class PolicyAuthorizer(Func<string, bool> allows) : IOperationAuthorizer
{
    public Func<string, bool> Allows { get; set; } = allows;

    public Task<bool> AllowsAsync(ClaimsPrincipal user, string policy) => Task.FromResult(Allows(policy));
}

/// <summary>An audit logger that records nothing, for samples that never check what was audited.</summary>
internal sealed class NoAudit : IAuditLogger
{
    public static readonly NoAudit Instance = new();

    public Task LogAsync(string action, string? resourceType = null, string? resourceId = null, object? details = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Source visibility from a fixed filter: every source is visible unless <see cref="Sees"/> says otherwise.</summary>
internal sealed class FixedVisibility : ISourceVisibility
{
    public Func<Source, bool> Sees { get; set; } = _ => true;

    public Func<Source, bool> SearchableWhenHidden { get; set; } = _ => false;

    public Task<Func<Source, bool>> ForAsync(ClaimsPrincipal? caller, CancellationToken ct = default) => Task.FromResult(Sees);

    public bool IsSearchableWhenHidden(Source source) => SearchableWhenHidden(source);
}
