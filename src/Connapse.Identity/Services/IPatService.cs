using Connapse.Core;

namespace Connapse.Identity.Services;

/// <summary>A person's personal access tokens, each read and changed only by the user who owns it.</summary>
public interface IPatService
{
    Task<PatCreateResponse> CreateAsync(
        Guid userId,
        PatCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PatListItem>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes one of the user's active tokens; false when they have no such token.</summary>
    Task<bool> RevokeAsync(
        Guid userId,
        Guid patId,
        CancellationToken cancellationToken = default);
}
