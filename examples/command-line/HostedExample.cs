using Runic.CommandLine;
using Runic.CommandLine.Examples;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Spectre;

// The application calls this with its lifetime token. The adapter never installs
// process signal handlers, starts a host, or disposes application-owned services.
[CommandGroup("application", Description = "Use services supplied by the application host.")]
internal static class HostedExample
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken applicationStopping)
    {
        var services = new ApplicationServices("Hello from application services");
        var console = new SpectreCommandConsole();
        // The adapter reuses the app's catalog, name, version, help presenter, scope factory and observer.
        var adapter = new CommandLineHostingAdapter(ExampleApplication.Create(console, services));
        var launch = new HostedCommandLineLaunchInput(args,
            outputEnvironmentValue: Environment.GetEnvironmentVariable("RUNIC_COMMANDLINE_OUTPUT"),
            emptyInputFallback: EmptyInputFallback.UserInterface)
        {
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["HELLO_ENV"] = Environment.GetEnvironmentVariable("HELLO_ENV"),
            },
        };
        var decision = adapter.Classify(launch);
        if (!decision.IsCommandLineRequest)
        {
            // A desktop application calls its existing UI launch method here, also for a
            // document path or an unknown word. This console example only demonstrates the decision.
            await console.WriteOutLineAsync("Application selected its UI launch path.", applicationStopping);
            return 0;
        }
        return await adapter.RunAsync(decision, cancellationToken: applicationStopping);
    }

    [Command("application info", Description = "Read services supplied by the application host.")]
    internal static string Info([FromServices] ApplicationServices services) => services.Greeting;

    internal sealed class ApplicationServices(string greeting)
    {
        internal static ApplicationServices Default { get; } = new("Hello from application services");
        internal string Greeting { get; } = greeting;
        internal Exception? LastException { get; private set; }
        internal void RecordException(Exception exception) => LastException = exception;
    }
}
