using System;
using System.Collections.Generic;
using System.Globalization;

namespace Runic.CommandLine;

/// <summary>Reads source-generator supported values from a parser-neutral invocation.</summary>
public static class GeneratedCommandBinding
{
    /// <summary>Gets a required positional value.</summary>
    public static string Argument(ParsedInvocation invocation, string id) => Get(invocation.Arguments, id);

    /// <summary>Gets all trailing positional values in encounter order.</summary>
    public static IReadOnlyList<string> Arguments(ParsedInvocation invocation, string id)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return GetValues(invocation.Arguments, id);
    }

    /// <summary>Gets a required option value.</summary>
    public static string Option(ParsedInvocation invocation, string id) => Get(invocation.Options, id);

    /// <summary>Gets all repeated option values in encounter order.</summary>
    public static IReadOnlyList<string> Options(ParsedInvocation invocation, string id)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return GetValues(invocation.Options, id);
    }

    private static IReadOnlyList<string> GetValues(IReadOnlyList<CommandValueBinding> bindings, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        foreach (CommandValueBinding binding in bindings)
        {
            if (string.Equals(binding.Id, id, StringComparison.Ordinal)) return binding.Values;
        }
        return Array.Empty<string>();
    }

    /// <summary>Gets whether a Boolean flag was specified.</summary>
    public static bool Flag(ParsedInvocation invocation, string id)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        foreach (CommandValueBinding binding in invocation.Options)
        {
            if (string.Equals(binding.Id, id, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Converts an invariant integral command value.</summary>
    public static int ParseInt32(string value, string id) => Parse(value, id, "invalid-integer", static text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? (true, result) : (false, 0));

    /// <summary>Converts an invariant integral command value.</summary>
    public static long ParseInt64(string value, string id) => Parse(value, id, "invalid-integer", static text => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? (true, result) : (false, 0));

    /// <summary>Converts an invariant decimal command value.</summary>
    public static decimal ParseDecimal(string value, string id) => Parse(value, id, "invalid-number", static text => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal result) ? (true, result) : (false, 0));

    /// <summary>Converts an invariant floating-point command value.</summary>
    public static double ParseDouble(string value, string id) => Parse(value, id, "invalid-number", static text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? (true, result) : (false, 0));

    /// <summary>Converts a Boolean value supplied to a nullable Boolean option.</summary>
    public static bool ParseBoolean(string value, string id) => Parse(value, id, "invalid-boolean", static text => bool.TryParse(text, out bool result) ? (true, result) : (false, false));

    /// <summary>Converts a GUID command value.</summary>
    public static Guid ParseGuid(string value, string id) => Parse(value, id, "invalid-guid", static text => System.Guid.TryParse(text, out Guid result) ? (true, result) : (false, default));

    /// <summary>Converts a named enum value, rejecting undefined numeric values.</summary>
    public static T ParseEnum<T>(string value, string id) where T : struct, Enum
    {
        if (Enum.TryParse(value, ignoreCase: true, out T result) && Enum.IsDefined(result)) return result;
        throw new GeneratedCommandBindingException(id, $"Invalid value for '{id}': expected one of {string.Join(", ", Enum.GetNames<T>())}.", "invalid-choice", [string.Join(", ", Enum.GetNames<T>())]);
    }

    /// <summary>Converts a value list without runtime type discovery.</summary>
    public static T[] ParseValues<T>(IReadOnlyList<string> values, Func<string, T> convert)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(convert);
        var result = new T[values.Count];
        for (int i = 0; i < values.Count; i++) result[i] = convert(values[i]);
        return result;
    }

    /// <summary>Calls an explicitly selected converter, containing untrusted exception details.</summary>
    public static T Convert<T, TConverter>(string value, string id) where TConverter : ICommandValueConverter<T>
    {
        try { return TConverter.Parse(value); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException or OperationCanceledException))
        { throw new GeneratedCommandBindingException(id, $"Invalid value for '{id}'.", "invalid-value"); }
    }

    /// <summary>Calls an explicitly selected validator and returns the validated value.</summary>
    public static T Validate<T, TValidator>(T value, string id) where TValidator : ICommandValueValidator<T>
    {
        if (TValidator.IsValid(value)) return value;
        throw new GeneratedCommandBindingException(id, $"Validation failed for '{id}'.", "validation-failed");
    }

    private static string Get(IReadOnlyList<CommandValueBinding> bindings, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        foreach (CommandValueBinding binding in bindings)
        {
            if (string.Equals(binding.Id, id, StringComparison.Ordinal) && binding.Values.Count == 1) return binding.Values[0];
        }

        throw new GeneratedCommandBindingException(id, $"A value is required for '{id}'.", "missing-value");
    }

    /// <summary>Creates the usage outcome for a binding failure, naming the parameter as typed and, for an environment fallback, its variable.</summary>
    /// <remarks>The <c>RCLI2005</c> fault carries a binding diagnostic whose key is <c>diagnostics.{kind}</c>, for example <c>diagnostics.invalid-integer</c>.</remarks>
    public static CommandOutcome<TOptions> Failure<TOptions>(ParsedInvocation invocation, GeneratedCommandBindingException exception)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(exception);
        string[] details = exception.Kind == "invalid-choice" && SensitiveOrLong(invocation.Command, exception.ParameterId, exception.Details)
            ? [] : exception.Details;
        return CommandValueDiagnostics.Failure<TOptions>(invocation, CommandValueDiagnostics.BindingCode, exception.ParameterId, exception.Kind, details);
    }

    private static bool SensitiveOrLong(CommandDescriptor command, string id, string[] details)
    {
        bool sensitive = CommandValueDiagnostics.FindOption(command, id)?.IsSensitive ?? false;
        foreach (CommandArgumentDescriptor argument in command.Arguments) sensitive |= argument.Id == id && argument.IsSensitive;
        return details.Length == 0 || CommandValueDiagnostics.Choices(details[0].Split(", "), sensitive) is null;
    }

    private static T Parse<T>(string value, string id, string kind, TryParser<T> parser)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (parser(value) is (true, T result)) return result;
        throw new GeneratedCommandBindingException(id, $"Invalid value for '{id}': expected {typeof(T).Name}.", kind);
    }

    private delegate (bool Success, T Value) TryParser<T>(string value);
}

/// <summary>Represents a safe source-generated binding failure.</summary>
public sealed class GeneratedCommandBindingException : Exception
{
    /// <summary>Initializes a binding failure.</summary>
    public GeneratedCommandBindingException(string parameterId, string message) : this(parameterId, message, "invalid-value")
    {
    }

    internal GeneratedCommandBindingException(string parameterId, string message, string kind, string[]? details = null) : base(message)
    {
        ParameterId = parameterId;
        Kind = kind;
        Details = details ?? [];
    }

    // The diagnostic kind and the arguments that follow the parameter name.
    internal string Kind { get; }

    internal string[] Details { get; }

    /// <summary>Gets the stable catalog parameter identifier.</summary>
    public string ParameterId { get; }
}
