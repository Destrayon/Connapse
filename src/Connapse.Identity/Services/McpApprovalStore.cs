using System.Security.Cryptography;
using System.Text;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Identity.Services;

/// <summary>How deciding on an approval request went.</summary>
public enum McpApprovalDecision
{
    Decided,
    NotFound,
    Forbidden,
    NotPending,
}

/// <summary>High-impact MCP calls waiting for a person, and the approvals that let them run once (#782).</summary>
public interface IMcpApprovalStore
{
    /// <summary>
    /// The pending request for this call, made now unless one is already waiting. <paramref name="arguments"/> are
    /// the call's real canonical arguments and <paramref name="redactedArguments"/> the same with secrets hidden:
    /// only the redacted ones are stored readable.
    /// </summary>
    Task<McpApprovalEntity> RequestAsync(string tokenKey, Guid ownerUserId, string toolName, string arguments, string redactedArguments, CancellationToken ct = default);

    /// <summary>Whether an approval for exactly this call is waiting; if so, it is used up and the call may run.</summary>
    Task<bool> TryConsumeAsync(string tokenKey, string toolName, string arguments, string redactedArguments, CancellationToken ct = default);

    /// <summary>Requests still waiting: a person's own, or everyone's for an administrator.</summary>
    Task<IReadOnlyList<McpApprovalEntity>> ListPendingAsync(Guid userId, bool isAdmin, CancellationToken ct = default);

    /// <summary>Approves or denies a waiting request; only its owner or an administrator may.</summary>
    Task<McpApprovalDecision> DecideAsync(Guid id, Guid userId, bool isAdmin, bool approve, CancellationToken ct = default);

    /// <summary>
    /// Empties expired requests and runs <see cref="McpApprovalStore.HideUnredactedSecrets"/>, on a schedule rather
    /// than only when a request is made, so nothing readable outlives its use while MCP traffic is quiet.
    /// </summary>
    Task PurgeAsync(CancellationToken ct = default);
}

/// <summary>
/// Stores approval requests without a readable secret (CON-185): the summary and hash are of the redacted
/// arguments, and when redacting hid something the real arguments are kept only as DataProtection ciphertext,
/// which a retried call must unprotect to exactly. Its own purpose, so no other protector can read them.
/// </summary>
public sealed class McpApprovalStore(
    IDbContextFactory<ConnapseIdentityDbContext> contexts, TimeProvider clock, IDataProtectionProvider dataProtection) : IMcpApprovalStore
{
    /// <summary>How long a request waits for a person.</summary>
    public static readonly TimeSpan PendingFor = TimeSpan.FromDays(1);

    /// <summary>How long an approval may be used once given.</summary>
    public static readonly TimeSpan ApprovedFor = TimeSpan.FromMinutes(15);

    /// <summary>What the stored hash of a request made since CON-185 is taken over, ahead of its redacted arguments.</summary>
    public const string HashPrefix = "mcp-approval.v2\n";

    /// <summary>
    /// Hides what code from before CON-185 stored readable: an unsalted hash of the full arguments and a summary
    /// with secrets in it. The migration that added sealing runs it once, and <see cref="PurgeAsync"/> runs it on a
    /// schedule, so a request an older instance still writes mid-deploy is hidden within minutes. A denied, used or
    /// expired request is never shown or matched again, so its hash and summary go. A pending or approved one keeps
    /// its hash, so approving or running it still works, until the store empties it on deny, use or expiry (a day at
    /// most). A pending one without sealed arguments has its summary replaced when a field McpCallArguments.IsSecret
    /// would match holds anything but null or the placeholder: the pattern is McpCallArguments.SecretNamePattern, run
    /// on the summary lowercased with '_' and '-' dropped as IsSecret does, so a summary the store wrote since CON-185
    /// never matches. Every statement is idempotent.
    /// </summary>
    public const string HideUnredactedSecrets = """
        UPDATE mcp_approvals SET arguments_hash = ''
        WHERE arguments_hash <> '' AND (status NOT IN ('Pending', 'Approved') OR expires_at <= now());

        UPDATE mcp_approvals SET arguments_summary = ''
        WHERE arguments_summary <> '' AND (status <> 'Pending' OR expires_at <= now());

        UPDATE mcp_approvals
        SET arguments_summary = '(hidden: requested before secret fields were redacted. Approving still runs the call; deny it and repeat the call to see its other arguments.)'
        WHERE status = 'Pending' AND expires_at > now() AND arguments_protected IS NULL
          AND regexp_replace(lower(arguments_summary), '[-_]', '', 'g')
              ~ '"[^"]*(?:(?:secret|password|passwd|pwd|passphrase|token|credentials?|apikey|privatekey|connectionstring|authorization|cookie)(?:value|string|text|data|base64|pem)?|(?:secret|private|access|account|signing|session|shared|master|encryption)[^"]*key)":(?!null|"\[hidden\])';
        """;

    private IDataProtector Protector => dataProtection.CreateProtector("McpApproval.v1");

    public async Task<McpApprovalEntity> RequestAsync(string tokenKey, Guid ownerUserId, string toolName, string arguments, string redactedArguments, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var call = new Call(arguments, redactedArguments);

        await ClearExpiredAsync(db, now, ct);

        var candidates = await db.McpApprovals
            .Where(a => a.TokenKey == tokenKey && a.ToolName == toolName && (a.ArgumentsHash == call.Hash || a.ArgumentsHash == call.LegacyHash)
                && a.Status == McpApprovalStatus.Pending && a.ExpiresAt > now)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
        if (candidates.FirstOrDefault(a => Matches(a, call)) is { } waiting)
            return waiting;

        var request = new McpApprovalEntity
        {
            Id = Guid.NewGuid(),
            TokenKey = tokenKey,
            OwnerUserId = ownerUserId,
            ToolName = toolName,
            ArgumentsHash = call.HasSecrets ? call.Hash : call.LegacyHash,
            ArgumentsSummary = redactedArguments.Length <= 2048 ? redactedArguments : redactedArguments[..2045] + "...",
            ArgumentsProtected = call.HasSecrets ? Protector.Protect(arguments) : null,
            Status = McpApprovalStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now + PendingFor,
        };
        db.McpApprovals.Add(request);
        await db.SaveChangesAsync(ct);
        return request;
    }

    public async Task<bool> TryConsumeAsync(string tokenKey, string toolName, string arguments, string redactedArguments, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var call = new Call(arguments, redactedArguments);
        var candidates = await db.McpApprovals.AsNoTracking()
            .Where(a => a.TokenKey == tokenKey && a.ToolName == toolName && (a.ArgumentsHash == call.Hash || a.ArgumentsHash == call.LegacyHash)
                && a.Status == McpApprovalStatus.Approved && a.ExpiresAt > now)
            .OrderBy(a => a.DecidedAt)
            .ToListAsync(ct);
        if (candidates.FirstOrDefault(a => Matches(a, call)) is not { } approved)
            return false;

        // Conditional on still being Approved, so two identical calls racing can't both use one approval. The
        // sealed arguments go with it: a used request never needs them again.
        int used = await db.McpApprovals
            .Where(a => a.Id == approved.Id && a.Status == McpApprovalStatus.Approved)
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.Status, McpApprovalStatus.Used)
                .SetProperty(a => a.UsedAt, now)
                .SetProperty(a => a.ArgumentsHash, "")
                .SetProperty(a => a.ArgumentsProtected, (string?)null), ct);
        return used == 1;
    }

    public async Task PurgeAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await ClearExpiredAsync(db, clock.GetUtcNow().UtcDateTime, ct);
        await db.Database.ExecuteSqlRawAsync(HideUnredactedSecrets, ct);
    }

    // An expired request is never matched again, so its hash (which for one made before CON-185 is of the full
    // arguments, secrets included) and its sealed arguments go.
    private static Task<int> ClearExpiredAsync(ConnapseIdentityDbContext db, DateTime now, CancellationToken ct) =>
        db.McpApprovals
            .Where(a => a.ExpiresAt <= now && (a.ArgumentsHash != "" || a.ArgumentsProtected != null))
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.ArgumentsHash, "")
                .SetProperty(a => a.ArgumentsProtected, (string?)null), ct);

    /// <summary>
    /// Whether a stored request is for exactly this call. One with sealed arguments matches only a call whose real
    /// arguments are those, so two calls that differ only in a secret never share an approval. One without (no
    /// secrets, or made before CON-185) matches by its hash, which for those is the hash of the full arguments.
    /// </summary>
    private bool Matches(McpApprovalEntity request, Call call)
    {
        if (request.ArgumentsProtected is null)
            return request.ArgumentsHash == call.LegacyHash;
        if (request.ArgumentsHash != call.Hash)
            return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Protector.Unprotect(request.ArgumentsProtected)), Encoding.UTF8.GetBytes(call.Arguments));
        }
        catch (CryptographicException)
        {
            // The key ring that sealed it is gone; the request can't be shown to be this call, so it isn't.
            return false;
        }
    }

    /// <summary>A call's arguments and the hashes a stored request may hold for it.</summary>
    private sealed record Call(string Arguments, string RedactedArguments)
    {
        public bool HasSecrets { get; } = !string.Equals(Arguments, RedactedArguments, StringComparison.Ordinal);

        /// <summary>
        /// The hash a request made now stores: of the redacted arguments, never of a secret. Prefixed, so a matcher
        /// from before CON-185 (still running mid-deploy, or after a rollback) can't take a call that sends the
        /// placeholder in place of the secret for the call that was approved: no JSON object hashes to it.
        /// </summary>
        public string Hash { get; } = Sha256(HashPrefix + RedactedArguments);

        /// <summary>The hash of the full arguments, which a request without sealed arguments holds (no secret in it, or made before CON-185).</summary>
        public string LegacyHash { get; } = Sha256(Arguments);

        private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public async Task<IReadOnlyList<McpApprovalEntity>> ListPendingAsync(Guid userId, bool isAdmin, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.McpApprovals.AsNoTracking()
            .Where(a => a.Status == McpApprovalStatus.Pending && a.ExpiresAt > now && (isAdmin || a.OwnerUserId == userId))
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<McpApprovalDecision> DecideAsync(Guid id, Guid userId, bool isAdmin, bool approve, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var request = await db.McpApprovals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (request is null || (!isAdmin && request.OwnerUserId != userId))
            return request is null ? McpApprovalDecision.NotFound : McpApprovalDecision.Forbidden;

        // Conditional on still being Pending, so an approve and a deny racing can't both land and the
        // later one overwrite the other.
        var status = approve ? McpApprovalStatus.Approved : McpApprovalStatus.Denied;
        var expiresAt = approve ? now + ApprovedFor : request.ExpiresAt;
        int decided = await db.McpApprovals
            .Where(a => a.Id == id && a.Status == McpApprovalStatus.Pending && a.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.DecidedAt, now)
                .SetProperty(a => a.DecidedByUserId, userId)
                .SetProperty(a => a.ExpiresAt, expiresAt)
                .SetProperty(a => a.ArgumentsHash, a => approve ? a.ArgumentsHash : "")
                .SetProperty(a => a.ArgumentsProtected, a => approve ? a.ArgumentsProtected : null), ct);
        return decided == 1 ? McpApprovalDecision.Decided : McpApprovalDecision.NotPending;
    }
}
