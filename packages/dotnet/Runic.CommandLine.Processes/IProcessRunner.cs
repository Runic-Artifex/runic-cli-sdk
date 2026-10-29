using System;
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

/// <summary>Convenience overloads of <see cref="IProcessRunner"/>.</summary>
public static class ProcessRunnerExtensions
{
    /// <summary>
    /// Runs one bounded request without an output handler: <c>runner.RunAsync(request, cancellationToken)</c> is
    /// <c>runner.RunAsync(request, null, cancellationToken)</c>.
    /// </summary>
    /// <param name="runner">The runner.</param>
    /// <param name="request">The immutable process request.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>A sanitized terminal result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runner"/> is null.</exception>
    public static ValueTask<ProcessResult> RunAsync(
        this IProcessRunner runner,
        ProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.RunAsync(request, null, cancellationToken);
    }
}
