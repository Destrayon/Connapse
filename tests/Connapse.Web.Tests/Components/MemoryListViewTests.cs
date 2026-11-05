using Connapse.Application.Memory;
using Connapse.Core;
using Connapse.Web.Components.Memory;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;

namespace Connapse.Web.Tests.Components;

/// <summary>The memory list's view: what it reads from and writes to the address, and which sort and rows it means.</summary>
[Trait("Category", "Unit")]
public class MemoryListViewTests
{
    private static readonly Guid Author = Guid.NewGuid();

    [Fact]
    public void Query_ThenParse_GivesBackTheSameView()
    {
        var view = new MemoryListView("postgres db", "Halcyon & Co", Author, MemoryCites.None,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), All: true, MemoryFactSort.ValidFrom, "abc+/=", Page: 3);

        string uri = QueryHelpers.AddQueryString("http://localhost/containers/x?tab=memory",
            view.Query().Where(p => p.Value is not null).ToDictionary(p => p.Key, p => (string?)p.Value));

        MemoryListView.Parse(uri).Should().Be(view);
    }

    [Fact]
    public void Query_DefaultView_WritesNothing()
    {
        new MemoryListView().Query().Values.Should().AllSatisfy(v => v.Should().BeNull());
    }

    [Fact]
    public void Parse_JunkValues_AreDroppedNotGuessed()
    {
        MemoryListView view = MemoryListView.Parse(
            "http://localhost/x?author=nope&cites=maybe&from=01/02/2026&to=2026-13-01&status=old&sort=size&page=7&q=%20%20");

        view.Should().Be(new MemoryListView());
    }

    [Fact]
    public void Parse_PageWithoutItsCursor_IsTheFirstPage()
    {
        MemoryListView.Parse("http://localhost/x?page=4").Page.Should().Be(1);
        MemoryListView.Parse("http://localhost/x?after=c&page=0").Page.Should().Be(1);
        MemoryListView.Parse("http://localhost/x?after=c&page=4").Page.Should().Be(4);
    }

    [Fact]
    public void Parse_RelevanceWithoutSearchText_IsDropped()
    {
        MemoryListView.Parse("http://localhost/x?sort=relevance").Sort.Should().BeNull();
        MemoryListView.Parse("http://localhost/x?sort=relevance&q=db").Sort.Should().Be(MemoryFactSort.Relevance);
    }

    [Theory]
    [InlineData(null, null, MemoryFactSort.Recorded)]
    [InlineData("db", null, MemoryFactSort.Relevance)]
    [InlineData(null, MemoryFactSort.Relevance, MemoryFactSort.Recorded)]
    [InlineData("db", MemoryFactSort.Subject, MemoryFactSort.Subject)]
    public void Order_ChosenElseBestMatchWhileSearchingElseNewest(string? text, MemoryFactSort? sort, MemoryFactSort expected)
    {
        new MemoryListView(text, Sort: sort).Order.Should().Be(expected);
    }

    [Fact]
    public void Filter_ToDay_RunsThroughTheEndOfIt()
    {
        MemoryFactFilter filter = new MemoryListView(From: new DateOnly(2026, 3, 1), To: new DateOnly(2026, 3, 1)).Filter;

        filter.From.Should().Be(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        filter.To.Should().BeAfter(new DateTimeOffset(2026, 3, 1, 23, 59, 59, TimeSpan.Zero));
        filter.To.Should().BeBefore(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero));
        filter.Status.Should().Be(MemoryFactStatus.Current);
    }

    [Theory]
    [InlineData(1, 50, null, false, "1–50 of …")]
    [InlineData(2, 50, 4812L, false, "51–100 of 4,812 facts you can see")]
    [InlineData(1, 1, 1L, false, "1–1 of 1 fact you can see")]
    [InlineData(1, 0, 0L, false, "0–0 of 0 facts you can see")]
    [InlineData(3, 50, 10000L, true, "101–150 of 10,000+ facts you can see")]
    public void Range_SaysWhichRowsAndTheTotalOnceKnown(int page, int rows, long? count, bool capped, string expected)
    {
        var view = new MemoryListView(Cursor: page > 1 ? "c" : null, Page: page);

        view.Range(rows, count is { } n ? new MemoryFactCount(n, capped) : null).Should().Be(expected);
    }
}
