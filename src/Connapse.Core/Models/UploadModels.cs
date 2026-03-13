namespace Connapse.Core;

public record UploadRequest(
    Guid ContainerId,
    string FileName,
    Stream Content,
    Guid? UserId = null,
    string? Path = null,
    string? ContentType = null,
    string? Strategy = null,
    string IngestedVia = "API");

public record UploadResult(
    bool Success,
    string? DocumentId = null,
    string? JobId = null,
    string? Error = null);

public record BulkUploadRequest(
    Guid ContainerId,
    IReadOnlyList<UploadRequest> Files);

public record BulkUploadResult(
    int SuccessCount,
    int FailureCount,
    string? BatchId = null,
    IReadOnlyList<UploadResult> Results = null!);
