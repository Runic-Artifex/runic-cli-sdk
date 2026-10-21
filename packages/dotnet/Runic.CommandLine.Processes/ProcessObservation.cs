using System;

namespace Runic.CommandLine.Processes;

/// <summary>
/// Receives lifecycle notifications for every request of a runner: no arguments, paths or output.
/// </summary>
public interface IProcessObserver
{
    /// <summary>Called after a child starts. The default implementation does nothing.</summary>
    /// <param name="process">The child's process ID and, on Unix, its process group.</param>
    /// <param name="startedAt">The UTC start timestamp.</param>
    void OnStarted(ProcessIdentity process, DateTimeOffset startedAt)
    {
    }

    /// <summary>Called after a request reaches a terminal state.</summary>
    /// <param name="state">The terminal state.</param>
    /// <param name="duration">The elapsed wall-clock duration.</param>
    void OnCompleted(ProcessState state, TimeSpan duration);
}
