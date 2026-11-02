using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class PermissionTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["eng/README.md"] = "# Engineering",
        ["eng/design/architecture.md"] = "# Architecture",
        ["eng/design/secret.md"] = "# Secret",
    };

    [Fact]
    public async Task SearchResource_LinkedUser_ReturnsTheirKeysInThatScope()
    {
        using ExampleServer server = new(files: Files);

        JsonElement root = await PostJson(server, "/access/v1/search/resource", KeySearch(ExampleServer.AliceId, "eng"));

        root.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("id").GetString())
            .Should().Equal("folder:eng/", "folder:eng/design");
        root.GetProperty("results")[0].GetProperty("type").GetString().Should().Be("access_key");
        root.GetProperty("page").GetProperty("next_token").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task SearchResource_LimitOne_PagesWithNextToken()
    {
        using ExampleServer server = new(files: Files);

        JsonElement first = await PostJson(server, "/access/v1/search/resource", KeySearch(ExampleServer.AliceId, "eng", limit: 1));
        string token = first.GetProperty("page").GetProperty("next_token").GetString()!;
        JsonElement second = await PostJson(server, "/access/v1/search/resource", KeySearch(ExampleServer.AliceId, "eng", token, 1));

        first.GetProperty("results")[0].GetProperty("id").GetString().Should().Be("folder:eng/");
        token.Should().NotBeEmpty();
        second.GetProperty("results")[0].GetProperty("id").GetString().Should().Be("folder:eng/design");
        second.GetProperty("page").GetProperty("next_token").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task SearchResource_UnlinkedUser_ReturnsNoKeys()
    {
        using ExampleServer server = new(files: Files);

        JsonElement root = await PostJson(server, "/access/v1/search/resource", KeySearch("connapse-stranger", "eng"));

        root.GetProperty("results").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task SearchResource_WrongResourceType_Returns400()
    {
        using ExampleServer server = new(files: Files);
        object body = new
        {
            subject = new { type = "user", id = ExampleServer.AliceId },
            action = new { name = "read" },
            resource = new { type = "document", properties = new { scope = "eng" } },
        };

        HttpResponseMessage response = await server.Client().PostAsJsonAsync("/access/v1/search/resource", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Evaluations_ReturnOneDecisionPerItemInOrder()
    {
        using ExampleServer server = new(files: Files);

        JsonElement root = await PostJson(server, "/access/v1/evaluations",
            Check(ExampleServer.AliceId, "eng", "README.md", "design/architecture.md", "design/secret.md"));

        Decisions(root).Should().Equal(true, true, false);
    }

    [Fact]
    public async Task Evaluations_UserWithoutTheKey_IsDenied()
    {
        using ExampleServer server = new(files: Files);

        JsonElement root = await PostJson(server, "/access/v1/evaluations",
            Check(ExampleServer.BobId, "eng", "README.md", "design/architecture.md"));

        Decisions(root).Should().Equal(true, false);
    }

    [Fact]
    public async Task Evaluations_UnknownDocumentWrongTypeOrUnlinkedUser_AreFalse()
    {
        using ExampleServer server = new(files: Files);
        object body = new
        {
            subject = new { type = "user", id = ExampleServer.AliceId },
            action = new { name = "read" },
            evaluations = new object[]
            {
                new { resource = new { type = "document", id = DocumentId.Encode("missing.md"), properties = new { scope = "eng" } } },
                new { resource = new { type = "folder", id = DocumentId.Encode("README.md"), properties = new { scope = "eng" } } },
                new { resource = new { type = "document", id = DocumentId.Encode("README.md") } },
            },
        };

        JsonElement mixed = await PostJson(server, "/access/v1/evaluations", body);
        JsonElement stranger = await PostJson(server, "/access/v1/evaluations", Check("connapse-stranger", "eng", "README.md"));

        Decisions(mixed).Should().Equal(false, false, false);
        Decisions(stranger).Should().Equal(false);
    }

    [Fact]
    public async Task Evaluations_OverTheBatchLimit_Returns400()
    {
        using ExampleServer server = new(ExampleServer.DefaultConfig() with { MaxCheckBatch = 2 }, Files);

        HttpResponseMessage response = await server.Client().PostAsJsonAsync("/access/v1/evaluations",
            Check(ExampleServer.AliceId, "eng", "README.md", "design/architecture.md", "design/secret.md"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Permissions_ContentOnlyConfig_Return404()
    {
        using ExampleServer server = new(ExampleServer.DefaultConfig() with { PermissionsEnabled = false }, Files);

        HttpResponseMessage response = await server.Client().PostAsJsonAsync("/access/v1/evaluations",
            Check(ExampleServer.AliceId, "eng", "README.md"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Permissions_WithoutToken_Return401()
    {
        using ExampleServer server = new(files: Files);

        HttpResponseMessage response = await server.Client(authorized: false).PostAsJsonAsync("/access/v1/evaluations",
            Check(ExampleServer.AliceId, "eng", "README.md"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    internal static object KeySearch(string userId, string scope, string token = "", int limit = 100) => new
    {
        subject = new { type = "user", id = userId },
        action = new { name = "read" },
        resource = new { type = "access_key", properties = new { scope } },
        page = new { token, limit },
    };

    private static object Check(string userId, string scope, params string[] paths) => new
    {
        subject = new { type = "user", id = userId },
        action = new { name = "read" },
        evaluations = paths.Select(p => new
        {
            resource = new { type = "document", id = DocumentId.Encode(p), properties = new { scope } },
        }).ToArray(),
    };

    private static IEnumerable<bool> Decisions(JsonElement root) =>
        root.GetProperty("evaluations").EnumerateArray().Select(e => e.GetProperty("decision").GetBoolean());

    internal static async Task<JsonElement> PostJson(ExampleServer server, string url, object body)
    {
        HttpResponseMessage response = await server.Client().PostAsJsonAsync(url, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Evaluations_NullOrWronglyTypedItem_DecidesFalseForThatItemOnly()
    {
        using ExampleServer server = new(files: Files);
        string readme = DocumentId.Encode("README.md");
        string body = $$"""
            {
              "subject": { "type": "user", "id": "{{ExampleServer.AliceId}}" },
              "action": { "name": "read" },
              "evaluations": [
                null,
                { "resource": { "type": "document", "id": 5, "properties": { "scope": "eng" } } },
                { "resource": { "type": "document", "id": "{{readme}}", "properties": { "scope": "eng" } } }
              ]
            }
            """;

        HttpResponseMessage response = await server.Client().PostAsync("/access/v1/evaluations",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Decisions(root).Should().Equal(false, false, true);
    }
}
