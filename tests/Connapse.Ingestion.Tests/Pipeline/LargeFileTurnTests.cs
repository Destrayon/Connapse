using Connapse.Ingestion.Pipeline;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Pipeline;

[Trait("Category", "Unit")]
public sealed class LargeFileTurnTests
{
    private const long Large = IngestionPipeline.LargeFileBytes + 1;

    [Fact]
    public async Task OneLargeFileAtATimeAsync_SecondLargeFile_WaitsForTheFirst()
    {
        TaskCompletionSource finish = new();
        Task<bool> first = IngestionPipeline.OneLargeFileAtATimeAsync(Large, async () =>
        {
            await finish.Task;
            return true;
        }, CancellationToken.None);

        bool secondStarted = false;
        Task<bool> second = IngestionPipeline.OneLargeFileAtATimeAsync(Large, () => Task.FromResult(secondStarted = true), CancellationToken.None);

        secondStarted.Should().BeFalse();
        finish.SetResult();
        await Task.WhenAll(first, second);
        secondStarted.Should().BeTrue();
    }

    [Fact]
    public async Task OneLargeFileAtATimeAsync_SmallFile_GoesPastALargeOne()
    {
        TaskCompletionSource finish = new();
        Task<bool> large = IngestionPipeline.OneLargeFileAtATimeAsync(Large, async () =>
        {
            await finish.Task;
            return true;
        }, CancellationToken.None);

        bool small = await IngestionPipeline.OneLargeFileAtATimeAsync(IngestionPipeline.LargeFileBytes, () => Task.FromResult(true), CancellationToken.None);

        small.Should().BeTrue();
        finish.SetResult();
        await large;
    }

    [Fact]
    public async Task OneLargeFileAtATimeAsync_ParseThrows_LeavesTheTurnFree()
    {
        Func<Task> failing = () => IngestionPipeline.OneLargeFileAtATimeAsync<bool>(Large, () => throw new InvalidDataException(), CancellationToken.None);
        await failing.Should().ThrowAsync<InvalidDataException>();

        (await IngestionPipeline.OneLargeFileAtATimeAsync(Large, () => Task.FromResult(true), CancellationToken.None)).Should().BeTrue();
    }
}
