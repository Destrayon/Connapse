using Connapse.Application;
using Connapse.Application.Memory;

namespace Connapse.Integration.Tests.Memory;

/// <summary>A document check that finds nothing close, for tests whose saves never reach a document container.</summary>
internal sealed class NoClosePassages : IDocumentPassages
{
    public static readonly NoClosePassages Instance = new();

    public Task<PassageLookup> FindAsync(OperationContext context, Guid containerId, string text, CancellationToken ct = default) =>
        Task.FromResult(new PassageLookup([]));
}
