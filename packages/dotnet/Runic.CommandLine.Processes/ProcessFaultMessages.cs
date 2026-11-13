using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Runic.CommandLine.Processes;

// The English message of each process fault, which is the English text of its faults.{code} key (ProcessTextKeys) in
// the runic-command-line catalog. A translation replaces a fault message by its code, so each code has exactly one
// message; the fault details, such as stream, reason or argumentIndex, tell the cases of one code apart.
internal static class ProcessFaultMessages
{
    internal const string PolicyRejected = "The process request is not permitted by the configured policy.";
    internal const string InvalidPolicyDecision = "The executable policy returned an invalid decision.";
    internal const string PolicyEvaluationFailed = "The executable policy could not evaluate the request.";
    internal const string StartFailed = "The process could not be started.";
    internal const string ExecutionFailed = "The process lifecycle could not be observed safely.";
    internal const string BatchFileRejected = "Windows batch files run through cmd.exe and require AllowWindowsBatchFiles.";
    internal const string BatchFileArgumentRejected = "The batch file path or an argument contains characters that cmd.exe would interpret.";
    internal const string OutputHandlerFailed = "The output handler failed.";
    internal const string TimedOut = "The process did not exit before its timeout.";
    internal const string OutputIncomplete =
        "The captured output is incomplete: it exceeded its capture limit, or a descendant process held the output pipe open.";

    /// <summary>Every process fault code with its English message, in code order.</summary>
    internal static IReadOnlyDictionary<string, string> ByCode { get; } = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProcessFaultCodes.ExecutableRejected] = PolicyRejected,
            [ProcessFaultCodes.WorkingDirectoryRejected] = PolicyRejected,
            [ProcessFaultCodes.InvalidPolicyDecision] = InvalidPolicyDecision,
            [ProcessFaultCodes.PolicyEvaluationFailed] = PolicyEvaluationFailed,
            [ProcessFaultCodes.StartFailed] = StartFailed,
            [ProcessFaultCodes.ExecutionFailed] = ExecutionFailed,
            [ProcessFaultCodes.BatchFileRejected] = BatchFileRejected,
            [ProcessFaultCodes.BatchFileArgumentRejected] = BatchFileArgumentRejected,
            [ProcessFaultCodes.OutputHandlerFailed] = OutputHandlerFailed,
            [ProcessFaultCodes.TimedOut] = TimedOut,
            [ProcessFaultCodes.OutputIncomplete] = OutputIncomplete,
        });
}
