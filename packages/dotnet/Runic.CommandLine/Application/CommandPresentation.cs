using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Shared framework presentation for standalone and hosted commands.</summary>
public sealed class CommandPresentation
{
    /// <summary>Gets the application name shown in help.</summary>
    public string Name { get; init; } = "app";
    /// <summary>Gets the application version.</summary>
    public string Version { get; init; } = "0.0.0";
    /// <summary>Gets the executable name used by completion scripts.</summary>
    public string? CompletionExecutableName { get; init; }
    /// <summary>Gets optional text resolution for help and framework diagnostics; the caller supplies culture per invocation.</summary>
    public ICommandTextResolver? TextResolver { get; init; }
    /// <summary>Gets the human help renderer.</summary>
    public ICommandHelpPresenter? HelpPresenter { get; init; }
    /// <summary>Gets an optional application-specific help formatter.</summary>
    public Func<CommandPath, string>? FormatHelp { get; init; }
    /// <summary>Gets the policy used for framework errors; use the executor's policy for consistency.</summary>
    public IExitCodePolicy ExitCodePolicy { get; init; } = DefaultExitCodePolicy.Instance;

    /// <summary>Gets the observer for private presentation failures. Observer failures do not change the exit code.</summary>
    public Action<Exception>? ExceptionObserver { get; init; }

    internal async ValueTask<int> GuardAsync(Func<ValueTask<int>> present, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await present().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodePolicy.GetExitCode(CommandExitCategory.Cancelled);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            try { ExceptionObserver?.Invoke(exception); }
            catch (Exception observerException) when (!IsFatal(observerException)) { }
            // Output may already be partial or broken. Do not attempt another frame/write.
            return ExitCodePolicy.GetExitCode(CommandExitCategory.HostFailure);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or AccessViolationException or AppDomainUnloadedException or BadImageFormatException;

    internal async ValueTask<int> WriteCompletionAsync(CommandCatalog catalog, string shell, string outputOptionName, ICommandConsole console, CultureInfo culture, CancellationToken cancellationToken)
    {
        string script;
        try { script = CommandCompletion.Generate(catalog, CompletionExecutableName ?? Name, shell, outputOptionName); }
        catch (ArgumentException)
        {
            string message = new CommandTextContext(culture, TextResolver).Resolve("completion.invalid-shell", "Choose a completion shell: bash, zsh, fish or powershell.");
            message = CommandFaultSanitizer.ContainsTechnicalContent(message) ? "The diagnostic content was redacted." : CommandFaultSanitizer.SanitizeRequiredText(message);
            await console.WriteErrorAsync((message + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
            return ExitCodePolicy.GetExitCode(CommandExitCategory.Usage);
        }
        await console.WriteOutBytesAsync(System.Text.Encoding.UTF8.GetBytes(script), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    internal async ValueTask<int> WriteAsync(CommandCatalog catalog, ParseOutcome parsed, string outputOptionName,
        ICommandConsole console, CultureInfo culture, string requestId, CancellationToken cancellationToken)
    {
        var textContext = new CommandTextContext(culture, TextResolver);
        if (parsed.Kind == ParseOutcomeKind.Help && parsed.OutputClassification?.Mode == CommandOutputMode.Human && HelpPresenter is not null && FormatHelp is null)
        {
            await HelpPresenter.WriteAsync(catalog, Name, parsed.HelpRequest!.Path, outputOptionName, textContext, console, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        CommandResponse<string> response;
        if (parsed.Kind == ParseOutcomeKind.Error)
        {
            CommandDiagnostic diagnostic = parsed.Diagnostics[0];
            int exitCode = ExitCodePolicy.GetExitCode(CommandExitCategory.Usage);
            response = CommandResponse.Failed<string>(requestId, diagnostic.Path.Count > 0 ? diagnostic.Path.ToString() : "root", exitCode,
                new CommandFault(diagnostic.Code, diagnostic.Message), parsed.Diagnostics);
        }
        else
        {
            string text = parsed.Kind == ParseOutcomeKind.Version ? Version :
                FormatHelp?.Invoke(parsed.HelpRequest!.Path) ?? CommandHelpFormatter.Format(catalog, Name, parsed.HelpRequest!.Path, textContext, outputOptionName,
                    HelpWidth(console, parsed.OutputClassification?.Mode ?? CommandOutputMode.Human));
            response = CommandResponse.Succeeded(requestId, parsed.Kind == ParseOutcomeKind.Version ? "version" : "help", CommandResultCodecs.String.PayloadType, text);
        }
        await CommandOutputDispatcher.DispatchAsync(parsed.OutputClassification?.Mode ?? CommandOutputMode.Human,
            console, culture, response, CommandResultCodecs.String, textContext, cancellationToken).ConfigureAwait(false);
        return response.ExitCode;
    }

    // Human help on the process terminal follows its width, one column short so a full line never triggers the
    // legacy Windows console's automatic wrap. Redirected, test and machine output use the fixed default.
    private static int HelpWidth(ICommandConsole console, CommandOutputMode mode)
    {
        if (mode != CommandOutputMode.Human || console is not SystemCommandConsole || console.IsOutputRedirected) return CommandHelpFormatter.DefaultWidth;
        try
        {
            int width = Console.WindowWidth - 1;
            return width >= CommandHelpFormatter.MinimumWidth ? width : CommandHelpFormatter.DefaultWidth;
        }
        catch (Exception exception) when (exception is System.IO.IOException or PlatformNotSupportedException)
        {
            return CommandHelpFormatter.DefaultWidth;
        }
    }
}
