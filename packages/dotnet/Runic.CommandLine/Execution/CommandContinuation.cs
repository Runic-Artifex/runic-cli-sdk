using System;
using System.Threading;

namespace Runic.CommandLine;

// The continuation a command hands back to the thread that called CommandApp.Run; see
// CommandExecutionContext.ContinueOnCallingThread.
internal sealed class CommandContinuation
{
    private Func<int>? _continuation;

    internal void Set(Func<int> continuation)
    {
        if (Interlocked.CompareExchange(ref _continuation, continuation, null) is not null)
        {
            throw new InvalidOperationException("The invocation already has a continuation.");
        }
    }

    internal Func<int>? Take() => Interlocked.Exchange(ref _continuation, null);
}
