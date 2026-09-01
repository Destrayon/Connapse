using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;

namespace Connapse.Storage.CloudScope.RolesAnywhere;

/// <summary>Temporary credentials from a CreateSession call, and when they expire.</summary>
public sealed record RolesAnywhereSession(ImmutableCredentials Credentials, DateTimeOffset Expiration);

/// <summary>A CreateSession response other than 201, surfaced with its status and body for diagnosis.</summary>
public sealed class RolesAnywhereException(int statusCode, string body)
    : Exception($"IAM Roles Anywhere CreateSession failed with HTTP {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Exchanges an X.509 certificate for temporary AWS credentials via IAM Roles Anywhere, signing the
/// request itself (no aws_signing_helper binary). The <see cref="HttpClient"/> is injected so the
/// caller owns its lifetime and so tests can stub the transport.
/// </summary>
public sealed class RolesAnywhereClient(HttpClient httpClient)
{
    public async Task<RolesAnywhereSession> CreateSessionAsync(
        X509Certificate2 certificate,
        RolesAnywhereParameters parameters,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        RolesAnywhereSigner.SignedSessionRequest signed = RolesAnywhereSigner.Sign(certificate, parameters, now);

        using var request = new HttpRequestMessage(HttpMethod.Post, signed.Url);

        // Content-Type must be exactly "application/json" — StringContent's (body, encoding, media)
        // overload appends "; charset=utf-8", which would not match the signed header value.
        var content = new StringContent(signed.JsonBody, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;

        foreach (KeyValuePair<string, string> header in signed.Headers)
        {
            if (header.Key is "content-type" or "host")
            {
                continue; // content-type is on the content; host is set by HttpClient from the URL.
            }
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using HttpResponseMessage response = await httpClient.SendAsync(request, ct);
        string responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new RolesAnywhereException((int)response.StatusCode, responseBody);
        }

        return Parse(responseBody);
    }

    private static RolesAnywhereSession Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement credentials = document.RootElement
            .GetProperty("credentialSet")[0]
            .GetProperty("credentials");

        string accessKeyId = credentials.GetProperty("accessKeyId").GetString()!;
        string secretAccessKey = credentials.GetProperty("secretAccessKey").GetString()!;
        string sessionToken = credentials.GetProperty("sessionToken").GetString()!;
        DateTimeOffset expiration = credentials.GetProperty("expiration").GetDateTimeOffset();

        return new RolesAnywhereSession(
            new ImmutableCredentials(accessKeyId, secretAccessKey, sessionToken), expiration);
    }
}
