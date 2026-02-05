namespace AIKnowledge.Storage.FileSystem;

public class MinioOptions
{
    public const string SectionName = "Knowledge:Storage:MinIO";

    public string Endpoint { get; set; } = "localhost:9000";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public bool UseSSL { get; set; }
    public string BucketName { get; set; } = "aikp-files";
}
