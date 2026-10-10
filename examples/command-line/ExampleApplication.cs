using Runic.CommandLine.Generated;
using Runic.CommandLine.Spectre;

namespace Runic.CommandLine.Examples;

/// <summary>The growing example shares its real application setup with invocation tests and the hosted runner.</summary>
public static class ExampleApplication
{
    /// <summary>Creates the real example application with invocation-local output.</summary>
    /// <param name="console">The invocation console.</param>
    /// <param name="services">Application services; a host that owns them also records internal exceptions there.</param>
    internal static CommandApp Create(ICommandConsole console, HostedExample.ApplicationServices? services = null) => new(GeneratedCommandCatalog.Create(builder => builder
        .GlobalOption("verbose", "--verbose", CommandArity.Zero, new CommandHelp("Show detailed progress."), "-v")
        .Present<TransformResult>("transform", (result, output, _, token) =>
            output.WriteOutLineAsync($"Wrote {result.Characters} characters to {result.Output}", token))))
    {
        // The application owns its services; an invocation only resolves them.
        ScopeFactory = CommandScopes.FromServices(CommandServices.Empty.With(services ?? HostedExample.ApplicationServices.Default)),
        ExceptionObserver = services is null ? null : services.RecordException,
        Name = "hello", Version = "1.0.0", HelpPresenter = new SpectreHelpPresenter(), Console = console,
    };

    /// <summary>Creates the real example application with invocation-local output.</summary>
    public static CommandApp Create(ICommandConsole console) => Create(console, null);
}
