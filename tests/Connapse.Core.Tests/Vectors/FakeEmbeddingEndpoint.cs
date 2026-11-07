using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Connapse.Core.Tests.Vectors;

/// <summary>
/// An OpenAI-style /embeddings endpoint that answers every input with a vector of <see cref="ReturnedLength"/>
/// numbers and keeps each request body, so a test can see whether "dimensions" was sent.
/// </summary>
internal sealed class FakeEmbeddingEndpoint : HttpMessageHandler
{
    public int ReturnedLength { get; set; } = 1536;

    /// <summary>True answers a request carrying "dimensions" with that many numbers, as Qwen3-Embedding does.</summary>
    public bool HonorsDimensions { get; set; }

    public List<JsonObject> Requests { get; } = [];

    public Uri? LastAddress { get; private set; }

    public PipelineTransport Transport => new HttpClientPipelineTransport(new HttpClient(this));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastAddress = request.RequestUri;
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var json = JsonNode.Parse(body)!.AsObject();
        Requests.Add(json);

        int count = json["input"] is JsonArray inputs ? inputs.Count : 1;
        bool base64 = json["encoding_format"]?.GetValue<string>() == "base64";
        int length = HonorsDimensions && json["dimensions"] is { } asked ? asked.GetValue<int>() : ReturnedLength;
        float[] vector = Enumerable.Repeat(0.5f, length).ToArray();
        object embedding = base64
            ? Convert.ToBase64String(vector.SelectMany(BitConverter.GetBytes).ToArray())
            : vector;

        var response = new
        {
            @object = "list",
            data = Enumerable.Range(0, count).Select(i => new { @object = "embedding", index = i, embedding }),
            model = json["model"]?.GetValue<string>(),
            usage = new { prompt_tokens = 1, total_tokens = 1 },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json"),
        };
    }
}
