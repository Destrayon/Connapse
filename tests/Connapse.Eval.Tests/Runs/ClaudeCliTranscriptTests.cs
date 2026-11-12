using System.Text.Json;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

// CON-312: an eval run can keep each agent's stream-json, so its reasoning can be read afterwards.
[Trait("Category", "Unit")]
public class ClaudeCliTranscriptTests
{
    [Fact]
    public async Task SaveTranscript_Folder_WritesTheQuestionThenEveryLine()
    {
        string dir = Path.Combine(Path.GetTempPath(), "con312-transcripts-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ClaudeCliAgent.SaveTranscriptAsync(dir, "s1", "How many \"vans\"?", ["{\"type\":\"system\"}", "{\"type\":\"result\"}"], CancellationToken.None);

            string[] lines = await File.ReadAllLinesAsync(Path.Combine(dir, "s1.jsonl"));
            lines.Should().HaveCount(3);
            JsonDocument.Parse(lines[0]).RootElement.GetProperty("question").GetString().Should().Be("How many \"vans\"?");
            lines[2].Should().Be("{\"type\":\"result\"}");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task SaveTranscript_NoFolder_WritesNothing(string? dir)
    {
        Func<Task> save = () => ClaudeCliAgent.SaveTranscriptAsync(dir, "s1", "q", ["x"], CancellationToken.None);

        await save.Should().NotThrowAsync();
    }
}
