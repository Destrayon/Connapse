using Connapse.Application.Approvals;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// CON-347: a re-upload to the same path keeps the file's id, path, name and creation date, and can keep its size and
/// type, so only the content version tells a held delete that the file it was asked about has been replaced.
/// </summary>
[Trait("Category", "Unit")]
public class DeletionFingerprintTests
{
    private static readonly DateTime Created = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Document Doc(string id = "d1", string path = "/notes/a.md") =>
        new(id, Guid.Empty.ToString(), "a.md", "text/markdown", path, 10, Created, []) { ContentVersion = Guid.NewGuid() };

    [Fact]
    public void FileFingerprint_ReplacedWithSameSizeAndType_Changes()
    {
        Document original = Doc();
        Document replaced = original with { ContentVersion = Guid.NewGuid() };

        DeletionTargets.FileFingerprint(replaced).Should().NotBe(DeletionTargets.FileFingerprint(original));
    }

    [Fact]
    public void FileFingerprint_SameVersion_IsStable()
    {
        Document original = Doc();

        DeletionTargets.FileFingerprint(original with { }).Should().Be(DeletionTargets.FileFingerprint(original));
    }

    [Fact]
    public void FolderFingerprint_FileInsideReplacedWithSameSizeAndType_Changes()
    {
        Guid container = Guid.NewGuid();
        Document inside = Doc();
        string before = DeletionTargets.FolderFingerprint(container, "/notes/", true, [inside]);

        DeletionTargets.FolderFingerprint(container, "/notes/", true, [inside with { ContentVersion = Guid.NewGuid() }])
            .Should().NotBe(before);
        DeletionTargets.FolderFingerprint(container, "/notes/", true, [inside with { }]).Should().Be(before);
    }
}
