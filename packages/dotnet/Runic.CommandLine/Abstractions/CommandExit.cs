using System;

namespace Runic.CommandLine;

/// <summary>
/// Identifies the semantic category of a command-line outcome independently of
/// the numeric exit code selected by a host.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CommandExitCodes"/> lists the default exit code of each category. Closed: these values are complete,
/// and adding one is a breaking change, so every <see cref="IExitCodePolicy"/> may switch over them exhaustively.
/// </para>
/// <para>
/// A handler chooses between the failure categories by asking whether the command line itself is wrong. If it is,
/// so that the same command line fails the same way whatever the files, services or data it works on, the failure
/// is input-shaped: <see cref="Validation"/>, exit code 2, like the <see cref="Usage"/> errors the parser and binder
/// report. If the command line is well formed and the operation failed on what it found, the failure is a domain
/// failure: <see cref="CommandFailure"/>, or <see cref="Unavailable"/> when something it needs is missing or
/// unreachable, exit code 1.
/// </para>
/// <list type="table">
/// <listheader><term>Category (exit code)</term><description>Examples</description></listheader>
/// <item><term><see cref="Validation"/> (2)</term><description>Two options that exclude each other, <c>--since</c> later than <c>--until</c>, a name that breaks a naming rule, a value in a format the binder does not check.</description></item>
/// <item><term><see cref="CommandFailure"/> (1)</term><description>A business rule refused the operation, a check found problems, a conflict with existing data, a remote service rejected the request, a child process failed.</description></item>
/// <item><term><see cref="Unavailable"/> (1)</term><description>The named file, project or record does not exist, or a required service cannot be reached.</description></item>
/// </list>
/// </remarks>
public enum CommandExitCategory
{
    /// <summary>The command completed successfully.</summary>
    Success = 0,

    /// <summary>
    /// The invocation could not be parsed or bound: an unknown command or option, missing, malformed or out-of-range
    /// input, or a prompt that cannot ask. Command Line reports it; exit code 2.
    /// </summary>
    Usage = 1,

    /// <summary>
    /// A handler rejected well-formed input because of its shape, independently of the state the command works on, for
    /// example two options that exclude each other; exit code 2. A failure that depends on that state, such as a
    /// business rule that refuses the operation, is a <see cref="CommandFailure"/>.
    /// </summary>
    Validation = 2,

    /// <summary>The invocation was cancelled.</summary>
    Cancelled = 3,

    /// <summary>A required command resource or service was unavailable, including "not found"; exit code 1.</summary>
    Unavailable = 4,

    /// <summary>
    /// The command reported an expected domain failure on well-formed input, such as a refused operation, a failed
    /// check or a conflict; exit code 1.
    /// </summary>
    CommandFailure = 5,

    /// <summary>The host or command infrastructure failed unexpectedly.</summary>
    HostFailure = 6,
}

/// <summary>
/// Defines the default process exit codes for command outcome categories.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Exit code</term><description>Category and typical cause</description></listheader>
/// <item><term>0</term><description><see cref="CommandExitCategory.Success"/>, including help, version and completion scripts.</description></item>
/// <item><term>1</term><description><see cref="CommandExitCategory.CommandFailure"/>: the command reported an expected domain failure on well-formed input; <see cref="CommandExitCategory.Unavailable"/>: a required resource or service was unavailable, including "not found".</description></item>
/// <item><term>2</term><description><see cref="CommandExitCategory.Usage"/>: unknown command or option, missing, malformed or out-of-range input (<c>RCLI1xxx</c>, <c>RCLI2xxx</c>), or a prompt that cannot ask (<c>RCLI4001</c>); <see cref="CommandExitCategory.Validation"/>: a handler rejected the shape of well-formed input, for example two options that exclude each other. A business rule that refuses the operation is a <see cref="CommandExitCategory.CommandFailure"/>, exit code 1.</description></item>
/// <item><term>3</term><description><see cref="CommandExitCategory.HostFailure"/>: an unexpected exception or infrastructure failure (<c>RCLI5000</c>).</description></item>
/// <item><term>130</term><description><see cref="CommandExitCategory.Cancelled"/>: Ctrl+C, SIGTERM, SIGQUIT or the caller's token cancelled the invocation, or the user cancelled a prompt.</description></item>
/// <item><term>130, 131, 143</term><description>Not a category: a repeated termination signal while the invocation still runs exits at once with 128 plus the signal number.</description></item>
/// </list>
/// These are the codes every Runic tool reports: the dotnet CLI's 1 for failures, 2 for usage, and the shell's 130
/// for an interrupted command. The categories stay distinct in <see cref="CommandExitCategory"/>, the fault code
/// and observers. Handlers select a category with <see cref="CommandOutcome"/>; an <see cref="IExitCodePolicy"/>
/// may give categories distinct codes, but zero always means success.
/// </remarks>
public static class CommandExitCodes
{
    /// <summary>The default success exit code.</summary>
    public const int Success = 0;

    /// <summary>The default usage or parse failure exit code.</summary>
    public const int Usage = 2;

    /// <summary>
    /// The default validation failure exit code, the same as <see cref="Usage"/>: input-shaped failures exit with 2,
    /// domain failures with <see cref="CommandFailure"/>.
    /// </summary>
    public const int Validation = 2;

    /// <summary>The default cancellation exit code, as a shell reports an interrupted command (128 plus SIGINT).</summary>
    public const int Cancelled = 130;

    /// <summary>The default unavailable-resource exit code, the same as <see cref="CommandFailure"/>.</summary>
    public const int Unavailable = 1;

    /// <summary>The default expected command failure exit code.</summary>
    public const int CommandFailure = 1;

    /// <summary>The default unexpected host or software failure exit code.</summary>
    public const int HostFailure = 3;

    /// <summary>
    /// Gets the default numeric exit code for a semantic category.
    /// </summary>
    /// <param name="category">The semantic outcome category.</param>
    /// <returns>The corresponding default exit code.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="category"/> is not a defined category.
    /// </exception>
    public static int GetDefault(CommandExitCategory category) => category switch
    {
        CommandExitCategory.Success => Success,
        CommandExitCategory.Usage => Usage,
        CommandExitCategory.Validation => Validation,
        CommandExitCategory.Cancelled => Cancelled,
        CommandExitCategory.Unavailable => Unavailable,
        CommandExitCategory.CommandFailure => CommandFailure,
        CommandExitCategory.HostFailure => HostFailure,
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };
}

/// <summary>
/// Maps semantic command outcome categories to process exit codes.
/// </summary>
/// <remarks>
/// Implementations may remap failures but must return zero only for
/// <see cref="CommandExitCategory.Success"/>.
/// </remarks>
public interface IExitCodePolicy
{
    /// <summary>
    /// Gets a process exit code for the supplied semantic category.
    /// </summary>
    /// <param name="category">The semantic outcome category.</param>
    /// <returns>A process exit code. Zero is reserved for success.</returns>
    int GetExitCode(CommandExitCategory category);
}

/// <summary>
/// Maps each semantic category to the default Runic.CommandLine process exit code.
/// </summary>
public sealed class DefaultExitCodePolicy : IExitCodePolicy
{
    /// <summary>Gets the shared stateless default policy.</summary>
    public static DefaultExitCodePolicy Instance { get; } = new();

    private DefaultExitCodePolicy()
    {
    }

    /// <inheritdoc />
    public int GetExitCode(CommandExitCategory category) => CommandExitCodes.GetDefault(category);
}
