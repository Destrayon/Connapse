using System.Text.Json;
using Connapse.Application.Settings;
using Connapse.Core;
using CsCheck;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// CON-170: the upload settings had a ParallelWorkers value that nothing read; the ingestion worker count is
/// Hangfire:IngestionWorkerCount, fixed when the server starts. It is deprecated: still accepted, so a call that
/// sent it keeps working, but never stored or returned, and settings saved while it was stored still load.
/// </summary>
public partial class SettingsOperationTests
{
    [Fact]
    public void UploadSettings_WorkerCount_IsDeprecated() =>
        typeof(UploadSettings).GetProperty("ParallelWorkers")!.GetCustomAttributes(typeof(ObsoleteAttribute), false)
            .Should().ContainSingle("the ingestion worker count is Hangfire:IngestionWorkerCount, set at startup");

    [Fact]
    public void Serialize_UploadSettings_LeavesOutTheWorkerCount() =>
        JsonSerializer.Serialize(new UploadSettings()).Should().NotContainEquivalentOf("parallelWorkers");

    [Fact]
    public async Task Get_UploadSavedWithAWorkerCount_ReadsTheRest()
    {
        var world = new World();
        world.Store.Rows["upload"] = """{"ParallelWorkers":8,"ParseTimeoutSeconds":123}""";

        var read = await world.Get().RunAsync(Caller, "upload");

        read.Status.Should().Be(SettingsStatus.Success);
        read.Result!.Settings.Should().BeOfType<UploadSettings>().Which.ParseTimeoutSeconds.Should().Be(123);
    }

    [Fact]
    public async Task Update_UploadSavedWithAWorkerCount_SavesWithoutIt()
    {
        var world = new World();
        world.Store.Rows["upload"] = """{"ParallelWorkers":8,"ParseTimeoutSeconds":123}""";

        var updated = await world.Update().RunAsync(Caller, "upload", Body(new { parseTimeoutSeconds = 9 }));

        updated.Status.Should().Be(SettingsStatus.Success);
        world.Store.Rows["upload"].Should().NotContain("ParallelWorkers");
        world.Store.Stored<UploadSettings>("upload")!.ParseTimeoutSeconds.Should().Be(9);
    }

    [Fact]
    public async Task Update_UploadWithAWorkerCount_SucceedsWithoutKeepingIt()
    {
        var world = new World();

        var updated = await world.Update().RunAsync(Caller, "upload", Body(new { parallelWorkers = 8, parseTimeoutSeconds = 9 }));

        updated.Status.Should().Be(SettingsStatus.Success);
        world.Store.Rows["upload"].Should().NotContainEquivalentOf("parallelWorkers");
        world.Store.Stored<UploadSettings>("upload")!.ParseTimeoutSeconds.Should().Be(9);
        var read = await world.Get().RunAsync(Caller, "upload");
        JsonSerializer.Serialize(read.Result!.Settings).Should().NotContainEquivalentOf("parallelWorkers");
    }

    [Fact]
    [RandomInputTestOf(typeof(UpdateSettings))]
    public Task Update_UploadWithAnyWorkerCount_SucceedsWithoutKeepingIt() =>
        Gen.Select(Gen.Int, Gen.Int[1, 100_000], Gen.Bool).SampleAsync(async t =>
        {
            var (workers, timeout, camel) = t;
            var world = new World();
            string name = camel ? "parallelWorkers" : "ParallelWorkers";
            var body = Raw($$"""{"{{name}}":{{workers}},"parseTimeoutSeconds":{{timeout}}}""");

            var updated = await new UpdateSettings(world.Store, world.Configured, world.Indexes, world.Models, world.Audit, world.Authorizer)
                .RunAsync(Caller, "upload", body);

            updated.Status.Should().Be(SettingsStatus.Success);
            world.Store.Rows["upload"].Should().NotContainEquivalentOf("parallelWorkers");
            world.Store.Stored<UploadSettings>("upload")!.ParseTimeoutSeconds.Should().Be(timeout);
        }, iter: 300);

    [Fact]
    public void Bind_ConfigurationWithAWorkerCount_BindsTheRest()
    {
        // The database settings provider flattens a saved row into Knowledge:Upload:* keys, so an old row still
        // carries Knowledge:Upload:ParallelWorkers.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
        [
            new("Knowledge:Upload:ParallelWorkers", "8"),
            new("Knowledge:Upload:ParseTimeoutSeconds", "123"),
        ]).Build();

        var settings = configuration.GetSection("Knowledge:Upload").Get<UploadSettings>();

        settings!.ParseTimeoutSeconds.Should().Be(123);
    }

    // Rows were written by an int property, so a stored worker count is always an integer.
    private static readonly Gen<string> AnyOldWorkerCount = Gen.Int.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    [RandomInputTestOf(typeof(GetSettings))]
    public Task Get_UploadSavedWithAnyWorkerCount_ReadsTheRest() =>
        Gen.Select(AnyOldWorkerCount, Gen.Int[1, 100_000], Gen.Bool).SampleAsync(async t =>
        {
            var (workers, timeout, camel) = t;
            var world = new World();
            string name = camel ? "parallelWorkers" : "ParallelWorkers";
            world.Store.Rows["upload"] = $$"""{"{{name}}":{{workers}},"ParseTimeoutSeconds":{{timeout}}}""";

            var read = await new GetSettings(world.Store, world.Configured, world.Authorizer).RunAsync(Caller, "upload");

            read.Status.Should().Be(SettingsStatus.Success);
            var settings = read.Result!.Settings.Should().BeOfType<UploadSettings>().Subject;
            settings.ParseTimeoutSeconds.Should().Be(timeout);
            JsonSerializer.Serialize(settings).Should().NotContainEquivalentOf("parallelWorkers");
        }, iter: 300);
}
