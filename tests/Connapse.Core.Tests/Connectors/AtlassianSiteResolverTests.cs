using System.Net;
using System.Net.Sockets;
using System.Text;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Storage.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// The tenant_info lookup through the client the app really registers, against a real socket: a
/// redirect must not be followed, because only the address the administrator's input was checked
/// as may be dialled.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AtlassianSiteResolverTests
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task ResolveCloudIdAsync_RegisteredClient_DoesNotFollowARedirect()
    {
        using var server = new TinyServer(path => path == "/_edge/tenant_info"
            ? "HTTP/1.1 302 Found\r\nLocation: /elsewhere/_edge/tenant_info\r\nContent-Length: 0\r\n\r\n"
            : Ok($"{{\"cloudId\":\"{CloudId}\"}}"));
        using var http = RegisteredFactory().CreateClient(AtlassianSiteResolver.HttpClientName);

        string? cloudId = await AtlassianSiteResolver.ResolveCloudIdAsync(http, server.BaseUrl, CancellationToken.None);

        cloudId.Should().BeNull();
        server.Paths.Should().Equal("/_edge/tenant_info");
    }

    [Fact]
    public async Task ResolveCloudIdAsync_RegisteredClient_ReadsADirectAnswer()
    {
        using var server = new TinyServer(_ => Ok($"{{\"cloudId\":\"{CloudId}\"}}"));
        using var http = RegisteredFactory().CreateClient(AtlassianSiteResolver.HttpClientName);

        string? cloudId = await AtlassianSiteResolver.ResolveCloudIdAsync(http, server.BaseUrl, CancellationToken.None);

        cloudId.Should().Be(CloudId);
    }

    private static string Ok(string json) =>
        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";

    private static IHttpClientFactory RegisteredFactory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddConnapseStorage(configuration);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    /// <summary>Answers each HTTP/1.1 request on a loopback socket with whatever <c>respond</c> returns for its path.</summary>
    private sealed class TinyServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string, string> _respond;
        private readonly List<string> _paths = [];

        public TinyServer(Func<string, string> respond)
        {
            _respond = respond;
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public IReadOnlyList<string> Paths { get { lock (_paths) return [.. _paths]; } }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (true)
                {
                    string? requestLine = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(requestLine))
                        return;
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

                    string path = requestLine.Split(' ')[1];
                    lock (_paths) _paths.Add(path);
                    byte[] response = Encoding.UTF8.GetBytes(_respond(path));
                    await stream.WriteAsync(response);
                    await stream.FlushAsync();
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
