using System.Net;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Vectors;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Storage.Tests.Vectors;

[Trait("Category", "Unit")]
public class LocalModelDownloadTests
{
    private const string Address = "http://ollama:11434";
    private const string Model = "nomic-embed-text";

    private sealed class ScriptedOllama(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Paths)
                Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Monitor(EmbeddingSettings value) : IOptionsMonitor<EmbeddingSettings>
    {
        private Action<EmbeddingSettings, string?>? _listener;

        public EmbeddingSettings CurrentValue { get; private set; } = value;

        public EmbeddingSettings Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<EmbeddingSettings, string?> listener)
        {
            _listener = listener;
            return Substitute.For<IDisposable>();
        }

        public void Set(EmbeddingSettings settings)
        {
            CurrentValue = settings;
            _listener?.Invoke(settings, null);
        }
    }

    private static HttpResponseMessage Ndjson(params string[] lines) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join('\n', lines) + "\n", Encoding.UTF8, "application/x-ndjson"),
    };

    private static OllamaModelClient Client(HttpMessageHandler handler)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(OllamaModelClient.HttpClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new OllamaModelClient(factory);
    }

    private static EmbeddingSettings Bundled() => new() { Provider = "Ollama", Model = Model, BaseUrl = Address };

    private static LocalModelDownloadService Service(
        HttpMessageHandler handler, IOptionsMonitor<EmbeddingSettings> settings, string? address = Address, string onOrOff = "on") =>
        new(Client(handler), settings, Options.Create(new LocalModelOptions { Address = address, Switch = onOrOff }),
            NullLogger<LocalModelDownloadService>.Instance)
        {
            RecheckEvery = TimeSpan.FromMinutes(5),
            FirstRetryDelay = TimeSpan.FromMilliseconds(1),
        };

    private static async Task<LocalModelStatus> RunUntil(ILocalModelMonitor service, Func<LocalModelStatus, bool> reached)
    {
        TaskCompletionSource<LocalModelStatus> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (reached(service.Current))
                done.TrySetResult(service.Current);
        }

        service.Changed += Check;
        Check();
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PullAsync_TwoLayers_ReportsPercentOfBothWithoutGoingBackwards()
    {
        ScriptedOllama ollama = new(_ => Ndjson(
            """{"status":"pulling manifest"}""",
            """{"status":"pulling a","digest":"sha256:a","total":100,"completed":50}""",
            """{"status":"pulling b","digest":"sha256:b","total":100,"completed":0}""",
            """{"status":"pulling a","digest":"sha256:a","total":100,"completed":100}""",
            """{"status":"pulling b","digest":"sha256:b","total":100,"completed":100}""",
            """{"status":"success"}"""));
        List<int> percents = [];

        await Client(ollama).PullAsync(Address, Model, percents.Add, CancellationToken.None);

        percents.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        percents.Should().EndWith(100);
    }

    [Fact]
    public async Task PullAsync_ErrorAfterPartOfTheDownload_Throws()
    {
        ScriptedOllama ollama = new(_ => Ndjson(
            """{"status":"pulling a","digest":"sha256:a","total":100,"completed":50}""",
            """{"error":"no space left on device"}"""));

        Func<Task> pull = () => Client(ollama).PullAsync(Address, Model, _ => { }, CancellationToken.None);

        await pull.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no space left*");
    }

    [Fact]
    public async Task PullAsync_StreamEndsWithoutSuccess_Throws()
    {
        ScriptedOllama ollama = new(_ => Ndjson("""{"status":"pulling manifest"}"""));

        Func<Task> pull = () => Client(ollama).PullAsync(Address, Model, _ => { }, CancellationToken.None);

        await pull.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>Hands out some progress, then fails the way a dropped connection does.</summary>
    private sealed class DroppedStream(string before) : MemoryStream(Encoding.UTF8.GetBytes(before))
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int read = await base.ReadAsync(buffer, ct);
            return read > 0 ? read : throw new IOException("The response ended prematurely.");
        }
    }

    [Fact]
    public async Task PullAsync_OllamaNeverAnswers_GivesUpAfterTheStallLimit()
    {
        ScriptedOllamaThatHangs ollama = new();
        TimeSpan limit = OllamaModelClient.StallLimit;
        OllamaModelClient.StallLimit = TimeSpan.FromMilliseconds(50);
        try
        {
            Func<Task> pull = () => Client(ollama).PullAsync(Address, Model, _ => { }, CancellationToken.None);

            await pull.Should().ThrowAsync<InvalidOperationException>().WithMessage("*did not answer*");
        }
        finally
        {
            OllamaModelClient.StallLimit = limit;
        }
    }

    private sealed class ScriptedOllamaThatHangs : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task ExecuteAsync_ConnectionDropsMidDownload_RetriesInsteadOfStopping()
    {
        int pulls = 0;
        ScriptedOllama ollama = new(r => r.RequestUri!.AbsolutePath == "/api/show"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Interlocked.Increment(ref pulls) == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new DroppedStream("""{"status":"pulling a","digest":"sha256:a","total":100,"completed":40}""" + "\n")),
                }
                : Ndjson("""{"status":"success"}"""));
        using LocalModelDownloadService service = Service(ollama, new Monitor(Bundled()));

        await service.StartAsync(CancellationToken.None);
        LocalModelStatus reached = await RunUntil(service, s => s.State == LocalModelState.Ready);
        await service.StopAsync(CancellationToken.None);

        reached.Should().Be(new LocalModelStatus(LocalModelState.Ready, Model));
        pulls.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_ModelMissing_DownloadsItAndBecomesReady()
    {
        ScriptedOllama ollama = new(r => r.RequestUri!.AbsolutePath == "/api/show"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Ndjson(
                """{"status":"pulling a","digest":"sha256:a","total":100,"completed":40}""",
                """{"status":"success"}"""));
        using LocalModelDownloadService service = Service(ollama, new Monitor(Bundled()));
        List<LocalModelStatus> seen = [];
        service.Changed += () => seen.Add(service.Current);

        service.Current.State.Should().Be(LocalModelState.Checking);
        await service.StartAsync(CancellationToken.None);
        LocalModelStatus reached = await RunUntil(service, s => s.State == LocalModelState.Ready);
        await service.StopAsync(CancellationToken.None);

        reached.Should().Be(new LocalModelStatus(LocalModelState.Ready, Model));
        seen.Should().Contain(new LocalModelStatus(LocalModelState.Downloading, Model, 40));
        ollama.Paths.Should().Contain("/api/pull");
    }

    [Fact]
    public async Task ExecuteAsync_ModelAlreadyHeld_NeverPulls()
    {
        ScriptedOllama ollama = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using LocalModelDownloadService service = Service(ollama, new Monitor(Bundled()));

        await service.StartAsync(CancellationToken.None);
        await RunUntil(service, s => s.State == LocalModelState.Ready);
        await service.StopAsync(CancellationToken.None);

        ollama.Paths.Should().NotContain("/api/pull");
    }

    [Fact]
    public async Task ExecuteAsync_KeepsFailing_ReportsFailedThenRecovers()
    {
        int calls = 0;
        ScriptedOllama ollama = new(_ => Interlocked.Increment(ref calls) <= 4
            ? throw new HttpRequestException("connection refused")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using LocalModelDownloadService service = Service(ollama, new Monitor(Bundled()));
        List<LocalModelState> seen = [];
        service.Changed += () => seen.Add(service.Current.State);

        await service.StartAsync(CancellationToken.None);
        await RunUntil(service, s => s.State == LocalModelState.Ready);
        await service.StopAsync(CancellationToken.None);

        seen.Should().Contain(LocalModelState.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_FailsOnceWhileOllamaStarts_DoesNotReportFailed()
    {
        int calls = 0;
        ScriptedOllama ollama = new(_ => Interlocked.Increment(ref calls) == 1
            ? throw new HttpRequestException("connection refused")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using LocalModelDownloadService service = Service(ollama, new Monitor(Bundled()));
        List<LocalModelState> seen = [];
        service.Changed += () => seen.Add(service.Current.State);

        await service.StartAsync(CancellationToken.None);
        await RunUntil(service, s => s.State == LocalModelState.Ready);
        await service.StopAsync(CancellationToken.None);

        seen.Should().NotContain(LocalModelState.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_ModelChangedInSettings_GetsTheNewModel()
    {
        List<string> asked = [];
        ScriptedOllama ollama = new(r =>
        {
            lock (asked)
                asked.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        Monitor settings = new(Bundled());
        using LocalModelDownloadService service = Service(ollama, settings);

        await service.StartAsync(CancellationToken.None);
        await RunUntil(service, s => s.State == LocalModelState.Ready);
        settings.Set(new EmbeddingSettings { Provider = "Ollama", Model = "bge-m3", BaseUrl = Address });
        LocalModelStatus reached = await RunUntil(service, s => s is { State: LocalModelState.Ready, Model: "bge-m3" });
        await service.StopAsync(CancellationToken.None);

        reached.Model.Should().Be("bge-m3");
    }

    [Theory]
    [InlineData(null, "Ollama", Address)] // Not running under Compose.
    [InlineData(Address, "OpenAI", Address)] // A hosted provider.
    [InlineData(Address, "Ollama", "http://gpu-box:11434")] // Somebody's own Ollama isn't ours to download into.
    public async Task ExecuteAsync_NotTheBundledOllama_LeavesEverythingAlone(string? address, string provider, string baseUrl)
    {
        ScriptedOllama ollama = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using LocalModelDownloadService service = Service(
            ollama, new Monitor(new EmbeddingSettings { Provider = provider, Model = Model, BaseUrl = baseUrl }), address);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        service.Current.Should().Be(LocalModelStatus.NotManaged);
        service.Current.HoldsEmbedding.Should().BeFalse();
        ollama.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SwitchedOffWithNoProvider_SaysSoAndNeverCallsOllama()
    {
        ScriptedOllama ollama = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using LocalModelDownloadService service = Service(ollama, new Monitor(Bundled()), onOrOff: "off");

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        service.Current.State.Should().Be(LocalModelState.NoProvider);
        service.Current.Explanation.Should().Contain("Settings");
        ollama.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SwitchedOffThenProviderChosen_StopsHoldingEmbedding()
    {
        Monitor settings = new(Bundled());
        ScriptedOllama ollama = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using LocalModelDownloadService service = Service(ollama, settings, onOrOff: "off");

        await service.StartAsync(CancellationToken.None);
        await RunUntil(service, s => s.State == LocalModelState.NoProvider);
        settings.Set(new EmbeddingSettings { Provider = "OpenAI", Model = "text-embedding-3-small", BaseUrl = Address });
        await RunUntil(service, s => s.State == LocalModelState.NotManaged);
        await service.StopAsync(CancellationToken.None);

        service.Current.HoldsEmbedding.Should().BeFalse();
    }
}
