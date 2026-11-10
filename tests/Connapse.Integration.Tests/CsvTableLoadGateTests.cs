using System.Diagnostics;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Tables;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Connapse.Integration.Tests;

/// <summary>
/// CSV tables' speed gate (CON-302): a 100,000-row, five-column CSV is read, typed and binary-copied into its table in
/// under 15 s. The first, smaller load warms the pool and the JIT and isn't timed. The category runs in a CI step of
/// its own, beside no other tests.
/// </summary>
[Trait("Category", "Gate")]
[Collection("Integration Tests")]
public class CsvTableLoadGateTests(SharedWebAppFixture fixture, ITestOutputHelper output)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Load_100kRows_FinishesUnder15Seconds()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
        var created = await containers.CreateAsync(new CreateContainerRequest($"gate-{Guid.NewGuid():N}"[..20], null, ContainerKind.Tables));
        Guid containerId = Guid.Parse(created.Id);
        try
        {
            await LoadAsync(tables, containerId, Csv(1_000));

            byte[] csv = Csv(100_000);
            var timer = Stopwatch.StartNew();
            TableInfo table = await LoadAsync(tables, containerId, csv);
            timer.Stop();

            output.WriteLine($"{table.RowCount} rows ({csv.Length / 1024 / 1024} MB) loaded in {timer.ElapsedMilliseconds} ms");
            table.RowCount.Should().Be(100_000);
            table.Columns.Select(c => c.Type).Should().Equal(
                TableColumnType.WholeNumber, TableColumnType.Text, TableColumnType.Number, TableColumnType.Date, TableColumnType.Boolean);
            timer.Elapsed.Should().BeLessThan(Limit);
        }
        finally
        {
            await containers.DeleteAsync(containerId);
        }
    }

    private static async Task<TableInfo> LoadAsync(ITableStore tables, Guid containerId, byte[] csv)
    {
        await using var stream = new MemoryStream(csv);
        TableLoad load = await CsvTableReader.ReadAsync(stream, "orders.csv", documentId: null);
        return await tables.CreateAsync(containerId, load);
    }

    private static byte[] Csv(int rows)
    {
        var csv = new StringBuilder("id,customer,amount,day,paid\n");
        var start = new DateOnly(2020, 1, 1);
        for (int i = 1; i <= rows; i++)
        {
            csv.Append(i).Append(",customer-").Append(i % 977).Append(',')
               .Append(i % 10_000).Append('.').Append((i % 100).ToString("00")).Append(',')
               .Append(start.AddDays(i % 1_500).ToString("yyyy-MM-dd")).Append(',')
               .Append(i % 3 == 0 ? "true" : "false").Append('\n');
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }
}
