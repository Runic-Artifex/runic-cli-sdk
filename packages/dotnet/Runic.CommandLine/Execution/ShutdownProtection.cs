using System;
using System.Threading;

namespace Runic.CommandLine;

/// <summary>Counts the live <see cref="CommandExecutionContext.ProtectShutdown"/> scopes of one invocation.</summary>
internal sealed class ShutdownProtection
{
    private int _scopes;

    /// <summary>A scope that protects nothing, for invocations whose signals CommandApp does not handle.</summary>
    internal static IDisposable None { get; } = new NoScope();

    internal bool IsActive => Volatile.Read(ref _scopes) > 0;

    internal IDisposable Enter()
    {
        Interlocked.Increment(ref _scopes);
        return new Scope(this);
    }

    private sealed class Scope(ShutdownProtection owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._scopes);
        }
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
