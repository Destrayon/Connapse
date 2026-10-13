using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Components.Sources;

namespace Connapse.Web.Services;

/// <summary>
/// What <see cref="ConfluenceSourceScope"/> and <see cref="JiraSourceScope"/> share: the outcome of a
/// check, reading a scope's id and attachment fields, and changing an existing source's attachment
/// settings under the rules they were created with. Which one a scope is for is its <c>kind</c>;
/// a scope without one is Confluence's, as every scope was before Jira.
/// </summary>
public abstract class AtlassianSourceScope
{
    private protected AtlassianSourceScope()
    {
    }

    public enum Outcome { Valid, Invalid, Duplicate, Unavailable }

    /// <summary>The checked scope and default name, or what is wrong and with which field.</summary>
    public sealed record Result(Outcome Outcome, string? ScopeJson = null, string? DefaultName = null, string? Error = null, string? Field = null)
    {
        public static Result Fail(Outcome outcome, string field, string error) => new(outcome, Error: error, Field: field);
    }

    /// <summary>Whether <paramref name="scopeJson"/> names a Jira project by its <c>kind</c>.</summary>
    public static bool IsJira(string? scopeJson)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(scopeJson) ? "{}" : scopeJson) is JsonObject scope
                && scope["kind"] is JsonValue kind && kind.TryGetValue(out string? k)
                && IsJiraKind(k);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>What a product's checks say, in its own words; the checks themselves are shared.</summary>
    /// <param name="NotAnObject">The scope is not a JSON object.</param>
    /// <param name="IdError">The id is not digits.</param>
    /// <param name="MissingId">The scope names no id; given the connection id, for the listing route.</param>
    /// <param name="Unavailable">The site could not be asked, and said nothing itself.</param>
    /// <param name="NotOnSite">The id is not one the service account can read; given the id and the connection id.</param>
    /// <param name="Duplicate">The item already has a source; given its name.</param>
    /// <param name="IdFixed">A patch names another id.</param>
    private protected sealed record ScopeTexts(
        string NotAnObject, string IdError, Func<Guid, string> MissingId, string Unavailable,
        Func<string, Guid, string> NotOnSite, Func<string, string> Duplicate, string IdFixed);

    /// <summary>
    /// The checks the New source dialog runs, for the REST API: the id must be one the site's service
    /// account can read now and not already a source on the connection, and the attachment cap must be
    /// in range. The scope stored is rebuilt from Atlassian's answer, so an API-created source matches
    /// a UI-created one. A connection for another product than <paramref name="product"/> is refused, and
    /// <paramref name="notReady"/>, when set, refuses the site, both before Atlassian is asked.
    /// </summary>
    private protected static async Task<Result> ResolveAsync<TItem, TSelection>(
        string? scopeJson, Connection connection, AtlassianSiteService sites, ISourceStore sourceStore,
        ScopeTexts texts, AtlassianProduct product, string? notReady, CancellationToken ct)
        where TItem : IAtlassianSourceItem
        where TSelection : AtlassianSourceSelection<TItem, TSelection>, new()
    {
        var selection = new TSelection();
        string idField = selection.IdField;

        if (Read(scopeJson, idField, texts.IdError) is not { } parsed)
            return Result.Fail(Outcome.Invalid, "scopeJson", texts.NotAnObject);
        if (parsed.Error is not null)
            return Result.Fail(Outcome.Invalid, parsed.Field!, parsed.Error);
        if (parsed.Id is not { } id)
            return Result.Fail(Outcome.Invalid, idField, texts.MissingId(connection.Id));
        if (AtlassianSite.FromConfigJson(connection.ConfigJson) is { } site && site.Product != product)
            return Result.Fail(Outcome.Invalid, "connectionId", WrongProduct(connection, site.Product, product));
        if (notReady is not null)
            return Result.Fail(Outcome.Invalid, "connectionId", notReady);

        // Left out, attachments are on only when the site's last test confirmed their scope, as on the dialog.
        selection.IncludeAttachments = parsed.IncludeAttachments ?? selection.AttachmentScopeConfirmedFor(connection.Health);
        selection.MaxAttachmentMb = parsed.MaxAttachmentMb ?? AtlassianSourceSelection.DefaultMaxAttachmentMb;
        selection.Selected.Add(id);
        if (selection.Validate() is not null)
            return Result.Fail(Outcome.Invalid, "maxAttachmentMb", selection.MaxAttachmentMbError!);

        if (SiteOf(connection, out string siteHost) is { } incomplete)
            return incomplete;

        // Asked now, as the site's service account, exactly as the dialog asks before it creates anything.
        var onSite = await selection.ListOnSiteAsync(sites, connection.Id, ct);
        if (onSite.Outcome != AtlassianSpacesOutcome.Listed)
            return Result.Fail(Outcome.Unavailable, idField, onSite.Error ?? texts.Unavailable);

        var verified = selection.VerifiedAgainst(onSite.Items!, out var refused);
        if (refused.Count > 0)
            return Result.Fail(Outcome.Invalid, idField, texts.NotOnSite(id, connection.Id));

        var existing = selection.ExistingIdsOf(await sourceStore.ListByConnectionAsync(connection.Id, ct));
        var requests = verified.ToRequests(connection.Id, siteHost, existing, out var skipped);
        if (skipped.Count > 0)
            return Result.Fail(Outcome.Duplicate, idField, texts.Duplicate(skipped[0]));

        var request = requests.Single();
        return new Result(Outcome.Valid, request.ScopeJson, request.Name);
    }

    /// <summary>What a source of <paramref name="wanted"/> on a connection for <paramref name="actual"/> is told.</summary>
    private static string WrongProduct(Connection connection, AtlassianProduct actual, AtlassianProduct wanted) =>
        $"Connection '{connection.Name}' is a {actual} connection, so it can't hold a {wanted} source. "
        + $"Choose this site's {wanted} connection, or add one on the Connections page.";

    /// <summary>
    /// What the New source dialog says when a source of <paramref name="wantedProduct"/> (<c>confluence</c>
    /// or <c>jira</c>) would go on a connection for <paramref name="actual"/>; null when they match.
    /// </summary>
    public static string? WrongProduct(Connection connection, AtlassianProduct actual, string wantedProduct) =>
        AtlassianSite.ProductValue(actual) == wantedProduct
            ? null
            : WrongProduct(connection, actual,
                wantedProduct == AtlassianSite.ProductValue(AtlassianProduct.Jira) ? AtlassianProduct.Jira : AtlassianProduct.Confluence);

    /// <summary>Whether a scope's <c>kind</c> value is a Jira project's; the one place that comparison is made.</summary>
    public static bool IsJiraKind(string? kind) => string.Equals(kind, JiraProjectSelection.Kind, StringComparison.OrdinalIgnoreCase);

    private protected sealed record Parsed(string? Id, bool? IncludeAttachments, int? MaxAttachmentMb, string? Error = null, string? Field = null);

    /// <summary>
    /// The scope's fields, or null when it is not a JSON object. A field of the wrong type is
    /// reported, not ignored; <paramref name="idField"/> must be digits, or <paramref name="idError"/>.
    /// </summary>
    private protected static Parsed? Read(string? scopeJson, string idField, string idError)
    {
        JsonObject? scope;
        try
        {
            scope = JsonNode.Parse(string.IsNullOrWhiteSpace(scopeJson) ? "{}" : scopeJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (scope is null)
            return null;

        string? itemId = null;
        if (scope[idField] is { } id)
        {
            itemId = id.GetValueKind() switch
            {
                JsonValueKind.String => id.GetValue<string>().Trim(),
                JsonValueKind.Number => id.ToJsonString(),
                _ => null,
            };
            // The connector's own rule for a content id: digits only, which is all it will sync.
            if (itemId is not { Length: > 0 and <= 19 } || !itemId.All(char.IsAsciiDigit))
                return new Parsed(null, null, null, idError, idField);
        }

        bool? includeAttachments = null;
        if (scope["includeAttachments"] is { } include)
        {
            if (include.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                return new Parsed(null, null, null, "\"includeAttachments\" must be true or false.", "includeAttachments");
            includeAttachments = include.GetValue<bool>();
        }

        int? maxAttachmentMb = null;
        if (scope["maxAttachmentMb"] is { } max)
        {
            if (max is not JsonValue value || !value.TryGetValue(out int mb))
                return new Parsed(null, null, null,
                    $"\"maxAttachmentMb\" must be a whole number from {AtlassianSourceSelection.MinAttachmentMb} to {AtlassianSourceSelection.MaxAttachmentMbLimit}.",
                    "maxAttachmentMb");
            maxAttachmentMb = mb;
        }

        return new Parsed(itemId, includeAttachments, maxAttachmentMb);
    }

    /// <summary>
    /// A changed scope for an existing source whose id, in <paramref name="idField"/>, is fixed:
    /// indexed documents are keyed to it, so a different id is refused with <see cref="ScopeTexts.IdFixed"/>.
    /// Only what to read may change, under the same rules as when the source was created.
    /// </summary>
    private protected static Result Update(string currentScopeJson, string? newScopeJson, string idField, ScopeTexts texts)
    {
        if (Read(newScopeJson, idField, texts.IdError) is not { } parsed)
            return Result.Fail(Outcome.Invalid, "scopeJson", "The scope must be a JSON object.");
        if (parsed.Error is not null)
            return Result.Fail(Outcome.Invalid, parsed.Field!, parsed.Error);

        JsonObject current;
        try
        {
            current = JsonNode.Parse(currentScopeJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            current = [];
        }

        string? currentId = Read(currentScopeJson, idField, texts.IdError)?.Id;
        if (parsed.Id is { } id && !string.Equals(id, currentId, StringComparison.Ordinal))
            return Result.Fail(Outcome.Invalid, idField, texts.IdFixed);

        bool includeAttachments = parsed.IncludeAttachments
            ?? (current["includeAttachments"]?.GetValueKind() is JsonValueKind.False ? false : true);
        int storedMb = current["maxAttachmentMb"] is JsonValue v && v.TryGetValue(out int stored)
            ? AtlassianSourceSelection.StorableMaxAttachmentMb(stored)
            : AtlassianSourceSelection.DefaultMaxAttachmentMb;
        int maxAttachmentMb = parsed.MaxAttachmentMb ?? storedMb;

        if (includeAttachments && maxAttachmentMb is < AtlassianSourceSelection.MinAttachmentMb or > AtlassianSourceSelection.MaxAttachmentMbLimit)
            return Result.Fail(Outcome.Invalid, "maxAttachmentMb", AtlassianSourceSelection.AttachmentRangeError);

        // Not checked with attachments off, so an out-of-range size then keeps the stored one.
        maxAttachmentMb = AtlassianSourceSelection.StorableMaxAttachmentMb(maxAttachmentMb, storedMb);

        current["includeAttachments"] = includeAttachments;
        current["maxAttachmentMb"] = maxAttachmentMb;
        return new Result(Outcome.Valid, current.ToJsonString());
    }

    /// <summary>The site and its host, or the error a connection without its site details gets.</summary>
    private protected static Result? SiteOf(Connection connection, out string siteHost)
    {
        siteHost = "";
        if (Connapse.Storage.Connectors.Atlassian.AtlassianSite.FromConfigJson(connection.ConfigJson) is not { } site)
            return Result.Fail(Outcome.Invalid, "connectionId",
                $"Connection '{connection.Name}' is missing its site details or credential. Open it on the Connections page and choose Replace credentials.");
        siteHost = new Uri(site.SiteUrl).Host;
        return null;
    }
}
