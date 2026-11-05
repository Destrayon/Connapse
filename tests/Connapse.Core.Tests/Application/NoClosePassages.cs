using Connapse.Application;
using Connapse.Application.Memory;

namespace Connapse.Core.Tests.Application;

/// <summary>A document check that finds nothing close, so container saves behave as they did before CON-245.</summary>
internal sealed class NoClosePassages : IDocumentPassages
{
    public static readonly NoClosePassages Instance = new();

    public Task<PassageLookup> FindAsync(OperationContext context, Guid containerId, string text, CancellationToken ct = default) =>
        Task.FromResult(new PassageLookup([]));
}
