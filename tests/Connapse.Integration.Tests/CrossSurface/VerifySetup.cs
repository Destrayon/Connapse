using System.Runtime.CompilerServices;
using DiffEngine;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// Snapshot settings for the characterization tests built on this harness. A changed snapshot
/// must arrive as a reviewed .verified file in the PR: CI never accepts one, and no diff tool
/// launches on a build server.
/// </summary>
public static class VerifySetup
{
    [ModuleInitializer]
    public static void Initialize()
    {
        VerifierSettings.ScrubInlineGuids();
        VerifierSettings.ScrubInlineDateTimes("yyyy-MM-ddTHH:mm:ss.FFFFFFFZ");
        if (Environment.GetEnvironmentVariable("CI") is not null)
            DiffRunner.Disabled = true;
    }
}
