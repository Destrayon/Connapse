using Connapse.Application.Ports;
using Connapse.Storage.Data;
using Connapse.Storage.Vectors;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

/// <summary>What an MCP tool now takes besides its arguments: the operation's authorizer, and the caller.</summary>
internal static class McpCaller
{
    /// <summary>Allows every policy, so a tool test covers the tool's own rendering.</summary>
    public static IOperationAuthorizer Allowed
    {
        get
        {
            var authorizer = Substitute.For<IOperationAuthorizer>();
            authorizer.AllowsAsync(default!, default!).ReturnsForAnyArgs(true);
            return authorizer;
        }
    }

    public static IHttpContextAccessor Http => new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

    /// <summary>A model discovery that finds no embedding models, without a database.</summary>
    public static VectorModelDiscovery NoModels()
    {
        var models = Substitute.ForPartsOf<VectorModelDiscovery>(
            Substitute.For<IDbContextFactory<KnowledgeDbContext>>(), Substitute.For<ILogger<VectorModelDiscovery>>());
        models.GetModelsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<EmbeddingModelInfo>());
        return models;
    }
}
