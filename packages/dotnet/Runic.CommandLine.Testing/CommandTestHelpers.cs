using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

namespace Runic.CommandLine.Testing;

/// <summary>Creates deterministic parsing inputs without mutating process environment.</summary>
public static class CommandTestEnvironment
{
    /// <summary>
    /// Captures parse settings as <see cref="CommandApp"/> captures them from the process environment, but from the
    /// supplied variables only: <c>RUNIC_COMMANDLINE_OUTPUT</c> selects the output mode, and
    /// <c>RUNIC_COMMANDLINE_NO_INPUT</c> and option environment fallbacks read the same variables.
    /// </summary>
    /// <param name="variables">The environment variables, or none; the process environment is never read.</param>
    /// <returns>Settings for <see cref="CommandTestInvocation.Parse(CommandCatalog, ParseSettings, string[])"/> or <see cref="CommandApp.ParseSettings"/>.</returns>
    public static ParseSettings Capture(IReadOnlyDictionary<string, string?>? variables = null)
    {
        var snapshot = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (variables is not null) foreach (KeyValuePair<string, string?> entry in variables) snapshot.Add(entry.Key, entry.Value);
        return new ParseSettings(snapshot.GetValueOrDefault(CommandOutputClassifier.EnvironmentVariableName))
        {
            GetEnvironmentVariable = name => snapshot.GetValueOrDefault(name),
        };
    }
}

/// <summary>Creates deterministic parsed invocations for command tests.</summary>
public static class CommandTestInvocation
{
    /// <summary>Parses arguments and returns the successful invocation.</summary>
    public static ParsedInvocation Parse(CommandCatalog catalog, ParseSettings settings, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(args);
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(catalog, args, settings);
        return outcome.Invocation ?? throw new InvalidOperationException("The supplied test arguments did not produce an invocation.");
    }

    /// <summary>Parses arguments with the default captured settings.</summary>
    public static ParsedInvocation Parse(CommandCatalog catalog, params string[] args) => Parse(catalog, ParseSettings.Default, args);
}

/// <summary>Reads the stable command response envelope in tests.</summary>
public static class CommandTestEnvelope
{
    /// <summary>Parses exactly one JSON command response frame.</summary>
    public static JsonDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        int lineFeedCount = 0;
        foreach (char character in json) if (character == '\n') lineFeedCount++;
        if (json.Length < 2 || !json.EndsWith('\n') || lineFeedCount != 1 || char.IsWhiteSpace(json[0]) || char.IsWhiteSpace(json[^2])) throw new ArgumentException("A command JSON frame must be one unpadded JSON value with exactly one terminal LF.", nameof(json));
        return JsonDocument.Parse(json);
    }
}
