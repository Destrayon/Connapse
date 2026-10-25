using Connapse.Application.Ports;
using Connapse.Background.Jobs;
using Hangfire;

namespace Connapse.Web.Services;

/// <summary>Queues a container's summary rollup as the Hangfire job the summary sweep runs.</summary>
public sealed class HangfireSummaryRollup(IBackgroundJobClient jobs) : ISummaryRollup
{
    public string Enqueue(Guid containerId) => jobs.Enqueue<ISummaryJobs>(s => s.RollupContainerAsync(containerId, default));
}
