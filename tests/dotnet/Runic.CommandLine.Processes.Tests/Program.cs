using System.Threading.Tasks;
using Microsoft.Testing.Platform.Builder;

namespace Runic.CommandLine.Processes.Tests;

/// <summary>
/// The process cases start this executable again as their child process, so the entry point runs that child
/// before it starts the TUnit test application. <c>GenerateTestingPlatformEntryPoint</c> is off, and the test
/// application is built the way the generated entry point builds it.
/// </summary>
internal static class Program
{
#pragma warning disable TUnit0034 // The child process fixture needs a hand-written entry point.
    public static async Task<int> Main(string[] args)
#pragma warning restore TUnit0034
    {
        if (ChildProcessFixture.IsChildInvocation(args))
        {
            return await ChildProcessFixture.RunAsync(args).ConfigureAwait(false);
        }

        ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
        builder.AddSelfRegisteredExtensions(args);
        using ITestApplication application = await builder.BuildAsync().ConfigureAwait(false);
        return await application.RunAsync().ConfigureAwait(false);
    }
}
