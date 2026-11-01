using Connapse.Application;
using Connapse.Application.Files;
using Connapse.Web.Problems;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Tests.Problems;

[Trait("Category", "Unit")]
public class FileProblemsTests
{
    private static ProblemDetails Problem(IResult result) =>
        result.Should().BeOfType<ProblemHttpResult>().Subject.ProblemDetails;

    [Theory]
    [InlineData(FileStatus.UnsupportedType, "unsupported-file-type", 400, "unsupported_file_type")]
    [InlineData(FileStatus.NotReady, "file-not-ready", 400, "document_not_ready")]
    [InlineData(FileStatus.IngestionFailed, "file-ingestion-failed", 400, "document_failed")]
    [InlineData(FileStatus.NoParser, "no-parser", 400, "no_parser")]
    [InlineData(FileStatus.NotReadable, "file-not-readable", 404, "file_not_readable")]
    [InlineData(FileStatus.NotWritable, "container-not-writable", 400, "write_denied")]
    public void Result_FileFailure_CarriesProblemTypeAndOldCode(FileStatus status, string slug, int httpStatus, string code)
    {
        var problem = Problem(FileProblems.Result(Attempt<string, FileStatus>.Fail(status, "what went wrong")));

        problem.Type.Should().Be(OperationProblems.TypeBase + slug);
        problem.Status.Should().Be(httpStatus);
        problem.Detail.Should().Be("what went wrong");
        problem.Extensions["error"].Should().Be(code, "scripts written before #783 branch on the code");
    }

    [Fact]
    public void Result_FileNameTooLong_CarriesFilenameTooLong()
    {
        string name = new string('a', 300) + ".txt";

        var problem = Problem(FileProblems.Result(
            Attempt<string, FileStatus>.Fail(FileStatus.Invalid, "Filename exceeds 255 characters.", "fileName"), name));

        problem.Type.Should().Be(OperationProblems.TypeBase + "invalid-file-request");
        problem.Status.Should().Be(400);
        problem.Extensions["field"].Should().Be("fileName");
        problem.Extensions["error"].Should().Be("filename_too_long");
    }

    [Fact]
    public void Result_FileNameInvalidButShort_HasNoOldCode()
    {
        var problem = Problem(FileProblems.Result(
            Attempt<string, FileStatus>.Fail(FileStatus.Invalid, "Invalid filename: 'a|b.txt'.", "fileName"), "a|b.txt"));

        problem.Extensions["error"].Should().Be("Invalid filename: 'a|b.txt'.");
    }

    [Theory]
    [InlineData(FileStatus.NotFound)]
    [InlineData(FileStatus.ContainerNotFound)]
    [InlineData(FileStatus.Forbidden)]
    [InlineData(FileStatus.InvalidPage)]
    public void Result_FileFailureThatNeverHadACode_KeepsDetailInError(FileStatus status)
    {
        var problem = Problem(FileProblems.Result(Attempt<string, FileStatus>.Fail(status, "what went wrong")));

        problem.Extensions["error"].Should().Be("what went wrong");
    }

    [Fact]
    public void Result_FolderPathTooDeep_CarriesPathTooDeep()
    {
        string path = "/" + string.Join('/', Enumerable.Range(0, 51).Select(i => $"d{i}"));

        var problem = Problem(FileProblems.Result(
            Attempt<string, FolderStatus>.Fail(FolderStatus.Invalid, "Path exceeds maximum depth of 50 levels", "path"), path));

        problem.Type.Should().Be(OperationProblems.TypeBase + "invalid-folder");
        problem.Status.Should().Be(400);
        problem.Extensions["field"].Should().Be("path");
        problem.Extensions["error"].Should().Be("path_too_deep");
    }

    [Fact]
    public void Result_FolderPathTooDeepWithTraversal_HasNoOldCode()
    {
        string path = "/" + string.Join('/', Enumerable.Range(0, 51).Select(i => $"d{i}")) + "/..";

        var problem = Problem(FileProblems.Result(
            Attempt<string, FolderStatus>.Fail(FolderStatus.Invalid, "Path must not contain '..' segments", "path"), path));

        problem.Extensions["error"].Should().Be("Path must not contain '..' segments");
    }

    [Fact]
    public void Result_FolderExists_KeepsDetailInError()
    {
        var problem = Problem(FileProblems.Result(
            Attempt<string, FolderStatus>.Fail(FolderStatus.Exists, "Folder '/a/' already exists"), "/a"));

        problem.Status.Should().Be(409);
        problem.Extensions["error"].Should().Be("Folder '/a/' already exists");
    }
}
