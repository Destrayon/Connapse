using System.Text;
using FluentAssertions;

namespace Connapse.Integration.Tests.Spec;

/// <summary>Guards the spec check behind IntegrationSuite_EveryResponse_ConformsToCommittedSpec against passing because it checks nothing.</summary>
[Trait("Category", "Unit")]
public sealed class CommittedSpecTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"spec-{Guid.NewGuid():N}.json");

    public CommittedSpecTests() => File.WriteAllText(_path, """
        {
          "openapi": "3.1.1",
          "paths": {
            "/api/things/{id}": {
              "get": {
                "responses": {
                  "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Thing" } } } },
                  "4XX": { "content": { "application/problem+json": { "schema": { "type": "object", "required": ["title"] } } } }
                }
              }
            },
            "/api/shapes": {
              "post": {
                "responses": {
                  "201": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Shape" } } } },
                  "204": { "description": "No Content" }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "Thing": { "type": "object", "required": ["id"], "properties": { "id": { "type": "string" } } },
              "Shape": {
                "anyOf": [ { "$ref": "#/components/schemas/Square" } ],
                "discriminator": { "propertyName": "type", "mapping": { "square": "#/components/schemas/Square" } }
              },
              "Square": { "type": "object", "required": ["type", "side"], "properties": { "type": { "const": "square" }, "side": { "type": "number" } } }
            }
          }
        }
        """);

    public void Dispose() => File.Delete(_path);

    private string? Check(string method, string route, int status, string? contentType, string body) =>
        CommittedSpec.Load(_path).Check(CommittedSpec.Key(method, route), status, contentType, Encoding.UTF8.GetBytes(body), out _);

    [Fact]
    public void Check_BodyMatchingItsSchema_Conforms() =>
        Check("GET", "/api/things/{id:guid}", 200, "application/json; charset=utf-8", """{"id":"a"}""").Should().BeNull();

    [Fact]
    public void Check_BodyBreakingItsSchema_IsReported() =>
        Check("GET", "/api/things/{id:guid}", 200, "application/json", """{"id":1}""").Should().Be("body doesn't match its schema");

    [Fact]
    public void Check_StatusTheSpecDoesNotList_IsReported() =>
        Check("GET", "/api/things/{id}", 500, "application/json", "{}").Should().Be("status not in the spec");

    [Fact]
    public void Check_StatusRange_CoversItsStatuses()
    {
        Check("GET", "/api/things/{id}", 404, "application/problem+json", """{"title":"Not found"}""").Should().BeNull();
        Check("GET", "/api/things/{id}", 404, "application/problem+json", """{"detail":"no title"}""").Should().Be("body doesn't match its schema");
    }

    [Fact]
    public void Check_RouteTheSpecDoesNotList_IsReported() =>
        Check("GET", "/api/other", 200, "application/json", "{}").Should().Be("not in the spec");

    [Fact]
    public void Check_JsonBodyOnAResponseWithoutContent_IsReported() =>
        Check("POST", "/api/shapes/", 204, "application/json", "{}").Should().Be("body not described");

    [Fact]
    public void Check_DiscriminatedSchema_ValidatesTheChosenShape()
    {
        Check("POST", "/api/shapes", 201, "application/json", """{"type":"square","side":2}""").Should().BeNull();
        Check("POST", "/api/shapes", 201, "application/json", """{"type":"square","side":"two"}""").Should().Be("body doesn't match its schema");
    }

    [Theory]
    [InlineData("/api/sources/{sourceId:guid}", "GET /api/sources/{sourceId}")]
    [InlineData("/api/sources/", "GET /api/sources")]
    [InlineData("/api/files/{*path}", "GET /api/files/{path}")]
    [InlineData("/api/items/{id?}", "GET /api/items/{id}")]
    public void Key_SpellsRoutesAsTheSpecDoes(string route, string key) =>
        CommittedSpec.Key("get", route).Should().Be(key);
}
