namespace Connapse.Core.Interfaces;

public interface IUploadService
{
    Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken ct = default);
    Task<BulkUploadResult> BulkUploadAsync(BulkUploadRequest request, CancellationToken ct = default);
}
