using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Connapse.CustomSource.Conformance;
using FluentAssertions;

namespace Connapse.CustomSource.Example.Tests;

/// <summary>
/// The suite against middle men that each break one rule. A conformance tool earns its keep by failing these,
/// so each test asserts the specific check fails.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConformanceNegativeTests
{
    private const string Token = "broken-token";

    private static async Task<CheckResult> Run(BrokenMiddleMan server, string checkId)
    {
        HttpClient http = new(server) { BaseAddress = new Uri("https://broken.test") };
        IReadOnlyList<CheckResult> results = await new ConformanceSuite(http, new ConformanceOptions
        {
            BaseUrl = new Uri("https://broken.test"),
            Token = Token,
            UserId = "reader",
        }, server).RunAsync();
        return results.Single(r => r.Id == checkId);
    }

    [Fact]
    public async Task OffsetPageTokens_FailTheDeletionWhilePagingCheck()
    {
        CheckResult result = await Run(new BrokenMiddleMan { OffsetTokens = true }, "listing.deletion-while-paging");

        result.Outcome.Should().Be(CheckOutcome.Fail, result.Detail);
    }

    [Fact]
    public async Task KeysetPageTokens_PassTheDeletionWhilePagingCheck()
    {
        CheckResult result = await Run(new BrokenMiddleMan(), "listing.deletion-while-paging");

        result.Outcome.Should().Be(CheckOutcome.Pass, result.Detail);
    }

    [Fact]
    public async Task ResultsWithoutADecision_FailTheUnknownUserCheck()
    {
        CheckResult result = await Run(new BrokenMiddleMan { EmptyDecisions = true }, "check.unknown-user");

        result.Outcome.Should().Be(CheckOutcome.Fail);
        result.Detail.Should().Contain("no boolean decision");
    }

    [Fact]
    public async Task MalformedPermissions_FailInsteadOfCountingAsContentOnly()
    {
        BrokenMiddleMan server = new() { MalformedPermissions = true };

        (await Run(server, "permissions.declared")).Outcome.Should().Be(CheckOutcome.Fail);
        (await Run(server, "check.unknown-user")).Outcome.Should().Be(CheckOutcome.Fail);
    }

    [Fact]
    public async Task KeysetPaging_LeavesNoTestDocumentsBehind()
    {
        BrokenMiddleMan server = new();

        await Run(server, "listing.deletion-while-paging");

        server.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateHookReportingAnExistingId_FailsAndLeavesThatDocumentAlone()
    {
        BrokenMiddleMan server = new() { ReportExistingId = true };

        CheckResult result = await Run(server, "listing.deletion-while-paging");

        result.Outcome.Should().Be(CheckOutcome.Fail);
        server.Holds("doc-a").Should().BeTrue();
    }

    [Fact]
    public async Task DeleteHookThatOnlyClaimsSuccess_FailsThePagingCheck()
    {
        CheckResult result = await Run(new BrokenMiddleMan { PretendToDelete = true }, "listing.deletion-while-paging");

        result.Outcome.Should().Be(CheckOutcome.Fail);
        result.Detail.Should().Contain("still answered");
    }

    [Fact]
    public async Task TestDocumentsThatCannotBeDeleted_AreReported()
    {
        CheckResult result = await Run(new BrokenMiddleMan { RefuseDeletes = true }, "listing.deletion-while-paging");

        result.Outcome.Should().Be(CheckOutcome.Fail);
        result.Detail.Should().Contain("remove them by hand");
    }

    [Fact]
    public async Task PermissionsWithoutCheck_FailInsteadOfSkipping()
    {
        BrokenMiddleMan server = new() { OmitCheckCapability = true };

        (await Run(server, "permissions.declared")).Outcome.Should().Be(CheckOutcome.Fail);
        (await Run(server, "check.unknown-user")).Outcome.Should().Be(CheckOutcome.Fail);
    }

    /// <summary>A minimal in-memory middle man whose faults are switched on one at a time.</summary>
    private sealed class BrokenMiddleMan : HttpMessageHandler, IConformanceHooks
    {
        private const int PageSize = 2;
        private readonly List<string> _ids = ["doc-a", "doc-b", "doc-c", "doc-d", "doc-e"];
        private int _created;

        public bool OffsetTokens { get; init; }
        public bool EmptyDecisions { get; init; }
        public bool OmitCheckCapability { get; init; }
        public bool MalformedPermissions { get; init; }
        public bool RefuseDeletes { get; init; }
        public bool ReportExistingId { get; init; }
        public bool PretendToDelete { get; init; }

        public bool CanChangeDocuments => true;

        public Task<string?> CreateDocumentAsync(string scope, CancellationToken ct)
        {
            // Sorts first, so a created document lands on the first page.
            string id = $"a-created-{_created++}";
            _ids.Add(id);
            _ids.Sort(StringComparer.Ordinal);
            return Task.FromResult<string?>(ReportExistingId ? "doc-a" : id);
        }

        public Task<bool> DeleteDocumentAsync(string scope, string documentId, CancellationToken ct) =>
            Task.FromResult(!RefuseDeletes && (PretendToDelete || _ids.Remove(documentId)));

        public bool Holds(string id) => _ids.Contains(id);

        /// <summary>Every document IDs created and not yet deleted, for checking cleanup.</summary>
        public IEnumerable<string> Created => _ids.Where(id => id.StartsWith("a-created-", StringComparison.Ordinal));

        public Task<string?> CompleteSignInAsync(Uri linkUrl, string linkToken, Uri returnUrl, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.Authorization?.Parameter != Token) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            string path = request.RequestUri!.AbsolutePath;
            string? pageToken = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["page_token"];

            return path switch
            {
                "/.well-known/connapse-source" => Json(new JsonObject
                {
                    ["protocol_versions"] = new JsonArray("1"),
                    ["capabilities"] = new JsonObject
                    {
                        ["max_page_size"] = PageSize,
                        ["permissions"] = MalformedPermissions
                            ? JsonValue.Create(true)
                            : OmitCheckCapability
                                ? new JsonObject { ["access_keys"] = new JsonObject() }
                                : new JsonObject { ["check"] = new JsonObject { ["max_batch"] = 10 } },
                    },
                }),
                "/v1/scopes" => Json(new JsonObject { ["items"] = new JsonArray(new JsonObject { ["id"] = "s", ["name"] = "S" }) }),
                "/v1/scopes/s/documents" => Listing(pageToken),
                _ when path.StartsWith("/v1/scopes/s/documents/", StringComparison.Ordinal) && path.EndsWith("/content", StringComparison.Ordinal)
                    => _ids.Contains(Uri.UnescapeDataString(path["/v1/scopes/s/documents/".Length..^"/content".Length]))
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") }
                        : new HttpResponseMessage(HttpStatusCode.NotFound),
                "/access/v1/evaluations" => await Evaluate(request, ct),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private HttpResponseMessage Listing(string? pageToken)
        {
            if (OffsetTokens && pageToken is not null && !int.TryParse(pageToken, out _))
                return new HttpResponseMessage(HttpStatusCode.Gone);
            IEnumerable<string> rest = OffsetTokens
                ? _ids.Skip(int.Parse(pageToken ?? "0"))
                : _ids.Where(id => pageToken is null || string.CompareOrdinal(id, pageToken) > 0);
            List<string> page = rest.Take(PageSize).ToList();
            bool more = rest.Count() > PageSize;
            string? next = !more ? null : OffsetTokens ? (int.Parse(pageToken ?? "0") + PageSize).ToString() : page[^1];
            return Json(new JsonObject
            {
                ["items"] = new JsonArray(page.Select(id => (JsonNode)new JsonObject
                {
                    ["id"] = id,
                    ["path"] = id + ".md",
                    ["content_type"] = "text/markdown",
                    ["size"] = 1,
                    ["version"] = "1",
                }).ToArray()),
                ["next_page_token"] = next,
            });
        }

        private async Task<HttpResponseMessage> Evaluate(HttpRequestMessage request, CancellationToken ct)
        {
            JsonNode body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            int count = body["evaluations"]!.AsArray().Count;
            return Json(new JsonObject
            {
                ["evaluations"] = new JsonArray(Enumerable.Range(0, count)
                    .Select(_ => (JsonNode)(EmptyDecisions ? new JsonObject() : new JsonObject { ["decision"] = false })).ToArray()),
            });
        }

        private static HttpResponseMessage Json(JsonNode body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
