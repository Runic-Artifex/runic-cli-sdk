using System;

namespace Runic.CommandLine;

/// <summary>Identifies the requested command result presentation.</summary>
public enum CommandOutputMode
{
    /// <summary>Localized presentation intended for a person.</summary>
    Human = 0,

    /// <summary>A single versioned JSON response envelope intended for a machine.</summary>
    Json = 1,
}

/// <summary>Identifies the winning source of an output-mode classification.</summary>
public enum CommandOutputModeSource
{
    /// <summary>The caller-provided default was used.</summary>
    Default = 0,

    /// <summary>The <c>RUNIC_COMMANDLINE_OUTPUT</c> environment value was used.</summary>
    Environment = 1,

    /// <summary>An explicit command-line argument was used.</summary>
    ExplicitArgument = 2,
}

/// <summary>Identifies what JSON output contains. See <see cref="CommandCatalogBuilder.JsonOutputAlias(string, JsonOutputShape)"/>.</summary>
public enum JsonOutputShape
{
    /// <summary>One <c>runic.commandline/1</c> response envelope, as the output selector writes it.</summary>
    Envelope = 0,

    /// <summary>
    /// The command's own JSON document without the envelope: the success payload, or the declared failure data on
    /// failure. Framework help, version and usage errors are human text.
    /// </summary>
    Payload = 1,
}

/// <summary>
/// Represents the result of resolving explicit, environment, and default
/// output-mode configuration.
/// </summary>
public readonly record struct CommandOutputClassification
{
    internal CommandOutputClassification(
        bool isValid,
        CommandOutputMode? mode,
        CommandOutputModeSource? source,
        string? invalidEnvironmentValue,
        JsonOutputShape shape = JsonOutputShape.Envelope)
    {
        IsValid = isValid;
        Mode = mode;
        Source = source;
        InvalidEnvironmentValue = invalidEnvironmentValue;
        Shape = shape;
    }

    /// <summary>Gets a value indicating whether classification succeeded.</summary>
    public bool IsValid { get; }

    /// <summary>Gets the selected mode, or null when classification failed.</summary>
    public CommandOutputMode? Mode { get; }

    /// <summary>Gets the winning source, or null when classification failed.</summary>
    public CommandOutputModeSource? Source { get; }

    /// <summary>
    /// Gets the unrecognized environment value when classification failed;
    /// otherwise, null.
    /// </summary>
    public string? InvalidEnvironmentValue { get; }

    /// <summary>
    /// Gets what JSON output contains: <see cref="JsonOutputShape.Payload"/> when the command line gave the catalog's
    /// JSON output alias declared with that shape, otherwise <see cref="JsonOutputShape.Envelope"/>. The output
    /// selector and <c>RUNIC_COMMANDLINE_OUTPUT</c> always select the envelope.
    /// </summary>
    public JsonOutputShape Shape { get; }

    internal CommandOutputClassification WithShape(JsonOutputShape shape) =>
        new(IsValid, Mode, Source, InvalidEnvironmentValue, shape);
}

/// <summary>
/// Applies the stable precedence contract for command output presentation.
/// </summary>
public static class CommandOutputClassifier
{
    /// <summary>The environment variable that may supply a default output mode.</summary>
    public const string EnvironmentVariableName = "RUNIC_COMMANDLINE_OUTPUT";

    /// <summary>
    /// Resolves output mode without reading or mutating process-global state.
    /// </summary>
    /// <param name="explicitMode">
    /// The mode supplied explicitly by command syntax, if any. It has highest
    /// precedence and causes <paramref name="environmentValue"/> to be ignored.
    /// </param>
    /// <param name="environmentValue">
    /// The captured value of <see cref="EnvironmentVariableName"/>. Null or an
    /// empty string is treated as absent. Recognized values are <c>human</c>
    /// and <c>json</c>, compared using ordinal case-insensitive comparison.
    /// </param>
    /// <param name="defaultMode">The mode used when neither higher-precedence source is present.</param>
    /// <returns>A successful selection, or an invalid-environment classification.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="explicitMode"/> or <paramref name="defaultMode"/> is not defined.
    /// </exception>
    public static CommandOutputClassification Classify(
        CommandOutputMode? explicitMode,
        string? environmentValue,
        CommandOutputMode defaultMode = CommandOutputMode.Human)
    {
        if (explicitMode is CommandOutputMode selected)
        {
            ValidateMode(selected, nameof(explicitMode));
            return Valid(selected, CommandOutputModeSource.ExplicitArgument);
        }

        if (string.IsNullOrEmpty(environmentValue))
        {
            ValidateMode(defaultMode, nameof(defaultMode));
            return Valid(defaultMode, CommandOutputModeSource.Default);
        }

        if (string.Equals(environmentValue, "human", StringComparison.OrdinalIgnoreCase))
        {
            return Valid(CommandOutputMode.Human, CommandOutputModeSource.Environment);
        }

        if (string.Equals(environmentValue, "json", StringComparison.OrdinalIgnoreCase))
        {
            return Valid(CommandOutputMode.Json, CommandOutputModeSource.Environment);
        }

        return new CommandOutputClassification(false, null, null, environmentValue);
    }

    private static CommandOutputClassification Valid(
        CommandOutputMode mode,
        CommandOutputModeSource source) => new(true, mode, source, null);

    private static void ValidateMode(CommandOutputMode mode, string parameterName)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
