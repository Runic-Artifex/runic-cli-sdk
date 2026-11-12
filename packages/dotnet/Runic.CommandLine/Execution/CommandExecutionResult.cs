namespace Runic.CommandLine;

/// <summary>Summarizes a completed command execution after scope disposal and output dispatch.</summary>
public sealed class CommandExecutionResult
{
    internal CommandExecutionResult(CommandExitCategory category, int exitCode, CommandFault? fault)
    {
        ExitCategory = category;
        ExitCode = exitCode;
        Fault = fault;
    }

    /// <summary>Gets the semantic exit category.</summary>
    public CommandExitCategory ExitCategory { get; }

    /// <summary>
    /// Gets the process exit code: the one the outcome selected with <see cref="CommandOutcome{T}.WithExitCode"/>,
    /// otherwise the one the exit-code policy maps the category to.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>Gets the safe fault for an unsuccessful result.</summary>
    public CommandFault? Fault { get; }

    /// <summary>Gets whether execution succeeded.</summary>
    public bool Succeeded => ExitCategory == CommandExitCategory.Success;
}
