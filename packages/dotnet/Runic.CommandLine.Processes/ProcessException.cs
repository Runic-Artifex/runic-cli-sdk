using System;

namespace Runic.CommandLine.Processes;

/// <summary>Reports a process request that did not complete; thrown by <see cref="ProcessResult.EnsureCompleted"/>.</summary>
public sealed class ProcessException : Exception
{
    /// <summary>Initializes a process exception.</summary>
    /// <param name="result">The incomplete result.</param>
    /// <param name="fault">The fault that names the reason; its message becomes the exception message.</param>
    /// <exception cref="ArgumentNullException">The result or fault is null.</exception>
    public ProcessException(ProcessResult result, CommandFault fault)
        : base(fault?.Message)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(fault);
        Result = result;
        Fault = fault;
    }

    /// <summary>Gets the incomplete result.</summary>
    public ProcessResult Result { get; }

    /// <summary>Gets the fault that names the reason, with a code from <see cref="ProcessFaultCodes"/>.</summary>
    public CommandFault Fault { get; }
}
