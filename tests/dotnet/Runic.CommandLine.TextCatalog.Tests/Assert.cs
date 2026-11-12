using System;
using System.Collections.Generic;

namespace Runic.CommandLine.TextCatalog.Tests;

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}: expected '{expected}', but found '{actual}'.");
    }

    // Reports every problem at once, so one run lists all missing or orphan messages.
    public static void Empty(IReadOnlyCollection<string> problems, string heading)
    {
        if (problems.Count > 0) throw new InvalidOperationException(heading + ":" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems));
    }
}
