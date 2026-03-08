namespace Connapse.Core;

public record PagedRequest(int Skip = 0, int Take = 50);

public record PagedResponse<T>(IReadOnlyList<T> Items, int TotalCount, bool HasMore);
