using System.Text.Json.Serialization;

namespace Connapse.Core;

/// <summary>
/// What a source reads, typed per kind (#859), instead of an opaque JSON string. A source still stores
/// its scope as JSON with the keys each connector reads; <see cref="SourceScopeCodec"/> maps between the
/// two by the source's provider, so rows saved before this type existed load without a migration.
/// Over the API the kind is the <c>type</c> member.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(S3BucketScope), "s3")]
[JsonDerivedType(typeof(AzureBlobContainerScope), "azureBlob")]
[JsonDerivedType(typeof(FilesystemFolderScope), "filesystem")]
[JsonDerivedType(typeof(SftpFolderScope), "sftp")]
[JsonDerivedType(typeof(GitHubRepositoryScope), "github")]
[JsonDerivedType(typeof(JiraProjectScope), "jira")]
[JsonDerivedType(typeof(ConfluenceSpaceScope), "confluence")]
[JsonDerivedType(typeof(CustomSourceScope), "custom")]
public abstract record SourceScope;

/// <summary>An S3 bucket, optionally only the keys under <see cref="Prefix"/>.</summary>
public sealed record S3BucketScope(string BucketName, string? Prefix = null) : SourceScope;

/// <summary>An Azure Blob container, optionally only the blobs under <see cref="Prefix"/>.</summary>
public sealed record AzureBlobContainerScope(string ContainerName, string? Prefix = null) : SourceScope;

/// <summary>A folder under the connection's allowed root, filtered by glob patterns.</summary>
public sealed record FilesystemFolderScope(
    string? SubPath = null,
    IReadOnlyList<string>? IncludePatterns = null,
    IReadOnlyList<string>? ExcludePatterns = null) : SourceScope;

/// <summary>A folder on the SFTP server under the connection's allowed root, filtered by glob patterns.</summary>
public sealed record SftpFolderScope(
    string? SubPath = null,
    IReadOnlyList<string>? IncludePatterns = null,
    IReadOnlyList<string>? ExcludePatterns = null) : SourceScope;

/// <summary>
/// One of a GitHub repository's two kinds of content. <see cref="RepoId"/> and <see cref="Private"/>
/// come from GitHub, never from the caller; the comment members apply to issues and pull requests.
/// </summary>
public sealed record GitHubRepositoryScope(
    string Owner,
    string Repo,
    GitHubContentKind Kind,
    long? RepoId = null,
    bool Private = false,
    IReadOnlyList<string>? IncludePatterns = null,
    IReadOnlyList<string>? ExcludePatterns = null,
    bool? IncludeComments = null,
    IReadOnlyList<string>? IncludeCommentAuthors = null,
    IReadOnlyList<string>? ExcludeCommentAuthors = null) : SourceScope;

/// <summary>A Jira project on an Atlassian site connection, by its numeric id and its key.</summary>
public sealed record JiraProjectScope(
    string ProjectId,
    string? ProjectKey = null,
    string? ProjectName = null,
    bool? IncludeAttachments = null,
    int? MaxAttachmentMb = null) : SourceScope;

/// <summary>A Confluence space on an Atlassian site connection, by its numeric id and its key.</summary>
public sealed record ConfluenceSpaceScope(
    string SpaceId,
    string? SpaceKey = null,
    string? SpaceName = null,
    bool? IncludeAttachments = null,
    int? MaxAttachmentMb = null) : SourceScope;
