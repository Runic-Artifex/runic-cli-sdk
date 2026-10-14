using Microsoft.Testing.Platform.Builder;

namespace Runic.CommandLine.Tests;

/// <summary>
/// The signal and prompt terminal cases start this executable again as a child process, so the entry point
/// runs that child before it starts the TUnit test application. <c>GenerateTestingPlatformEntryPoint</c> is
/// off, and the test application is built the way the generated entry point builds it.
/// </summary>
internal static class Program
{
#pragma warning disable TUnit0034 // The signal child needs a hand-written entry point.
    public static async Task<int> Main(string[] args)
#pragma warning restore TUnit0034
    {
        if (SignalTests.IsChildInvocation(args))
            return await SignalTests.RunChildAsync(args[1]).ConfigureAwait(false);
        if (PromptTerminalTests.IsChildInvocation(args))
            return await PromptTerminalTests.RunChildAsync().ConfigureAwait(false);

        ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
        builder.AddSelfRegisteredExtensions(args);
        using ITestApplication application = await builder.BuildAsync().ConfigureAwait(false);
        return await application.RunAsync().ConfigureAwait(false);
    }
}
