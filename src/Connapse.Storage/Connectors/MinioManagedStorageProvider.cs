using Amazon.S3;
using Amazon.S3.Model;
using Connapse.Core.Interfaces;
using Connapse.Storage.FileSystem;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Default IManagedStorageProvider backed by MinIO (S3-compatible).
/// Creates buckets for tenants and returns MinioConnector instances.
/// Used in local dev and self-hosted deployments.
/// </summary>
public class MinioManagedStorageProvider(
    IAmazonS3 s3,
    IOptions<MinioOptions> minioOptions) : IManagedStorageProvider
{
    public async Task CreateStorageAsync(string containerName, CancellationToken ct = default)
    {
        // Check if bucket already exists (idempotent)
        try
        {
            await s3.GetBucketLocationAsync(new GetBucketLocationRequest
            {
                BucketName = containerName,
            }, ct);
            return; // Already exists
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Bucket doesn't exist — create it
        }

        await s3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = containerName,
        }, ct);
    }

    public async Task DeleteStorageAsync(string containerName, CancellationToken ct = default)
    {
        // Delete all objects first (S3 requires empty bucket for deletion)
        var listRequest = new ListObjectsV2Request { BucketName = containerName };
        ListObjectsV2Response listResponse;
        do
        {
            try
            {
                listResponse = await s3.ListObjectsV2Async(listRequest, ct);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return; // Bucket doesn't exist — nothing to delete
            }

            if (listResponse.S3Objects.Count > 0)
            {
                var deleteRequest = new DeleteObjectsRequest
                {
                    BucketName = containerName,
                    Objects = listResponse.S3Objects
                        .Select(o => new KeyVersion { Key = o.Key })
                        .ToList(),
                };
                await s3.DeleteObjectsAsync(deleteRequest, ct);
            }
            listRequest.ContinuationToken = listResponse.NextContinuationToken;
        } while (listResponse.IsTruncated == true);

        await s3.DeleteBucketAsync(new DeleteBucketRequest
        {
            BucketName = containerName,
        }, ct);
    }

    public IConnector CreateConnector(string containerId)
    {
        // Use the container ID as a path prefix within the shared MinIO bucket
        var config = new MinioConnectorConfig { ContainerId = containerId };
        return new MinioConnector(s3, minioOptions, config);
    }
}
