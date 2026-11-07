namespace Connapse.Web.Components.Shared;

/// <summary>What wwwroot/js/fileDrop.js reports about a folder upload. Stage is one of the constants below.</summary>
public record FolderUploadProgress(
    string Stage,
    int Found,
    int Total,
    int Succeeded,
    int Failed,
    bool Waiting,
    bool Cancelled,
    string? StoppedBecause)
{
    public const string Reading = "reading";
    public const string Confirm = "confirm";
    public const string Uploading = "uploading";
    public const string Finished = "finished";
}

/// <summary>One file that didn't upload: where it was going, and why.</summary>
public record FolderUploadFailure(string Path, string Error);
