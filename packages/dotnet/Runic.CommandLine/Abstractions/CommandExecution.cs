using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Executes a command with typed options and a typed semantic result.</summary>
/// <typeparam name="TOptions">The immutable bound options type.</typeparam>
/// <typeparam name="TResult">The successful command result type.</typeparam>
public interface ICommandHandler<in TOptions, TResult>
{
    /// <summary>Executes one command invocation.</summary>
    /// <param name="options">The bound command options.</param>
    /// <param name="context">The invocation-local execution context.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The semantic command outcome.</returns>
    ValueTask<CommandOutcome<TResult>> ExecuteAsync(
        TOptions options,
        CommandExecutionContext context,
        CancellationToken cancellationToken);
}

/// <summary>Provides invocation-local state to a typed command handler.</summary>
public sealed class CommandExecutionContext
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>Initializes an invocation-local command execution context.</summary>
    /// <param name="services">The scoped service provider for this invocation.</param>
    /// <param name="console">The invocation-local console.</param>
    /// <param name="path">The canonical command path.</param>
    /// <param name="outputMode">The selected presentation mode.</param>
    /// <param name="culture">The culture selected for presentation.</param>
    /// <param name="correlationId">A non-empty opaque correlation identifier.</param>
    /// <exception cref="ArgumentNullException">A required reference is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="correlationId"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outputMode"/> is not defined.</exception>
    public CommandExecutionContext(
        IServiceProvider services,
        ICommandConsole console,
        CommandPath path,
        CommandOutputMode outputMode,
        CultureInfo culture,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(culture);
        ValidateCorrelationId(correlationId);

        if (!Enum.IsDefined(outputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(outputMode));
        }

        Services = services;
        Console = console;
        Path = path;
        OutputMode = outputMode;
        Culture = CultureInfo.ReadOnly((CultureInfo)culture.Clone());
        CorrelationId = correlationId;
    }

    /// <summary>Gets the scoped service provider for this invocation.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Gets the invocation-local console.</summary>
    public ICommandConsole Console { get; }

    /// <summary>Gets the canonical command path.</summary>
    public CommandPath Path { get; }

    /// <summary>Gets the selected presentation mode.</summary>
    public CommandOutputMode OutputMode { get; }

    /// <summary>
    /// Gets or sets what JSON output contains when <see cref="OutputMode"/> is <see cref="CommandOutputMode.Json"/>:
    /// <see cref="JsonOutputShape.Payload"/> when the command line gave a JSON output alias declared with that
    /// shape. The default is <see cref="JsonOutputShape.Envelope"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public JsonOutputShape OutputShape
    {
        get => _outputShape;
        init => _outputShape = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    private readonly JsonOutputShape _outputShape;

    /// <summary>Gets the culture selected for presentation.</summary>
    public CultureInfo Culture { get; }

    /// <summary>Gets the opaque correlation identifier.</summary>
    public string CorrelationId { get; }

    /// <summary>
    /// Gets or sets the application's text resolver, which <see cref="CommandPrompts"/> uses for framework prompt text.
    /// Handlers can resolve their own prompt messages with <c>new CommandTextContext(context.Culture, context.TextResolver)</c>.
    /// </summary>
    public ICommandTextResolver? TextResolver { get; init; }

    /// <summary>
    /// Gets whether the entry point that started this invocation can run a continuation on its calling thread:
    /// true under <see cref="CommandApp.Run"/> and under a hosting adapter execution whose input sets
    /// <see cref="Hosting.HostedCommandLineExecutionInput.AcceptsContinuation"/>, false under
    /// <see cref="CommandApp.RunAsync"/>, other hosting adapter executions and a directly used
    /// <see cref="CommandExecutor"/>. See <see cref="ContinueOnCallingThread"/>.
    /// </summary>
    public bool CanContinueOnCallingThread => Continuation is not null;

    /// <summary>
    /// Hands a synchronous continuation back to the thread that called <see cref="CommandApp.Run"/>, typically the
    /// process's main thread. <see cref="CommandApp.Run"/> invokes it on that thread after the invocation has
    /// completed (its output written and its scope disposed) and returns its result as the process exit code.
    /// </summary>
    /// <param name="continuation">
    /// The work that must run on the calling thread, such as starting a UI host that requires the main thread.
    /// Capture values, not scoped services: the invocation scope is disposed before it runs.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="continuation"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="CanContinueOnCallingThread"/> is false, or the invocation already has a continuation.
    /// </exception>
    /// <remarks>
    /// The continuation runs only when the invocation succeeds: it is discarded when the command fails, is
    /// cancelled, or its output cannot be written. Help, version and usage errors never reach a command. An
    /// exception from the continuation propagates from <see cref="CommandApp.Run"/>. Under a hosting adapter that
    /// accepts continuations, the adapter returns it in
    /// <see cref="Hosting.HostedCommandLineExecutionResult.Continuation"/> and the host runs it on a thread of its
    /// choosing.
    /// </remarks>
    public void ContinueOnCallingThread(Func<int> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        if (Continuation is null)
        {
            throw new InvalidOperationException(
                "Only CommandApp.Run, or a hosting adapter execution that accepts continuations, runs a continuation; this invocation was started otherwise.");
        }

        Continuation.Set(continuation);
    }

    // Set by CommandApp.Run through the execution request.
    internal CommandContinuation? Continuation { get; init; }

    /// <summary>
    /// Keeps the process alive on a repeated termination signal while the returned scope is alive, so that cleanup
    /// that must finish, such as stopping child process trees or removing created resources, is not cut short.
    /// </summary>
    /// <returns>A scope; dispose it when the cleanup has finished. Disposing it more than once has no effect.</returns>
    /// <remarks>
    /// <para>
    /// With <see cref="CommandApp.HandleTerminationSignals"/>, the first Ctrl+C (SIGINT), SIGTERM or SIGQUIT cancels
    /// the invocation and a repeated one exits the process at once with 128 plus the signal number. While at least
    /// one scope of the invocation is alive, a repeated signal is ignored instead; the invocation stays cancelled.
    /// After the last scope is disposed, the next signal exits again.
    /// </para>
    /// <para>
    /// When the invocation's signals are not handled by <see cref="CommandApp"/> (its
    /// <see cref="CommandApp.HandleTerminationSignals"/> is false, or the invocation runs under the hosting adapter
    /// or a directly used <see cref="CommandExecutor"/>), the scope has no effect.
    /// </para>
    /// </remarks>
    public IDisposable ProtectShutdown() => ShutdownProtection?.Enter() ?? ShutdownProtection.None;

    // Set by CommandApp through the execution request when it handles termination signals.
    internal ShutdownProtection? ShutdownProtection { get; init; }

    // Set by CommandApp and the hosting adapter from RUNIC_COMMANDLINE_NO_INPUT.
    internal bool PromptsDisabled { get; init; }

    // The invocation's token, which CommandPrompts observes even when a handler passes none.
    internal CancellationToken InvocationCancellation { get; init; }

    private static void ValidateCorrelationId(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(correlationId);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Correlation identifiers must contain valid Unicode text.",
                nameof(correlationId),
                exception);
        }

        if (byteCount > 128)
        {
            throw new ArgumentException(
                "Correlation identifiers cannot exceed 128 UTF-8 bytes.",
                nameof(correlationId));
        }

        foreach (char character in correlationId)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                throw new ArgumentException(
                    "Correlation identifiers cannot contain control or whitespace characters.",
                    nameof(correlationId));
            }
        }

        foreach (Rune rune in correlationId.EnumerateRunes())
        {
            int scalar = rune.Value;
            if (scalar is >= 0xFDD0 and <= 0xFDEF || (scalar & 0xFFFE) == 0xFFFE)
            {
                throw new ArgumentException(
                    "Correlation identifiers cannot contain Unicode noncharacters.",
                    nameof(correlationId));
            }
        }
    }
}

/// <summary>Owns the scoped services for one valid command invocation.</summary>
public interface ICommandExecutionScope : IAsyncDisposable
{
    /// <summary>Gets the invocation's scoped services.</summary>
    IServiceProvider Services { get; }
}

/// <summary>Creates isolated scopes for valid command invocations.</summary>
public interface ICommandExecutionScopeFactory
{
    /// <summary>Creates a new invocation scope.</summary>
    ICommandExecutionScope CreateScope();
}

/// <summary>
/// Creates a handler through an explicitly registered, closed generic factory.
/// </summary>
/// <typeparam name="THandler">The handler type.</typeparam>
/// <remarks>
/// Implementations must not discover handler types or constructors through
/// assembly scanning or runtime reflection.
/// </remarks>
public interface ICommandHandlerFactory<out THandler>
    where THandler : notnull
{
    /// <summary>Creates or resolves the handler from invocation-scoped services.</summary>
    /// <param name="services">The invocation-scoped service provider.</param>
    THandler Create(IServiceProvider services);
}
