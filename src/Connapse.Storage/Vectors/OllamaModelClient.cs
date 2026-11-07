using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connapse.Storage.Vectors;

/// <summary>Asks an Ollama server which models it holds, and has it download one.</summary>
public class OllamaModelClient(IHttpClientFactory httpClients)
{
    public const string HttpClientName = "ollama-models";

    /// <summary>Ollama can go quiet while it retries a stalled download, but not for this long.</summary>
    internal static TimeSpan StallLimit { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Asked before pulling because a pull fetches the model's manifest first, so it would fail offline even for a
    /// model already held.
    /// </summary>
    public virtual async Task<bool> HasModelAsync(string address, string model, CancellationToken ct)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));

        HttpClient client = httpClients.CreateClient(HttpClientName);
        using HttpResponseMessage response = await client.PostAsJsonAsync($"{address.TrimEnd('/')}/api/show", new { model }, limit.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Downloads a model, reporting whole percents. Ollama resumes an interrupted download.</summary>
    /// <exception cref="InvalidOperationException">Ollama reported an error, or the stream ended without success.</exception>
    public virtual async Task PullAsync(string address, string model, Action<int> onPercent, CancellationToken ct)
    {
        HttpClient client = httpClients.CreateClient(HttpClientName);
        using HttpRequestMessage request = new(HttpMethod.Post, $"{address.TrimEnd('/')}/api/pull")
        {
            Content = JsonContent.Create(new { model, stream = true }),
        };

        // The client has no timeout, as a download can take hours, so the wait for headers needs its own limit.
        using CancellationTokenSource headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(StallLimit);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Ollama did not answer the download request for {StallLimit.TotalMinutes:0} minutes.");
        }

        using HttpResponseMessage answered = response;
        response.EnsureSuccessStatusCode();

        await using Stream body = await response.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(body);

        Dictionary<string, (long Completed, long Total)> layers = [];
        int reported = -1;
        while (true)
        {
            using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallLimit);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(stall.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Ollama reported nothing for {StallLimit.TotalMinutes:0} minutes.");
            }

            if (line is null)
                throw new InvalidOperationException("Ollama's download ended without finishing.");
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // An error can arrive here, after a 200 and part of the download.
            PullLine? update = JsonSerializer.Deserialize<PullLine>(line);
            if (update?.Error is { Length: > 0 } error)
                throw new InvalidOperationException(error);
            if (update?.Status == "success")
                return;

            if (update?.Digest is { Length: > 0 } digest && update.Total > 0)
            {
                layers[digest] = (update.Completed, update.Total);
                long total = layers.Values.Sum(l => l.Total);
                int percent = (int)(layers.Values.Sum(l => l.Completed) * 100 / total);
                if (percent > reported)
                {
                    reported = percent;
                    onPercent(percent);
                }
            }
        }
    }

    private sealed record PullLine(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("total")] long Total,
        [property: JsonPropertyName("completed")] long Completed,
        [property: JsonPropertyName("error")] string? Error);
}
