namespace Connapse.Core;

public sealed record ContainerSummaryDirtyEvent(
    Guid ContainerId,
    Guid DocumentId);
