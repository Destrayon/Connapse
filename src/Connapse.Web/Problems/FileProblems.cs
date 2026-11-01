using Connapse.Application;
using Connapse.Application.Files;
using Connapse.Core.Utilities;

namespace Connapse.Web.Problems;

/// <summary>
/// A failed file or folder action's problem. Before files and folders moved onto their operations (#783), these
/// failures were answered with a code in <c>error</c>, which scripts branch on, so they keep it beside the problem
/// type, as search does. docs/self-host/upgrade.md lists each code with its type.
/// </summary>
public static class FileProblems
{
    /// <param name="fileName">The uploaded file's name, when one file was uploaded, so a name that is too long keeps its code.</param>
    public static IResult Result<TResult>(Attempt<TResult, FileStatus> failed, string? fileName = null)
    {
        var problem = OperationProblems.For(failed.Status, failed.Detail);
        if (failed.Field is { } field)
            problem.Extensions["field"] = field;
        if (ErrorCode(failed.Status, failed.Field, fileName) is { } code)
            problem.Extensions["error"] = code;
        return Results.Problem(problem);
    }

    /// <summary>A failed folder create's problem. Only creation ever answered with path_too_deep, so a delete doesn't use this.</summary>
    /// <param name="path">The folder path the request named, so a path that is too deep keeps its code.</param>
    public static IResult Result<TResult>(Attempt<TResult, FolderStatus> failed, string? path)
    {
        var problem = OperationProblems.For(failed.Status, failed.Detail);
        if (failed.Field is { } field)
            problem.Extensions["field"] = field;
        if (ErrorCode(failed.Status, failed.Field, path) is { } code)
            problem.Extensions["error"] = code;
        return Results.Problem(problem);
    }

    public static string? ErrorCode(FileStatus status, string? field, string? fileName) => status switch
    {
        FileStatus.UnsupportedType => "unsupported_file_type",
        FileStatus.NotReady => "document_not_ready",
        FileStatus.IngestionFailed => "document_failed",
        FileStatus.NoParser => "no_parser",
        FileStatus.NotReadable => "file_not_readable",
        FileStatus.NotWritable => "write_denied",
        FileStatus.Invalid when field == "fileName" && fileName?.Length > ValidationConstants.MaxFileNameLength
            => "filename_too_long",
        _ => null,
    };

    /// <remarks>A path with a <c>..</c> segment is refused for that first, so it has no depth code.</remarks>
    public static string? ErrorCode(FolderStatus status, string? field, string? path) =>
        status == FolderStatus.Invalid && field == "path" && path is not null
            && !PathUtilities.ContainsPathTraversal(path)
            && path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length > ValidationConstants.MaxPathDepth
            ? "path_too_deep"
            : null;
}
