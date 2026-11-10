using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace Connapse.Integration.Tests;

[Collection("Integration Tests")]
[Trait("Category", "Integration")]
public class OAuthEndpointTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task ProtectedResourceMetadata_ReturnsValidJson()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-protected-resource");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        json!.RootElement.GetProperty("resource").GetString().Should().NotBeNullOrWhiteSpace();
        json.RootElement.GetProperty("authorization_servers").GetArrayLength().Should().BeGreaterThan(0);
        json.RootElement.GetProperty("scopes_supported").GetArrayLength().Should().Be(2);
    }

    /// <summary>
    /// RFC 9728 §3.1: clients may discover PRM by inserting
    /// "/.well-known/oauth-protected-resource" between host and path. RFC 9728
    /// §3.3 then requires the "resource" claim in the document to equal the
    /// protected resource identifier. Strict MCP clients (Claude Code among
    /// them) reject the document if this doesn't match the URL they are
    /// actually trying to access.
    /// </summary>
    [Theory]
    [InlineData("/mcp")]
    [InlineData("/khastra/mcp")]
    [InlineData("/some/deep/nested/path")]
    public async Task ProtectedResourceMetadata_PathSuffixed_ResourceMatchesRequestedPath(string resourcePath)
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync($"/.well-known/oauth-protected-resource{resourcePath}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var resource = json!.RootElement.GetProperty("resource").GetString();
        resource.Should().NotBeNullOrWhiteSpace();
        resource.Should().EndWith(resourcePath, "the resource claim must equal the URL the client discovered metadata for");
        json.RootElement.GetProperty("authorization_servers").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AuthorizationServerMetadata_ReturnsValidJson()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-authorization-server");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var root = json!.RootElement;
        root.GetProperty("issuer").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("authorization_endpoint").GetString().Should().Contain("/oauth/authorize");
        root.GetProperty("token_endpoint").GetString().Should().Contain("/oauth/token");
        root.GetProperty("registration_endpoint").GetString().Should().Contain("/oauth/register");
        root.GetProperty("code_challenge_methods_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("S256");
        root.GetProperty("client_id_metadata_document_supported").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Register_ValidRequest_Returns201WithClientId()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Test Client",
            redirect_uris = new[] { "http://127.0.0.1:3000/callback" },
            application_type = "native",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        json!.RootElement.GetProperty("client_id").GetString().Should().NotBeNullOrWhiteSpace();
        json.RootElement.GetProperty("client_name").GetString().Should().Be("Test Client");
    }

    [Fact]
    public async Task Register_InvalidRedirectUri_Returns400()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Bad Client",
            redirect_uris = new[] { "ftp://evil.com" },
            application_type = "native",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("{not json", "application/json")]
    [InlineData("", "application/json")]
    [InlineData("client_name=x", "application/x-www-form-urlencoded")]
    public async Task Register_UnreadableBody_Returns400InvalidClientMetadata(string body, string contentType)
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsync("/oauth/register", new StringContent(body, System.Text.Encoding.UTF8, contentType));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        json!.RootElement.GetProperty("error").GetString().Should().Be("invalid_client_metadata");
    }

    [Fact]
    public async Task Register_NoRedirectUris_Returns400()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "No Redirects",
            application_type = "native",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_NoClientName_Returns400()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/oauth/register", new
        {
            redirect_uris = new[] { "http://127.0.0.1:3000/callback" },
            application_type = "native",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TokenEndpoint_InvalidGrant_Returns400()
    {
        using var client = fixture.Factory.CreateClient();

        var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "invalid-code",
            ["redirect_uri"] = "http://127.0.0.1:3000/callback",
            ["client_id"] = "test",
            ["code_verifier"] = "test-verifier",
        });

        var response = await client.PostAsync("/oauth/token", formContent);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        json!.RootElement.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public async Task TokenEndpoint_UnsupportedGrantType_Returns400()
    {
        using var client = fixture.Factory.CreateClient();

        var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
        });

        var response = await client.PostAsync("/oauth/token", formContent);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        json!.RootElement.GetProperty("error").GetString().Should().Be("unsupported_grant_type");
    }

    public static TheoryData<string, string> UnreadableTokenBodies() => new()
    {
        // More fields than the form reader allows (1,024).
        { string.Join('&', Enumerable.Range(0, 2_000).Select(i => $"k{i}=v")), "application/x-www-form-urlencoded" },
        // One value longer than the form reader allows (4 MB).
        { "grant_type=refresh_token&refresh_token=" + new string('a', 5 * 1024 * 1024), "application/x-www-form-urlencoded" },
        // A form type the token endpoint does not take, without the boundary it would need.
        { "grant_type=refresh_token", "multipart/form-data" },
        // A multipart body cut off before its closing boundary.
        { "--b\r\nContent-Disposition: form-data; name=\"grant_type\"\r\n\r\nrefresh_token", "multipart/form-data; boundary=b" },
        { "{\"grant_type\":\"refresh_token\"}", "application/json" },
    };

    [Theory]
    [MemberData(nameof(UnreadableTokenBodies))]
    public async Task TokenEndpoint_UnreadableBody_Returns400InvalidRequest(string body, string contentType)
    {
        using var client = fixture.Factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        var response = await client.PostAsync("/oauth/token", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        json!.RootElement.GetProperty("error").GetString().Should().Be("invalid_request");
    }

    [Fact]
    public async Task TokenEndpoint_BodyReadFailsMidway_Returns400InvalidRequest()
    {
        HttpContext context = await fixture.Factory.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Post;
            ctx.Request.Path = "/oauth/token";
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Body = new FailingMidBodyStream("grant_type=refresh_token&refresh_token=abc");
        });

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.ContentType.Should().StartWith("application/json");
        using JsonDocument json = await JsonDocument.ParseAsync(context.Response.Body);
        json.RootElement.GetProperty("error").GetString().Should().Be("invalid_request");
    }

    // Yields the start of a form body, then fails the way a dropped connection does.
    private sealed class FailingMidBodyStream(string start) : Stream
    {
        private readonly byte[] _start = Encoding.UTF8.GetBytes(start);
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _start.Length)
                throw new IOException("The connection dropped mid-body.");
            int n = Math.Min(buffer.Length, _start.Length - _position);
            _start.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CliMetadataDocument_ReturnsValidJson()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/oauth/clients/cli.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        json!.RootElement.GetProperty("client_id").GetString().Should().Contain("/oauth/clients/cli.json");
        json.RootElement.GetProperty("client_name").GetString().Should().Be("Connapse CLI");
    }

    /// <summary>
    /// With AllowAnonymousDiscovery=false (default), an unauthenticated POST to /mcp
    /// should return 401. The MCP SDK uses Streamable HTTP transport (POST-based),
    /// so we use the same POST approach as McpDiscoveryTests.
    /// </summary>
    [Fact]
    public async Task McpEndpoint_NoAuth_Returns401WithWwwAuthenticate()
    {
        using var client = fixture.Factory.CreateClient();

        var json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-11-05",
                capabilities = new { },
                clientInfo = new { name = "oauth-test", version = "1.0.0" }
            },
            id = "1"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().NotBeEmpty();
        var wwwAuth = response.Headers.WwwAuthenticate.ToString();
        wwwAuth.Should().Contain("Bearer");
        wwwAuth.Should().Contain("resource_metadata");
        // The challenge must point at the path-suffixed PRM URL for the
        // actual resource (/mcp), not the bare /.well-known endpoint. This is
        // what lets strict MCP clients resolve metadata whose "resource"
        // claim matches the URL they are trying to access.
        wwwAuth.Should().Contain(".well-known/oauth-protected-resource/mcp");
    }
}
