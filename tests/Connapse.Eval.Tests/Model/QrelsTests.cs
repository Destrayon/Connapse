using Connapse.Eval.Model;
using FluentAssertions;

namespace Connapse.Eval.Tests.Model;

[Trait("Category", "Unit")]
public class QrelsTests
{
    [Fact]
    public void ParseTrec_ValidLines_ReadsGradesPerQuery()
    {
        Qrels qrels = Qrels.ParseTrec(new StringReader("q1 0 d1 2\nq1 0 d2 0\n\nq2 0 d3 1\n"));

        qrels.For("q1").Should().BeEquivalentTo(new Dictionary<string, int> { ["d1"] = 2, ["d2"] = 0 });
        qrels.For("q2").Should().BeEquivalentTo(new Dictionary<string, int> { ["d3"] = 1 });
        qrels.For("missing").Should().BeEmpty();
        qrels.QueryIds.Should().BeEquivalentTo(["q1", "q2"]);
    }

    [Fact]
    public void ParseTrec_MalformedLine_ThrowsWithLineNumber()
    {
        Action act = () => Qrels.ParseTrec(new StringReader("q1 0 d1 1\nq1 0 d2\n"));

        act.Should().Throw<FormatException>().WithMessage("*line 2*");
    }

    [Fact]
    public void ParseTrec_DuplicatePair_Throws()
    {
        Action act = () => Qrels.ParseTrec(new StringReader("q1 0 d1 1\nq1 0 d1 2\n"));

        act.Should().Throw<FormatException>().WithMessage("*q1*d1*");
    }

    [Fact]
    public void WriteTrec_AfterParse_RoundTripsInOrdinalOrder()
    {
        Qrels qrels = new();
        qrels.Add("q2", "d9", 1);
        qrels.Add("q1", "d2", 0);
        qrels.Add("q1", "d10", 3);
        StringWriter writer = new();

        qrels.WriteTrec(writer);

        writer.ToString().Should().Be("q1 0 d10 3\nq1 0 d2 0\nq2 0 d9 1\n");
        Qrels.ParseTrec(new StringReader(writer.ToString())).For("q1").Should().HaveCount(2);
    }
}
