using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Runic.CommandLine;

// Hosting, "Termination signals": where the runtime cannot register a handler for a signal, that signal keeps the
// runtime's default handling, and the invocation still runs. PosixSignalRegistration.Create throws
// PlatformNotSupportedException where signals are unsupported, and an I/O, access or Win32 error when the operating
// system refuses the registration.
internal static class SignalRegistrations
{
    internal static PosixSignalRegistration? TryCreate(PosixSignal signal, Action<PosixSignalContext> handler)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, handler);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }
}
