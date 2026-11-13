using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Runic.CommandLine.Processes;

/// <summary>Lists the framework text keys of the process faults, one <c>faults.{code}</c> key per <see cref="ProcessFaultCodes"/> code.</summary>
/// <remarks>
/// A command that returns a process fault shows its message, which an <see cref="ICommandTextResolver"/> translates
/// through these keys like the other fault keys: with no arguments, by code. The keys are also part of
/// <see cref="CommandTextKeys.All"/>, and their English and German text ships in the <c>runic-command-line</c> catalog of
/// <c>Runic.CommandLine</c>, so the <c>Runic.Translations.CommandLine</c> adapter translates them without referencing this
/// package. Open: a later minor release may add keys together with new codes.
/// </remarks>
public static class ProcessTextKeys
{
    /// <summary>The <c>RCLI6001</c> fault message of a request the executable policy rejected.</summary>
    public const string ExecutableRejected = FaultPrefix + ProcessFaultCodes.ExecutableRejected;
    /// <summary>The <c>RCLI6002</c> fault message of a working directory the executable policy rejected.</summary>
    public const string WorkingDirectoryRejected = FaultPrefix + ProcessFaultCodes.WorkingDirectoryRejected;
    /// <summary>The <c>RCLI6003</c> fault message of an invalid policy decision.</summary>
    public const string InvalidPolicyDecision = FaultPrefix + ProcessFaultCodes.InvalidPolicyDecision;
    /// <summary>The <c>RCLI6004</c> fault message of a policy that could not evaluate the request.</summary>
    public const string PolicyEvaluationFailed = FaultPrefix + ProcessFaultCodes.PolicyEvaluationFailed;
    /// <summary>The <c>RCLI6005</c> fault message of a process that could not be started.</summary>
    public const string StartFailed = FaultPrefix + ProcessFaultCodes.StartFailed;
    /// <summary>The <c>RCLI6006</c> fault message of a process lifecycle that could not be observed safely.</summary>
    public const string ExecutionFailed = FaultPrefix + ProcessFaultCodes.ExecutionFailed;
    /// <summary>The <c>RCLI6007</c> fault message of a Windows batch file without opt-in.</summary>
    public const string BatchFileRejected = FaultPrefix + ProcessFaultCodes.BatchFileRejected;
    /// <summary>The <c>RCLI6008</c> fault message of a batch file path or argument with a <c>cmd.exe</c> metacharacter.</summary>
    public const string BatchFileArgumentRejected = FaultPrefix + ProcessFaultCodes.BatchFileArgumentRejected;
    /// <summary>The <c>RCLI6009</c> fault message of an output handler that threw.</summary>
    public const string OutputHandlerFailed = FaultPrefix + ProcessFaultCodes.OutputHandlerFailed;
    /// <summary>The <c>RCLI6010</c> fault message of a request whose timeout elapsed.</summary>
    public const string TimedOut = FaultPrefix + ProcessFaultCodes.TimedOut;
    /// <summary>The <c>RCLI6011</c> fault message of incomplete captured output.</summary>
    public const string OutputIncomplete = FaultPrefix + ProcessFaultCodes.OutputIncomplete;

    private const string FaultPrefix = "faults.";

    /// <summary>Gets every process fault key, in code order; each has no arguments.</summary>
    public static IReadOnlyList<CommandTextKey> All { get; } = new ReadOnlyCollection<CommandTextKey>(
    [
        Find(ExecutableRejected), Find(WorkingDirectoryRejected), Find(InvalidPolicyDecision), Find(PolicyEvaluationFailed),
        Find(StartFailed), Find(ExecutionFailed), Find(BatchFileRejected), Find(BatchFileArgumentRejected),
        Find(OutputHandlerFailed), Find(TimedOut), Find(OutputIncomplete),
    ]);

    // Runic.CommandLine of the same release lists every process fault key.
    private static CommandTextKey Find(string key) =>
        CommandTextKeys.Find(key) ?? throw new System.InvalidOperationException(key + " is missing from CommandTextKeys.All.");
}
