using System.Text;
using Connapse.Core;
using Connapse.Ingestion.Tables;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Tables;

[Trait("Category", "Unit")]
public class XmlYamlTableReaderTests
{
    private sealed record Read(IReadOnlyList<TableLoad> Tables, IReadOnlyList<string> Warnings, Dictionary<string, List<IReadOnlyList<object?>>> Rows)
    {
        public IEnumerable<string> Names => Tables.Select(t => t.Name);

        public IEnumerable<string> Columns(string name) => Tables.Single(t => t.Name == name).Columns.Select(c => c.Name);

        public TableColumnType Type(string name, string column) => Tables.Single(t => t.Name == name).Columns.Single(c => c.Name == column).Type;

        public List<IReadOnlyList<object?>> RowsOf(string name) => Rows[name];
    }

    private static async Task<Read> ReadAsync(string text, string fileName)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        using TableFile file = await TableFiles.ReadAsync(stream, fileName, documentId: null);
        Dictionary<string, List<IReadOnlyList<object?>>> rows = [];
        foreach (TableLoad table in file.Tables)
        {
            List<IReadOnlyList<object?>> list = [];
            await foreach (IReadOnlyList<object?> row in table.Rows)
                list.Add(row);
            rows[table.Name] = list;
        }
        return new Read(file.Tables, file.Warnings, rows);
    }

    [Fact]
    public async Task ReadAsync_XmlRepeatedElements_BecomeRowsWithAttributesAndChildTables()
    {
        Read read = await ReadAsync("""
            <?xml version="1.0"?>
            <catalog xmlns="urn:books">
              <book id="b1"><title>Dune</title><price>9.5</price><tag>sf</tag><tag>classic</tag></book>
              <book id="b2"><title>Emma</title><price>7</price><tag>romance</tag></book>
            </catalog>
            """, "catalog.xml");

        read.Names.Should().Equal("catalog.book", "catalog.book.tag");
        read.Columns("catalog.book").Should().Equal("_row", "_index", "@id", "title", "price");
        read.Type("catalog.book", "price").Should().Be(TableColumnType.Number);
        read.RowsOf("catalog.book").Select(r => (r[2], r[3], r[4])).Should().Equal(("b1", (object)"Dune", (object)9.5m), ("b2", "Emma", 7m));
        read.RowsOf("catalog.book.tag").Select(r => (r[0], r[2])).Should().Equal((1L, (object)"sf"), (1L, "classic"), (2L, "romance"));
    }

    [Fact]
    public async Task ReadAsync_XmlTextBesideAttributesAndMixedContent_KeepEveryWord()
    {
        Read read = await ReadAsync("""
            <notes>
              <note lang="en">Hello <b>there</b> friend</note>
              <note lang="fr"><![CDATA[Salut]]></note>
              <amount currency="EUR">5</amount>
            </notes>
            """, "notes.xml");

        read.Names.Should().Equal("notes", "notes.note");
        read.Columns("notes").Should().Equal("amount.@currency", "amount.#text");
        read.RowsOf("notes").Single().Should().Equal("EUR", 5L);
        read.Columns("notes.note").Should().Equal("_index", "@lang", "#text");
        read.RowsOf("notes.note").Select(r => (r[1], r[2])).Should().Equal(("en", (object)"Hello there friend"), ("fr", "Salut"));
    }

    [Fact]
    public async Task ReadAsync_XmlWithEntityDeclarations_NeitherExpandsNorReadsFiles()
    {
        string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE r [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;"><!ENTITY f SYSTEM "file:///c:/windows/win.ini">]>
            <r><i>&b;</i><i>&f;</i></r>
            """;

        Func<Task> read = () => ReadAsync(xml, "bomb.xml");

        await read.Should().ThrowAsync<System.Xml.XmlException>();
    }

    [Fact]
    public async Task ReadAsync_XmlWithoutRepeats_StaysTextOnly()
    {
        Read read = await ReadAsync("<config><port>80</port><host>a</host></config>", "app.xml");

        read.Tables.Should().BeEmpty();
        read.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_YamlList_LoadsWithCoreSchemaScalarsAndMergeKeys()
    {
        Read read = await ReadAsync("""
            defaults: &d
              region: eu
              active: true
            servers:
              - <<: *d
                name: a
                ports: [80, 443]
              - <<: *d
                name: b
                region: us
                active: no
                ports: []
                note: ~
            """, "fleet.yaml");

        read.Names.Should().Equal("fleet", "fleet.servers", "fleet.servers.ports");
        read.Columns("fleet.servers").Should().Equal("_row", "_index", "name", "region", "active", "note");
        read.RowsOf("fleet.servers").Select(r => (r[2], r[3], r[4])).Should().Equal(("a", (object)"eu", (object)"true"), ("b", "us", "no"));
        read.Type("fleet.servers", "active").Should().Be(TableColumnType.Text);
        read.RowsOf("fleet.servers.ports").Select(r => (r[0], r[2])).Should().Equal((1L, (object)80L), (1L, 443L));
    }

    [Fact]
    public async Task ReadAsync_YamlDocumentStream_LoadsOneRowPerDocument()
    {
        Read read = await ReadAsync("id: 1\nok: true\n---\nid: 2\nok: false\n---\n", "events.yml");

        read.Names.Should().Equal("events");
        read.Columns("events").Should().Equal("id", "ok");
        read.Type("events", "ok").Should().Be(TableColumnType.Boolean);
        read.RowsOf("events").Should().BeEquivalentTo(new[] { new object?[] { 1L, true }, new object?[] { 2L, false } }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task ReadAsync_YamlTopLevelList_LoadsOneRowPerItem()
    {
        Read read = await ReadAsync("- name: a\n  port: 80\n- name: b\n  port: 443\n", "fleet.yaml");

        read.Names.Should().Equal("fleet");
        read.Columns("fleet").Should().Equal("name", "port");
        read.RowsOf("fleet").Select(r => (r[0], r[1])).Should().Equal(("a", (object)80L), ("b", 443L));
    }

    [Fact]
    public async Task ReadAsync_YamlAliasBomb_IsRefusedAsUnreadable()
    {
        var yaml = new StringBuilder("a: &a [x, x, x, x, x, x, x, x, x, x]\n");
        for (int i = 1; i < 9; i++)
            yaml.Append($"{(char)('a' + i)}: &{(char)('a' + i)} [{string.Join(", ", Enumerable.Repeat($"*{(char)('a' + i - 1)}", 10))}]\n");

        Func<Task> read = () => ReadAsync(yaml.ToString(), "bomb.yaml");

        await read.Should().ThrowAsync<InvalidDataException>().WithMessage("*more than*values*");
    }

    [Fact]
    public async Task ReadAsync_MalformedYaml_IsRefusedAsUnreadable()
    {
        Func<Task> read = () => ReadAsync("a: [1, 2\nb: }", "bad.yaml");

        await read.Should().ThrowAsync<InvalidDataException>();
    }
}
