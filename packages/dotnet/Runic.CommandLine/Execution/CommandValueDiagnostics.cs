using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Runic.CommandLine;

// Builds the localizable usage diagnostics for invalid values. Each kind resolves through
// diagnostics.{kind} (or an explicit key) with ordered arguments whose first item is the
// parameter as typed: an option spelling such as --limit or an argument such as <name>.
internal static class CommandValueDiagnostics
{
    internal const string ValidationCode = "RCLI2002";
    internal const string BindingCode = "RCLI2005";
    internal const string EnvironmentSourceCode = "RCLI2006";
    private const int MaximumChoicesLength = 512;

    internal static CommandOutcome<T> Failure<T>(ParsedInvocation invocation, string code, string id, string kind, params string[] details)
    {
        IReadOnlyList<CommandDiagnostic> diagnostics = Diagnostics(invocation, code, id, kind, details);
        return CommandOutcome.Failure<T>(CommandExitCategory.Usage, new CommandFault(code, diagnostics[0].Message), diagnostics);
    }

    internal static IReadOnlyList<CommandDiagnostic> Diagnostics(ParsedInvocation invocation, string code, string id, string kind, params string[] details)
    {
        string name = DisplayName(invocation.Command, id);
        string[] arguments = [name, .. details];
        var diagnostics = new List<CommandDiagnostic>(2)
        {
            new(code, kind, Format(kind, arguments), CommandDiagnosticPhase.Binding, CommandDiagnosticSeverity.Error, arguments: arguments, path: invocation.Path,
                messageKey: MessageKey(kind, arguments)),
        };
        // A value from an environment fallback is invisible on the command line, so name its source.
        if (invocation.EnvironmentOptionIds.Contains(id) && FindOption(invocation.Command, id) is { Help.EnvironmentVariable: { } variable })
        {
            diagnostics.Add(new CommandDiagnostic(EnvironmentSourceCode, "environment-value-source", Format("environment-value-source", [name, variable]),
                CommandDiagnosticPhase.Binding, CommandDiagnosticSeverity.Information, arguments: [name, variable], path: invocation.Path));
        }
        return diagnostics;
    }

    internal static string DisplayName(CommandDescriptor command, string id)
    {
        if (FindOption(command, id) is { } option) return option.Name;
        foreach (CommandArgumentDescriptor argument in command.Arguments)
        {
            if (argument.Id == id) return "<" + (argument.Help.ValueName ?? argument.Name) + ">";
        }
        return id;
    }

    internal static CommandOptionDescriptor? FindOption(CommandDescriptor command, string id)
    {
        foreach (CommandOptionDescriptor option in command.Options)
        {
            if (option.Id == id) return option;
        }
        return null;
    }

    // A sensitive choice list is withheld, so its message has one argument and its own key.
    internal static string? MessageKey(string kind, IReadOnlyList<string> arguments) =>
        kind == "invalid-choice" && arguments.Count == 1 ? "diagnostics.invalid-choice-hidden" : null;

    // Lists declared choices unless the value is sensitive or the list is too long for one diagnostic argument.
    internal static string? Choices(IReadOnlyList<string> choices, bool sensitive)
    {
        if (sensitive || choices.Count == 0) return null;
        string list = string.Join(", ", choices);
        return list.Length > MaximumChoicesLength ? null : list;
    }

    internal static string Format(string kind, IReadOnlyList<string> arguments)
    {
        string template = kind switch
        {
            "invalid-integer" => "{0} requires a whole number.",
            "invalid-number" => "{0} requires a number.",
            "invalid-guid" => "{0} requires a GUID such as 0f8fad5b-d9cb-469f-a165-70867728950e.",
            "invalid-boolean" => "{0} requires true or false.",
            "invalid-choice" when arguments.Count > 1 => "{0} must be one of: {1}.",
            "invalid-choice" => "{0} must be one of the choices shown in --help.",
            "invalid-value" => "{0} has an invalid value.",
            "validation-failed" => "{0} has a value that failed validation.",
            "missing-value" => "{0} requires a value.",
            "out-of-range" => "{0} requires a number between {1} and {2}.",
            "below-minimum" => "{0} requires a number of at least {1}.",
            "above-maximum" => "{0} requires a number of at most {1}.",
            "option-requires" => "{0} requires {1}.",
            "option-conflict" => "{0} cannot be combined with {1}.",
            "invalid-file-path" => "{0} requires a valid file path.",
            "invalid-directory-path" => "{0} requires a valid directory path.",
            "file-not-found" => "{0} requires an existing file; check the path and permissions.",
            "directory-not-found" => "{0} requires an existing directory; check the path and permissions.",
            "environment-value-source" => "The value for {0} came from environment variable {1}.",
            _ => "{0} has an invalid value.",
        };
        return string.Format(CultureInfo.InvariantCulture, template, [.. arguments]);
    }

    internal static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
}
