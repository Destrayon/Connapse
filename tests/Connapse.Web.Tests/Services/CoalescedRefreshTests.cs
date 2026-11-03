using Connapse.Web.Services;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Services;

[Trait("Category", "Unit")]
public class CoalescedRefreshTests
{
    [Fact]
    public async Task RunAsync_CalledWhileReadInFlight_CollapsesIntoOneTrailingRead()
    {
        var reads = new List<TaskCompletionSource>();
        var refresh = new CoalescedRefresh(() =>
        {
            var read = new TaskCompletionSource();
            reads.Add(read);
            return read.Task;
        });

        Task first = refresh.RunAsync();
        Task[] burst = [refresh.RunAsync(), refresh.RunAsync(), refresh.RunAsync()];

        burst.Should().OnlyContain(t => t.IsCompleted, "a read is already in flight, so these only mark it stale");
        reads.Should().HaveCount(1);

        reads[0].SetResult();
        reads.Should().HaveCount(2, "the burst arrived after the first read began, so one more read follows it");
        first.IsCompleted.Should().BeFalse();

        reads[1].SetResult();
        await first;
        reads.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_ReadThrows_NextCallReadsAgain()
    {
        int calls = 0;
        var refresh = new CoalescedRefresh(() => ++calls == 1 ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask);

        await refresh.Invoking(r => r.RunAsync()).Should().ThrowAsync<InvalidOperationException>();
        await refresh.RunAsync();

        calls.Should().Be(2);
    }
}
