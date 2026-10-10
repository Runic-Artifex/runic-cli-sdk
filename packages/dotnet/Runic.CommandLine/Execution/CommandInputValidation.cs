using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Runic.CommandLine;

internal static class CommandInputValidation
{
    // Option relationships and paths are checked before binding. Generated binders check numeric
    // bounds per value after conversion and before [ValidateWith] (GeneratedCommandBinding.CheckRange);
    // ValidateRanges applies the same bounds after a hand-written binder succeeds. Either way a value
    // of the wrong type reports its type (RCLI2005) rather than the range.
    internal static CommandOutcome<T>? Validate<T>(ParsedInvocation invocation)
    {
        var present = new HashSet<string>(invocation.Options.Select(binding => binding.Id), StringComparer.Ordinal);
        foreach (CommandOptionDescriptor option in invocation.Command.Options)
        {
            if (!present.Contains(option.Id)) continue;
            foreach (string required in option.Help.Requires)
                if (!present.Contains(required)) return Fault<T>(invocation, option.Id, "option-requires", Name(required));
            foreach (string conflict in option.Help.ConflictsWith)
                if (present.Contains(conflict)) return Fault<T>(invocation, option.Id, "option-conflict", Name(conflict));
            CommandOutcome<T>? fault = Paths<T>(invocation, option.Id, GeneratedCommandBinding.Options(invocation, option.Id), option.Help);
            if (fault is not null) return fault;
        }
        foreach (CommandArgumentDescriptor argument in invocation.Command.Arguments)
        {
            CommandOutcome<T>? fault = Paths<T>(invocation, argument.Id, GeneratedCommandBinding.Arguments(invocation, argument.Id), argument.Help);
            if (fault is not null) return fault;
        }
        return null;
        string Name(string id) => invocation.Command.Options.First(option => option.Id == id).Name;
    }

    internal static CommandOutcome<T>? ValidateRanges<T>(ParsedInvocation invocation)
    {
        foreach (CommandOptionDescriptor option in invocation.Command.Options)
        {
            CommandOutcome<T>? fault = Range<T>(invocation, option.Id, GeneratedCommandBinding.Options(invocation, option.Id), option.Help);
            if (fault is not null) return fault;
        }
        foreach (CommandArgumentDescriptor argument in invocation.Command.Arguments)
        {
            CommandOutcome<T>? fault = Range<T>(invocation, argument.Id, GeneratedCommandBinding.Arguments(invocation, argument.Id), argument.Help);
            if (fault is not null) return fault;
        }
        return null;
    }

    private static CommandOutcome<T>? Range<T>(ParsedInvocation invocation, string id, IReadOnlyList<string> values, CommandHelp help) =>
        RangeError(values, help) is var (kind, details) ? Fault<T>(invocation, id, kind, details) : null;

    // Returns the diagnostic kind and details for the first value outside the declared bounds.
    internal static (string Kind, string[] Details)? RangeError(IReadOnlyList<string> values, CommandHelp help)
    {
        if (help.Minimum is null && help.Maximum is null) return null;
        foreach (string value in values)
        {
            if (!double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                return ("invalid-number", []);
            if ((help.Minimum is { } min && number < min) || (help.Maximum is { } max && number > max))
            {
                return help switch
                {
                    { Minimum: { } lower, Maximum: { } upper } => ("out-of-range", [CommandValueDiagnostics.Number(lower), CommandValueDiagnostics.Number(upper)]),
                    { Minimum: { } lower } => ("below-minimum", [CommandValueDiagnostics.Number(lower)]),
                    _ => ("above-maximum", [CommandValueDiagnostics.Number(help.Maximum!.Value)]),
                };
            }
        }
        return null;
    }

    private static CommandOutcome<T>? Paths<T>(ParsedInvocation invocation, string id, IReadOnlyList<string> values, CommandHelp help)
    {
        if (help.PathKind == CommandPathKind.None) return null;
        bool file = help.PathKind == CommandPathKind.File;
        foreach (string value in values)
        {
            try { _ = Path.GetFullPath(value); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            { return Fault<T>(invocation, id, file ? "invalid-file-path" : "invalid-directory-path"); }
            if (help.MustExist && !(file ? File.Exists(value) : Directory.Exists(value)))
                return Fault<T>(invocation, id, file ? "file-not-found" : "directory-not-found");
        }
        return null;
    }

    private static CommandOutcome<T> Fault<T>(ParsedInvocation invocation, string id, string kind, params string[] details) =>
        CommandValueDiagnostics.Failure<T>(invocation, CommandValueDiagnostics.ValidationCode, id, kind, details);
}
