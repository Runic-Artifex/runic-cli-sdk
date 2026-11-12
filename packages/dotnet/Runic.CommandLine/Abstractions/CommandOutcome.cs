using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace Runic.CommandLine;

/// <summary>
/// Represents a typed semantic outcome returned by a command handler.
/// </summary>
/// <typeparam name="T">The command result type.</typeparam>
/// <remarks>
/// This contract deliberately contains no parser or serializer types. A host
/// maps <see cref="ExitCategory"/> through an <see cref="IExitCodePolicy"/>,
/// unless the outcome selects its own <see cref="ExitCode"/>, and presents
/// either <see cref="Value"/> or <see cref="Fault"/>.
/// </remarks>
public sealed class CommandOutcome<T>
{
    private const int MaximumHumanOutputBytes = 1_048_576;
    private static readonly IReadOnlyList<CommandDiagnostic> NoDiagnostics =
        new ReadOnlyCollection<CommandDiagnostic>(Array.Empty<CommandDiagnostic>());
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal CommandOutcome(
        bool isSuccess,
        CommandExitCategory exitCategory,
        T? value,
        CommandFault? fault,
        IReadOnlyList<CommandDiagnostic>? diagnostics,
        string? humanOutput,
        CommandFailureData? failureData = null,
        int? exitCode = null)
    {
        Succeeded = isSuccess;
        ExitCategory = exitCategory;
        Value = value;
        Fault = fault;
        Diagnostics = CopyDiagnostics(diagnostics, isSuccess);
        HumanOutput = CopyHumanOutput(humanOutput, isSuccess);
        FailureData = failureData;
        ExitCode = exitCode;
    }

    /// <summary>Gets a value indicating whether the command succeeded.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the semantic category used to select a process exit code.</summary>
    public CommandExitCategory ExitCategory { get; }

    /// <summary>Gets the typed value for a successful outcome.</summary>
    public T? Value { get; }

    /// <summary>Gets the safe fault for a failed outcome.</summary>
    public CommandFault? Fault { get; }

    /// <summary>Gets the optional, explicitly declared domain failure data snapshot.</summary>
    public CommandFailureData? FailureData { get; }

    /// <summary>
    /// Gets the process exit code this outcome selects with <see cref="WithExitCode"/>, or null when the
    /// <see cref="IExitCodePolicy"/> maps <see cref="ExitCategory"/> to the code.
    /// </summary>
    public int? ExitCode { get; }

    /// <summary>
    /// Returns a copy of this outcome that exits with <paramref name="exitCode"/> instead of the code the
    /// <see cref="IExitCodePolicy"/> maps its category to, for example 2, 3 and 4 for three documented failures of
    /// one command, or 3010 for a success that needs a restart.
    /// </summary>
    /// <param name="exitCode">
    /// The process exit code. A failure needs a code that no platform reads as success: not zero, and not a
    /// multiple of 256, which Linux and macOS truncate to zero. A success may use zero or an informational code.
    /// </param>
    /// <returns>An outcome with the same category, value, fault, diagnostics and data, and the given exit code.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The outcome is a failure and <paramref name="exitCode"/> would read as success.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The category stays the semantic classification: the fault code, observers, <c>success</c> in JSON output and
    /// <see cref="CommandExecutionResult.Succeeded"/> follow it, and the exit code changes only for this invocation.
    /// The JSON envelope, <see cref="CommandExecutionResult.ExitCode"/>, <see cref="CommandOutcomePresentation{T}.ExitCode"/>
    /// and observers report the code the process returns. Other commands of the catalog, and the outcomes the framework
    /// creates itself (usage errors, cancellation, host failures), keep the policy's codes.
    /// </para>
    /// <para>
    /// Linux and macOS report only the low 8 bits of an exit code, so 3010 reads as 194 there; a tool that runs on them
    /// uses codes from 1 to 255. A success with a nonzero code is an informational success for callers that document
    /// it, such as Windows Installer's 3010 ("restart required") or 1641 ("restart started"); other callers read any
    /// nonzero code as failure. Readers of the <c>runic.commandline/1</c> envelope before Runic Command Line 0.12
    /// reject a successful envelope with a nonzero <c>exitCode</c>. Document the codes in help with
    /// <see cref="CommandHelp.ExitCodes"/> or <see cref="CommandExitCodeAttribute"/>.
    /// </para>
    /// </remarks>
    public CommandOutcome<T> WithExitCode(int exitCode)
    {
        if (!Succeeded && !CommandExitCodes.IsFailureCode(exitCode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(exitCode),
                exitCode,
                "A failed outcome needs an exit code that is not zero and not a multiple of 256.");
        }

        return new CommandOutcome<T>(Succeeded, ExitCategory, Value, Fault, Diagnostics, HumanOutput, FailureData, exitCode);
    }

    /// <summary>
    /// Gets the ordered, immutable diagnostics produced while binding or
    /// executing this outcome.
    /// </summary>
    /// <remarks>
    /// An outcome holds at most 32 diagnostics so a successful binding outcome
    /// and a handler outcome can be combined without exceeding the response
    /// protocol's 64-diagnostic bound.
    /// </remarks>
    public IReadOnlyList<CommandDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets application-owned failure text that is written only to human
    /// standard output before diagnostics and the fault.
    /// </summary>
    /// <remarks>
    /// This value is never included in the <c>runic.commandline/1</c> JSON
    /// envelope. A JSON output alias of payload shape writes it to standard
    /// error in place of the fault line, because standard output then holds
    /// the document. It is preserved exactly, including control characters, and is
    /// limited to one mebibyte of valid UTF-8 text.
    /// </remarks>
    public string? HumanOutput { get; }

    private static IReadOnlyList<CommandDiagnostic> CopyDiagnostics(
        IReadOnlyList<CommandDiagnostic>? diagnostics,
        bool isSuccess)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return NoDiagnostics;
        }

        const int maximumDiagnosticCount = 32;
        if (diagnostics.Count > maximumDiagnosticCount)
        {
            throw new ArgumentException(
                $"A command outcome cannot contain more than {maximumDiagnosticCount} diagnostics.",
                nameof(diagnostics));
        }

        var copy = new CommandDiagnostic[diagnostics.Count];
        for (int index = 0; index < diagnostics.Count; index++)
        {
            CommandDiagnostic diagnostic = diagnostics[index] ??
                throw new ArgumentException("Diagnostics cannot contain null entries.", nameof(diagnostics));
            if (isSuccess && diagnostic.Severity == CommandDiagnosticSeverity.Error)
            {
                throw new ArgumentException(
                    "A successful command outcome cannot contain error diagnostics.",
                    nameof(diagnostics));
            }

            copy[index] = diagnostic;
        }

        return new ReadOnlyCollection<CommandDiagnostic>(copy);
    }

    private static string? CopyHumanOutput(string? humanOutput, bool isSuccess)
    {
        if (string.IsNullOrEmpty(humanOutput))
        {
            return null;
        }

        if (isSuccess)
        {
            throw new ArgumentException(
                "A successful command outcome cannot contain failure-only human output.",
                nameof(humanOutput));
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(humanOutput);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Human output must contain valid Unicode text.",
                nameof(humanOutput),
                exception);
        }

        if (byteCount > MaximumHumanOutputBytes)
        {
            throw new ArgumentException(
                $"Human output cannot exceed {MaximumHumanOutputBytes} UTF-8 bytes.",
                nameof(humanOutput));
        }

        return new string(humanOutput.AsSpan());
    }
}

/// <summary>Creates invariant-preserving typed command outcomes.</summary>
public static class CommandOutcome
{
    /// <summary>
    /// Creates a successful outcome. A nullable result type may use a
    /// <see langword="null"/> value.
    /// </summary>
    /// <typeparam name="T">The command result type.</typeparam>
    /// <param name="value">The typed command result.</param>
    /// <returns>A successful outcome.</returns>
    public static CommandOutcome<T> Success<T>(T value) => Success<T>(value, null);

    /// <summary>
    /// Creates a successful outcome with warning or information diagnostics.
    /// </summary>
    /// <typeparam name="T">The command result type.</typeparam>
    /// <param name="value">The typed command result.</param>
    /// <param name="diagnostics">
    /// Optional warning or information diagnostics to present with the value.
    /// </param>
    /// <returns>A successful outcome.</returns>
    public static CommandOutcome<T> Success<T>(
        T value,
        IReadOnlyList<CommandDiagnostic>? diagnostics) =>
        new(true, CommandExitCategory.Success, value, null, diagnostics, null);

    /// <summary>Creates a failed outcome with a safe fault.</summary>
    /// <param name="category">A failure category.</param>
    /// <param name="fault">The stable, consumer-safe fault.</param>
    /// <param name="diagnostics">
    /// Optional binding or execution diagnostics to present with the fault.
    /// </param>
    /// <param name="humanOutput">
    /// Optional application-owned text for human standard output only. It is
    /// never serialized into machine output.
    /// </param>
    /// <param name="failureData">
    /// Optional, separately declared domain data, preserved exactly in the machine fault extension. Human
    /// presentation uses the fault and <paramref name="humanOutput"/>.
    /// </param>
    /// <returns>A failed outcome.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="category"/> is <see cref="CommandExitCategory.Success"/>
    /// or is not a defined category.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="humanOutput"/> is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fault"/> is null.</exception>
    public static CommandOutcome<T> Failure<T>(
        CommandExitCategory category,
        CommandFault fault,
        IReadOnlyList<CommandDiagnostic>? diagnostics = null,
        string? humanOutput = null,
        CommandFailureData? failureData = null)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (category == CommandExitCategory.Success || !Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        return new CommandOutcome<T>(false, category, default, fault, diagnostics, humanOutput, failureData);
    }
}
