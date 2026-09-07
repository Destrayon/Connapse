using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureTagConditionEvaluatorTests
{
    private static AzureTagCondition Cond(bool keyCase = true, bool valueCase = false) =>
        new("azblob://acct/docs/", "Project", "Cascade", keyCase, valueCase);

    [Fact]
    public void Matches_KeyAndValuePresent_True() =>
        AzureTagConditionEvaluator.Matches(Cond(), new Dictionary<string, string> { ["Project"] = "Cascade" })
            .Should().BeTrue();

    [Fact]
    public void Matches_MissingKey_False() =>
        AzureTagConditionEvaluator.Matches(Cond(), new Dictionary<string, string> { ["Other"] = "Cascade" })
            .Should().BeFalse();

    [Fact]
    public void Matches_KeyCaseSensitive_DoesNotMatchDifferentCaseKey() =>
        AzureTagConditionEvaluator.Matches(Cond(keyCase: true), new Dictionary<string, string> { ["project"] = "Cascade" })
            .Should().BeFalse();

    [Fact]
    public void Matches_ValueCaseInsensitive_MatchesDifferentCaseValue() =>
        AzureTagConditionEvaluator.Matches(Cond(valueCase: false), new Dictionary<string, string> { ["Project"] = "cascade" })
            .Should().BeTrue();

    [Fact]
    public void Matches_ValueCaseSensitive_DoesNotMatchDifferentCaseValue() =>
        AzureTagConditionEvaluator.Matches(Cond(valueCase: true), new Dictionary<string, string> { ["Project"] = "cascade" })
            .Should().BeFalse();

    [Fact]
    public void Matches_WrongValue_False() =>
        AzureTagConditionEvaluator.Matches(Cond(), new Dictionary<string, string> { ["Project"] = "Other" })
            .Should().BeFalse();
}
