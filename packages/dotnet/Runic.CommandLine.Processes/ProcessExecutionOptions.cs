using System;
using System.Text;

namespace Runic.CommandLine.Processes;

/// <summary>Configures bounded process execution and output capture.</summary>
public sealed class ProcessExecutionOptions
{
    /// <summary>The default retained-byte cap for each output channel.</summary>
    public const int DefaultOutputLimitBytes = 1024 * 1024;

    /// <summary>The hard retained-byte ceiling for either output channel.</summary>
    public static int MaximumOutputLimitBytes { get; } = 16 * 1024 * 1024;

    /// <summary>The default grace period for exit and pipe drain after termination.</summary>
    public static readonly TimeSpan DefaultDrainGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>The hard ceiling for post-termination drain grace.</summary>
    public static readonly TimeSpan MaximumDrainGracePeriod = TimeSpan.FromMinutes(1);

    // Declared before NonInteractive, whose initializer runs the constructor.
    private static readonly TimeSpan MaximumTimerDuration = TimeSpan.FromMilliseconds(4294967294);

    private ProcessStandardInput standardInput = ProcessStandardInput.Inherit;

    /// <summary>
    /// Gets options for unattended callers, such as a desktop application or a service: standard input is closed, so
    /// a child that reads it gets end-of-file, and <see cref="DetachFromTerminal"/> is set, so on Unix a child that
    /// prompts on <c>/dev/tty</c> (ssh, sudo, git credentials, gpg) fails to open it instead of waiting on the
    /// terminal that started the caller. The other values are the defaults. To change them, set the same two
    /// properties on new options; check results with <see cref="ProcessResult.EnsureCompleted"/> so that truncated
    /// output is not taken for complete output.
    /// </summary>
    public static ProcessExecutionOptions NonInteractive { get; } = new()
    {
        StandardInput = ProcessStandardInput.Closed,
        DetachFromTerminal = true,
    };

    /// <summary>Initializes process execution options.</summary>
    /// <param name="timeout">Maximum execution time, or <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="standardOutputLimitBytes">Maximum retained standard-output bytes.</param>
    /// <param name="standardErrorLimitBytes">Maximum retained standard-error bytes.</param>
    /// <param name="drainGracePeriod">Bounded wait for pipes after exit and for the process group after SIGTERM.</param>
    /// <param name="standardOutputEncoding">Standard-output encoding; UTF-8 without a BOM by default.</param>
    /// <param name="standardErrorEncoding">Standard-error encoding; UTF-8 without a BOM by default.</param>
    /// <param name="allowWindowsBatchFiles">
    /// Allows Windows <c>.bat</c> and <c>.cmd</c> files, which run through <c>cmd.exe</c>; arguments
    /// containing <c>cmd.exe</c> metacharacters are still rejected.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A limit or duration is outside its allowed range.</exception>
    public ProcessExecutionOptions(
        TimeSpan? timeout = null,
        int standardOutputLimitBytes = DefaultOutputLimitBytes,
        int standardErrorLimitBytes = DefaultOutputLimitBytes,
        TimeSpan? drainGracePeriod = null,
        Encoding? standardOutputEncoding = null,
        Encoding? standardErrorEncoding = null,
        bool allowWindowsBatchFiles = false)
    {
        Timeout = timeout ?? System.Threading.Timeout.InfiniteTimeSpan;
        DrainGracePeriod = drainGracePeriod ?? DefaultDrainGracePeriod;

        if (Timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Timeout, TimeSpan.Zero, nameof(timeout));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                Timeout,
                MaximumTimerDuration,
                nameof(timeout));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            DrainGracePeriod,
            TimeSpan.Zero,
            nameof(drainGracePeriod));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            DrainGracePeriod,
            MaximumDrainGracePeriod,
            nameof(drainGracePeriod));

        ArgumentOutOfRangeException.ThrowIfNegative(standardOutputLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(standardErrorLimitBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            standardOutputLimitBytes,
            MaximumOutputLimitBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            standardErrorLimitBytes,
            MaximumOutputLimitBytes);

        StandardOutputLimitBytes = standardOutputLimitBytes;
        StandardErrorLimitBytes = standardErrorLimitBytes;
        StandardOutputEncoding = standardOutputEncoding ?? new UTF8Encoding(false, false);
        StandardErrorEncoding = standardErrorEncoding ?? new UTF8Encoding(false, false);
        AllowWindowsBatchFiles = allowWindowsBatchFiles;
    }

    /// <summary>
    /// Gets the child input mode. Inherits the caller's stdin by default, which suits a command-line application
    /// whose tools may prompt; see <see cref="NonInteractive"/> for callers without a terminal.
    /// </summary>
    /// <exception cref="ArgumentNullException">The input is null.</exception>
    public ProcessStandardInput StandardInput
    {
        get => standardInput;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            standardInput = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether, on Unix, the child starts in a new session without a controlling terminal.
    /// It then cannot open <c>/dev/tty</c>, so a tool that would prompt there fails instead of waiting, and it leads
    /// its own process group, which cancellation and timeout terminate as a whole. False by default: a caller with a
    /// controlling terminal then keeps the child in its own process group, so the child can prompt and receives the
    /// terminal's Ctrl+C, and cancellation terminates the process tree; a caller without one, such as a service,
    /// starts the child in a new process group anyway. No effect on Windows.
    /// </summary>
    public bool DetachFromTerminal { get; init; }

    /// <summary>Gets whether the child inherits the parent environment before request overrides are applied.</summary>
    public bool InheritEnvironment { get; init; } = true;

    /// <summary>Gets the maximum execution time.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Gets the standard-output retained-byte cap.</summary>
    public int StandardOutputLimitBytes { get; }

    /// <summary>Gets the standard-error retained-byte cap.</summary>
    public int StandardErrorLimitBytes { get; }

    /// <summary>
    /// Gets the grace period that bounds the waits after a child exits or is stopped: for its pipes to close after
    /// exit; on cancellation or timeout, for the process group to exit after SIGTERM before SIGKILL; and once more
    /// for the pipes after SIGKILL.
    /// </summary>
    public TimeSpan DrainGracePeriod { get; }

    /// <summary>Gets the standard-output encoding.</summary>
    public Encoding StandardOutputEncoding { get; }

    /// <summary>Gets the standard-error encoding.</summary>
    public Encoding StandardErrorEncoding { get; }

    /// <summary>
    /// Gets a value indicating whether Windows batch files may start. When enabled, arguments containing
    /// <c>% ! ^ &amp; | &lt; &gt; " ( )</c> or line breaks are rejected because <c>cmd.exe</c> would interpret them.
    /// </summary>
    public bool AllowWindowsBatchFiles { get; }
}
