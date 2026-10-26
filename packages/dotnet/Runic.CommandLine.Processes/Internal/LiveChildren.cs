using System;
using System.Collections.Generic;

namespace Runic.CommandLine.Processes.Internal;

/// <summary>
/// The children whose runs have not returned yet, process-wide. When the process exits while a run is still in
/// progress, for example through <see cref="Environment.Exit(int)"/> after a repeated termination signal, or when
/// <c>Main</c> returns, the runner cannot finish its own termination; the exit handler kills those children and
/// their process groups or jobs instead, so they do not outlive the request.
/// </summary>
/// <remarks>
/// Best effort: the handler runs only when the runtime runs <see cref="AppDomain.ProcessExit"/>. A crash, SIGKILL
/// or an unhandled SIGHUP skips it; on Windows the job object then still ends the children when the process's
/// handles close.
/// </remarks>
internal static class LiveChildren
{
    private static readonly HashSet<ChildProcess> Children = new(ReferenceEqualityComparer.Instance);
    private static bool registered;
    private static bool exiting;

    internal static void Add(ChildProcess child)
    {
        bool killNow;
        lock (Children)
        {
            if (!registered)
            {
                AppDomain.CurrentDomain.ProcessExit += static (_, _) => OnProcessExit();
                registered = true;
            }

            killNow = exiting;
            if (!killNow)
            {
                _ = Children.Add(child);
            }
        }

        // A child started while the process is already exiting would otherwise be missed.
        if (killNow)
        {
            Kill(child);
        }
    }

    internal static void Remove(ChildProcess child)
    {
        lock (Children)
        {
            _ = Children.Remove(child);
        }
    }

    internal static int Count
    {
        get
        {
            lock (Children)
            {
                return Children.Count;
            }
        }
    }

    private static void OnProcessExit()
    {
        lock (Children)
        {
            exiting = true;
        }

        KillAll();
    }

    /// <summary>Kills every live child, as the process exit handler does. Internal for tests.</summary>
    internal static void KillAll()
    {
        ChildProcess[] children;
        lock (Children)
        {
            children = [.. Children];
        }

        foreach (ChildProcess child in children)
        {
            Kill(child);
        }
    }

    // A run that returns in the meantime disposes its child; Kill on a disposed child does nothing or throws.
    private static void Kill(ChildProcess child)
    {
        try
        {
            child.Kill();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
        }
    }
}
