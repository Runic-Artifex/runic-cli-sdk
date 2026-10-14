using System;

namespace Runic.CommandLine.Processes;

/// <summary>
/// Receives lifecycle notifications for every request of a runner: no arguments, paths or output. Implement either
/// <c>OnStarted</c> overload; the runner calls the one with the <see cref="ProcessIdentity"/>.
/// </summary>
public interface IProcessObserver
{
    /// <summary>Called after a child starts, unless the other overload is implemented.</summary>
    /// <param name="startedAt">The UTC start timestamp.</param>
    void OnStarted(DateTimeOffset startedAt)
    {
    }

    /// <summary>Called after a child starts. By default, calls <see cref="OnStarted(DateTimeOffset)"/>.</summary>
    /// <param name="process">The child's process ID and, on Unix, its process group.</param>
    /// <param name="startedAt">The UTC start timestamp.</param>
    void OnStarted(ProcessIdentity process, DateTimeOffset startedAt) => OnStarted(startedAt);

    /// <summary>Called after a request reaches a terminal state.</summary>
    /// <param name="state">The terminal state.</param>
    /// <param name="duration">The elapsed wall-clock duration.</param>
    void OnCompleted(ProcessState state, TimeSpan duration);
}
