using System;
using System.Collections.Generic;

namespace Runic.CommandLine.Contracts.Tests;

internal static class Assert
{
    public static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true, but found false.");
        }
    }

    public static void False(bool condition)
    {
        if (condition)
        {
            throw new InvalidOperationException("Expected false, but found true.");
        }
    }

    public static void Null<T>(T? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected null, but found '{value}'.");
        }
    }

    public static void Same<T>(T expected, T actual)
        where T : class
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException("Expected both values to reference the same instance.");
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but found '{actual}'.");
        }
    }

    public static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        Equal(expected.Count, actual.Count);

        for (var index = 0; index < expected.Count; index++)
        {
            Equal(expected[index], actual[index]);
        }
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Expected {typeof(TException).Name}, but found {exception.GetType().Name}.",
                exception);
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
    }
}
