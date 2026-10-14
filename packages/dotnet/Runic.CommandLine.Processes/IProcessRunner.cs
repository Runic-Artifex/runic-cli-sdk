using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Processes;

/// <summary>Runs local processes without shell interpretation.</summary>
public interface IProcessRunner
{
    /// <summary>Runs one bounded request.</summary>
    /// <param name="request">The immutable process request.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>A sanitized terminal result.</returns>
    ValueTask<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Runs one bounded request and passes its output to a handler while it runs.</summary>
    /// <param name="request">The immutable process request.</param>
    /// <param name="outputHandler">Receives the output; null runs the request without one.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>A sanitized terminal result.</returns>
    /// <remarks>
    /// <see cref="ProcessRunner"/> streams the output. This default implementation, for runners that cannot, such
    /// as test doubles, runs the request and then passes the captured standard output and standard error, in that
    /// order, to the handler.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The required output handler parameter keeps calls unambiguous; the earlier overload is unchanged.")]
    async ValueTask<ProcessResult> RunAsync(
        ProcessRequest request,
        IProcessOutputHandler? outputHandler,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (outputHandler is not null)
        {
            await Internal.OutputReplay.ReplayAsync(request, result, outputHandler, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }
}
