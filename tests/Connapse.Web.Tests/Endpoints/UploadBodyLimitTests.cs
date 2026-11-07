using Connapse.Core;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Web.Tests.Endpoints;

[Trait("Category", "Unit")]
public sealed class UploadBodyLimitTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task UseUploadBodyLimit_MoreRefusalsThanDrainSlots_ExtraOneLeavesItsBodyUnread()
    {
        ServiceProvider services = Services();
        Endpoint endpoint = UploadEndpoint();
        RequestDelegate pipeline = new ApplicationBuilder(services).UseUploadBodyLimit().Build();
        using CancellationTokenSource clientsLeave = new();

        List<Task> draining = [];
        for (int i = 0; i < UploadBodyLimit.MaxConcurrentDrains; i++)
        {
            HangingBody body = new();
            draining.Add(pipeline(RefusedRequest(services, endpoint, body, clientsLeave.Token)));
            await body.Reading.WaitAsync(Patience);
        }

        HangingBody extraBody = new();
        HttpContext extra = RefusedRequest(services, endpoint, extraBody, CancellationToken.None);
        await pipeline(extra).WaitAsync(Patience);

        extra.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        extraBody.Reading.IsCompleted.Should().BeFalse();

        await clientsLeave.CancelAsync();
        await Task.WhenAll(draining).WaitAsync(Patience);

        using CancellationTokenSource lastClientLeaves = new();
        HangingBody lastBody = new();
        Task last = pipeline(RefusedRequest(services, endpoint, lastBody, lastClientLeaves.Token));
        await lastBody.Reading.WaitAsync(Patience);
        await lastClientLeaves.CancelAsync();
        await last.WaitAsync(Patience);
    }

    private static ServiceProvider Services()
    {
        IOptionsMonitor<UploadSettings> settings = Substitute.For<IOptionsMonitor<UploadSettings>>();
        settings.CurrentValue.Returns(new UploadSettings());
        return new ServiceCollection().AddLogging().AddSingleton(settings).BuildServiceProvider();
    }

    private static Endpoint UploadEndpoint()
    {
        WebApplication app = WebApplication.CreateSlimBuilder().Build();
        app.MapPost("/upload", () => Results.Ok()).WithUploadBodyLimit();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).Single();
    }

    /// <summary>States a body over the limit that a drain would still read.</summary>
    private static DefaultHttpContext RefusedRequest(IServiceProvider services, Endpoint endpoint, Stream body, CancellationToken clientLeaves)
    {
        DefaultHttpContext context = new() { RequestServices = services, RequestAborted = clientLeaves };
        context.Request.ContentLength = UploadBodyLimit.MaxDrainBytes;
        context.Request.Body = body;
        context.SetEndpoint(endpoint);
        return context;
    }

    /// <summary>A body whose client has stopped sending, so a drain reading it holds its slot until the client leaves.</summary>
    private sealed class HangingBody : MemoryStream
    {
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reading => _reading.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
