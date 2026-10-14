using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.CustomSource.Example;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddProblemDetails();

// Resolved lazily so a test host's DataDirectory setting is visible.
builder.Services.AddSingleton(sp => new ExampleDataDirectory(
    sp.GetRequiredService<IConfiguration>()["DataDirectory"]
    ?? Path.Combine(AppContext.BaseDirectory, "sample-data")));
builder.Services.AddSingleton(sp => ExampleConfig.Load(sp.GetRequiredService<ExampleDataDirectory>().Path));
builder.Services.AddSingleton<FolderCatalog>();
builder.Services.AddSingleton<ChangeJournal>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LinkStore>();
builder.Services.AddSingleton<AccessPolicy>();

WebApplication app = builder.Build();

app.MapContentEndpoints();
app.MapPermissionEndpoints();

app.Run();

public partial class Program;
