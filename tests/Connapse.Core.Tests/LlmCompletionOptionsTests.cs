using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests;

[Trait("Category", "Unit")]
public class LlmCompletionOptionsTests
{
    [Fact]
    public void DefaultInstance_HasNullModel()
    {
        LlmCompletionOptions opts = new();
        opts.Model.Should().BeNull();
        opts.Temperature.Should().BeNull();
        opts.MaxTokens.Should().BeNull();
    }

    [Fact]
    public void Model_CanBeSetViaWithExpression()
    {
        LlmCompletionOptions opts = new() { Model = "claude-haiku-4-5" };
        opts.Model.Should().Be("claude-haiku-4-5");
    }
}
