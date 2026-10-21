using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Processes;

/// <summary>Runs local processes without shell interpretation.</summary>
public interface IProcessRunner
{
    /// <summary>Runs one bounded request, passing its output to a handler while it runs when one is given.</summary>
    /// <param name="request">The immutable process request.</param>
    /// <param name="outputHandler">Receives the output as it arrives; null runs the request without one.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>A sanitized terminal result.</returns>
    /// <remarks>
    /// A test double that cannot stream may run the request and then pass the captured output to the handler, but
    /// callers rely on <see cref="ProcessRunner"/> streaming it, for example to show a task log while it runs.
    /// </remarks>
    ValueTask<ProcessResult> RunAsync(
        ProcessRequest request,
        IProcessOutputHandler? outputHandler = null,
        CancellationToken cancellationToken = default);
}
