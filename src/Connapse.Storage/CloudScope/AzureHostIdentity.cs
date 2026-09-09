using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Detects the host's managed identity by asking Azure's instance metadata service for a token and
/// reading who the token was issued to. Off Azure the metadata endpoint does not exist and the
/// credential gives up within about a second; a hard cap covers the slow-network case.
/// </summary>
/// <remarks>
/// The token's claims are read without validating its signature. That is fine here: nothing is
/// authorised by them. They only describe the identity so the setup script can name it, and every
/// later call to Azure authenticates for real with a token of its own.
/// </remarks>
public sealed class AzureHostIdentity(ILogger<AzureHostIdentity> logger) : IAzureHostIdentity
{
    private const string ArmScope = "https://management.azure.com/.default";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim gate = new(1, 1);
    private AzureHostIdentityInfo? found;

    /// <summary>How the token is obtained; replaced in tests so no metadata service is needed.</summary>
    internal Func<CancellationToken, Task<string>> AcquireToken { get; init; } = async ct =>
    {
        var credential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        AccessToken token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
        return token.Token;
    };

    public async Task<AzureHostIdentityInfo?> DetectAsync(CancellationToken ct = default)
    {
        if (found is not null) return found;

        await gate.WaitAsync(ct);
        try
        {
            if (found is not null) return found;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                string token = await AcquireToken(timeout.Token);
                found = Describe(token);
                if (found is null)
                    logger.LogWarning("The host's managed identity issued a token, but it carried none of the claims that name the identity");
                return found;
            }
            catch (CredentialUnavailableException)
            {
                // The ordinary answer off Azure: no metadata service, so no identity.
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Could not detect a managed identity on this host");
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reads the identity a managed-identity token describes, or null when the required
    /// claims are missing.</summary>
    public static AzureHostIdentityInfo? Describe(string jwt)
    {
        string[] parts = jwt.Split('.');
        if (parts.Length < 2) return null;

        JsonDocument payload;
        try
        {
            payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }

        using (payload)
        {
            JsonElement root = payload.RootElement;
            string? tenant = Claim(root, "tid");
            string? principal = Claim(root, "oid");
            string? client = Claim(root, "appid");
            if (tenant is null || principal is null || client is null) return null;

            string? resourceId = Claim(root, "xms_mirid");
            return new AzureHostIdentityInfo(
                tenant, principal, client, resourceId,
                SubscriptionFrom(resourceId),
                resourceId?.Contains("/providers/Microsoft.ManagedIdentity/userAssignedIdentities/", StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    /// <summary>The subscription segment of an ARM resource id, or null.</summary>
    public static string? SubscriptionFrom(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return null;
        string[] segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < segments.Length; i++)
        {
            if (segments[i].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(segments[i + 1], out Guid id))
                return id.ToString();
        }
        return null;
    }

    private static string? Claim(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    /// <summary>Encodes a payload the way a token carries it; used by tests to build one.</summary>
    public static string EncodePayload(object payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
        string body = Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}")).TrimEnd('=');
        return $"{header}.{body}.";
    }
}
