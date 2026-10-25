using Connapse.Application.Containers;
using Connapse.Application.Files;
using Connapse.Application.Ports;
using Connapse.Core.Interfaces;
using Connapse.Web.Services;
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

    /// <summary>
    /// The file operations a file tool takes, over the stores a test registered on <paramref name="services"/>. Without a
    /// read guard registered, every read is denied, as the tools denied it before, and every document is listed.
    /// </summary>
    public static FileTools Files(IServiceProvider services)
    {
        T Get<T>() where T : class => (T?)services.GetService(typeof(T)) ?? Substitute.For<T>();
        var containers = Get<IContainerStore>();
        var documents = Get<IDocumentStore>();
        var access = services.GetService(typeof(DocumentReadGuard)) is DocumentReadGuard guard
            ? (IDocumentAccess)new DocumentAccess(guard)
            : ListsEverything();
        var delete = new DeleteFile(containers, documents, Get<IFolderStore>(), Get<IManagedStorageProvider>(), Get<IIngestionQueue>(), Get<IAuditLogger>(), Allowed,
            Get<Microsoft.Extensions.Logging.ILogger<DeleteFile>>());
        var parsers = (IEnumerable<IDocumentParser>?)services.GetService(typeof(IEnumerable<IDocumentParser>)) ?? [];
        return new FileTools(
            new GetContainer(containers, Allowed),
            new ListFiles(containers, documents, Get<IFolderStore>(), access, Allowed),
            new GetFile(containers, documents, access, Allowed),
            new GetFileContent(containers, documents, access, Get<IManagedStorageProvider>(), parsers, Allowed),
            delete,
            new BulkDeleteFiles(containers, delete, Allowed));
    }

    /// <summary>
    /// The upload operations an upload tool takes, over the stores a test registered on <paramref name="services"/>.
    /// Without a storage provider registered, every container is managed storage, so writable.
    /// </summary>
    public static UploadTools Uploads(IServiceProvider services)
    {
        T Get<T>() where T : class => (T?)services.GetService(typeof(T)) ?? Substitute.For<T>();
        var containers = Get<IContainerStore>();
        var storage = (IManagedStorageProvider?)services.GetService(typeof(IManagedStorageProvider)) ?? Writable();
        return new UploadTools(
            new GetContainer(containers, Allowed),
            new UploadFile(containers, storage, Get<IFileTypeValidator>(), Get<IUploadService>(), Allowed),
            new BulkUploadFiles(containers, storage, Get<IFileTypeValidator>(), Get<IUploadService>(), Allowed));
    }

    private static IManagedStorageProvider Writable()
    {
        var storage = Substitute.For<IManagedStorageProvider>();
        storage.CreateConnector(Arg.Any<string>()).Returns(_ => Substitute.For<IWritableConnector>());
        return storage;
    }

    public sealed record UploadTools(GetContainer Container, UploadFile Upload, BulkUploadFiles BulkUpload);

    private static IDocumentAccess ListsEverything()
    {
        var access = Substitute.For<IDocumentAccess>();
        access.ReadableAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlySet<string>)c.Arg<IReadOnlyList<Document>>().Select(d => d.Id).ToHashSet());
        return access;
    }

    public sealed record FileTools(GetContainer Container, ListFiles List, GetFile Get, GetFileContent Content, DeleteFile Delete, BulkDeleteFiles BulkDelete);
}
