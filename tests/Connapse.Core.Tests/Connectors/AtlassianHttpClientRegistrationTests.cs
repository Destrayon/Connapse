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
/// Sends through the "Atlassian" client exactly as <c>AddConnapseStorage</c> registers it, over a
/// real socket, so the handler's redirect behaviour is what is under test rather than a fake's.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AtlassianHttpClientRegistrationTests
{
    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    [InlineData(302)]
    public async Task RegisteredClient_RedirectResponse_IsNotFollowed(int status)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var paths = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task server = ServeAsync(listener, paths, status, $"http://127.0.0.1:{port}/followed", cts.Token);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConnapseStorage(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused;Username=u;Password=p",
            })
            .Build());
        await using var provider = services.BuildServiceProvider();
        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(AtlassianApiClient.HttpClientName);

        using var response = await http.PostAsync(
            $"http://127.0.0.1:{port}/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["client_secret"] = "s" }),
            cts.Token);

        ((int)response.StatusCode).Should().Be(status);
        response.Headers.Location.Should().NotBeNull();
        cts.Cancel();
        await server.ContinueWith(_ => { }, TaskScheduler.Default);
        lock (paths)
            paths.Should().Equal("/oauth/token");
    }

    // A minimal HTTP/1.1 server: answers every request with a redirect to `location`, and
    // records each request path so a followed redirect would show up as a second entry.
    private static async Task ServeAsync(
        TcpListener listener, List<string> paths, int status, string location, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(ct);
            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            while (await reader.ReadLineAsync(ct) is { Length: > 0 } requestLine)
            {
                int contentLength = 0;
                while (await reader.ReadLineAsync(ct) is { Length: > 0 } header)
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(header["Content-Length:".Length..].Trim());
                }

                char[] body = new char[contentLength];
                int read = 0;
                while (read < contentLength)
                    read += await reader.ReadAsync(body.AsMemory(read), ct);

                lock (paths)
                    paths.Add(requestLine.Split(' ')[1]);

                byte[] answer = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} Redirect\r\nLocation: {location}\r\nContent-Length: 0\r\n\r\n");
                await stream.WriteAsync(answer, ct);
                await stream.FlushAsync(ct);
            }
        }
    }
}
