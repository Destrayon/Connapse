using Connapse.Core.Interfaces;
using NSubstitute;

namespace Connapse.Core.Tests;

public static class DocumentStoreStubs
{
    /// <summary>
    /// Has a substitute store run the delete <see cref="IDocumentStore.RemoveUnrecordedFileAsync"/> is given, as the real
    /// store does while no record holds the path; with <paramref name="pathRecorded"/>, as it does once a newer upload's
    /// record holds it, skipping the delete.
    /// </summary>
    public static void RemovesUnrecordedFiles(this IDocumentStore store, bool pathRecorded = false) =>
        store.RemoveUnrecordedFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                if (pathRecorded)
                    return false;
                await ci.ArgAt<Func<CancellationToken, Task>>(2)(ci.ArgAt<CancellationToken>(3));
                return true;
            });
}
