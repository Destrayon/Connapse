using Connapse.Application.Tables;
using Connapse.Core;
using CsCheck;
using FluentAssertions;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// CON-310: a container's tables are ranked against a search by their cards' words (BM25) and, when embedded, by
/// meaning, merged by reciprocal rank fusion. Cards never hold hidden columns, and no table is ever dropped.
/// </summary>
[Trait("Category", "Unit")]
public class TableSearchTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TableInfo Table(string name, string? description = null, IReadOnlyList<TableColumn>? columns = null,
        IReadOnlyList<string>? hidden = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, description, Guid.NewGuid(), columns ?? [], 1, false, [], "tables_x", "t_x", 1,
            Epoch, Epoch, HiddenColumns: hidden);

    [Theory]
    [InlineData("order_date", "order date")]
    [InlineData("orderDate", "order Date")]
    [InlineData("CustomerOrders_2024", "Customer Orders 2024")]
    [InlineData("  a--b..c  ", "a b c")]
    [InlineData("HTTPStatus", "HTTPStatus")]
    [InlineData("", "")]
    public void Words_SplitsHeadersAtSeparatorsCaseAndDigits(string name, string expected) =>
        TableCard.Words(name).Should().Be(expected);

    [Fact]
    public void For_NameDescriptionAndVisibleColumns_WritesThemInOrder()
    {
        TableInfo table = Table("sales_orders", "One row per order.",
            [new TableColumn("order_id", TableColumnType.WholeNumber), new TableColumn("shipDate", TableColumnType.Date, "When it left")]);

        TableCard.For(table).Text.Should().Be("sales orders\nOne row per order.\nColumns: order id; ship Date (When it left)");
    }

    [Fact]
    public void For_HiddenColumn_LeavesItOutOfTheCard()
    {
        TableInfo table = Table("staff", null,
            [new TableColumn("name", TableColumnType.Text), new TableColumn("salary", TableColumnType.Number, "Annual pay")],
            hidden: ["salary"]);

        TableCard card = TableCard.For(table);

        card.Text.Should().NotContainAny("salary", "Annual pay");
        card.Text.Should().Contain("name");
    }

    [Fact]
    public void Hash_ChangesWithTheText()
    {
        Guid id = Guid.NewGuid();
        new TableCard(id, "a").Hash.Should().Be(new TableCard(id, "a").Hash).And.NotBe(new TableCard(id, "b").Hash);
    }

    [Theory]
    [InlineData("Categories", "category")]
    [InlineData("orders", "order")]
    [InlineData("addresses", "address")]
    [InlineData("status", "status")]
    [InlineData("class", "class")]
    [InlineData("analysis", "analysis")]
    [InlineData("gas", "gas")]
    public void Terms_LowercaseAndFoldPlurals(string word, string expected) =>
        TableSearch.Terms(word).Should().Equal(expected);

    [Fact]
    public void Terms_SplitAtAnythingButLettersAndDigits() =>
        TableSearch.Terms("Order #42: ship-date, café").Should().Equal("order", "42", "ship", "date", "café");

    [Fact]
    public void Rank_KeywordsOnly_PutsTheTableSharingTheRarestWordFirst()
    {
        TableInfo[] tables =
        [
            Table("customers", "People who buy things."),
            Table("orders", "One row per order a customer placed."),
            Table("shipments", "When each order shipped and its carrier."),
        ];

        IReadOnlyList<Guid> ranked = TableSearch.Rank([.. tables.Select(TableCard.For)], "which carrier shipped it");

        ranked[0].Should().Be(tables[2].Id);
        ranked.Should().HaveCount(3);
    }

    [Fact]
    public void Rank_SimilarityFindsATableSharingNoWords()
    {
        TableInfo[] tables = [Table("alpha"), Table("beta"), Table("gamma")];
        var similarities = new Dictionary<Guid, double> { [tables[0].Id] = 0.1, [tables[1].Id] = 0.2, [tables[2].Id] = 0.9 };

        TableSearch.Rank([.. tables.Select(TableCard.For)], "staff pay", similarities)[0].Should().Be(tables[2].Id);
    }

    [Fact]
    public void Rank_WordsAndMeaningAgree_BeatsATableOnlyOneFavours()
    {
        TableInfo[] tables = [Table("payroll"), Table("staff payroll history"), Table("holidays")];
        var similarities = new Dictionary<Guid, double> { [tables[0].Id] = 0.5, [tables[1].Id] = 0.8, [tables[2].Id] = 0.9 };

        TableSearch.Rank([.. tables.Select(TableCard.For)], "staff payroll", similarities)[0].Should().Be(tables[1].Id);
    }

    [Fact]
    public void Rank_NothingMatches_KeepsTheGivenOrder()
    {
        TableInfo[] tables = [Table("a"), Table("b"), Table("c")];

        TableSearch.Rank([.. tables.Select(TableCard.For)], "zzz").Should().Equal(tables.Select(t => t.Id));
    }

    [Fact]
    public void Rank_AnyCardsQueryAndScores_ReturnsEveryTableOnceAndPutsAnExactWordMatchAboveNoMatch() =>
        Gen.Select(Gen.String[Gen.Char.AlphaNumeric, 0, 12].Array[0, 30], Gen.String[Gen.Char.AlphaNumeric, 1, 8],
                Gen.Double[-1, 1].Array[0, 30])
            .Sample((names, word, scores) =>
            {
                List<TableCard> cards = [.. names.Select(n => new TableCard(Guid.NewGuid(), n))];
                var similarities = cards.Zip(scores).ToDictionary(p => p.First.TableId, p => p.Second);

                IReadOnlyList<Guid> keyword = TableSearch.Rank(cards, word);
                IReadOnlyList<Guid> hybrid = TableSearch.Rank(cards, word, similarities);

                keyword.Should().BeEquivalentTo(cards.Select(c => c.TableId)).And.OnlyHaveUniqueItems();
                hybrid.Should().BeEquivalentTo(cards.Select(c => c.TableId)).And.OnlyHaveUniqueItems();
                double[] bm25 = TableSearch.Bm25(cards, word);
                bm25.Should().OnlyContain(s => s >= 0 && !double.IsNaN(s));
                int firstMiss = keyword.Select((id, i) => (id, i)).Where(p => bm25[cards.FindIndex(c => c.TableId == p.id)] == 0)
                    .Select(p => p.i).DefaultIfEmpty(keyword.Count).First();
                keyword.Skip(firstMiss).Should().OnlyContain(id => bm25[cards.FindIndex(c => c.TableId == id)] == 0);
            });

    [Fact]
    public void For_AnyColumnsAndHiddenSet_NeverWritesAHiddenColumnsDescription() =>
        Gen.Select(Gen.Bool.Array[0, 8], Gen.Int[0, 1000])
            .Sample((hide, seed) =>
            {
                TableColumn[] columns = [.. hide.Select((_, i) => new TableColumn($"c{i}x{seed}", TableColumnType.Text, $"secret{i}q{seed}"))];
                TableInfo table = Table("t", null, columns, [.. columns.Where((_, i) => hide[i]).Select(c => c.Name)]);

                string text = TableCard.For(table).Text;

                for (int i = 0; i < columns.Length; i++)
                    text.Contains($"secret{i}q{seed}", StringComparison.Ordinal).Should().Be(!hide[i]);
            });
}
