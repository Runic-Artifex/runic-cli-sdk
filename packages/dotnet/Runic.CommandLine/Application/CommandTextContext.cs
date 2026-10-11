using System;
using System.Collections.Generic;
using System.Globalization;

namespace Runic.CommandLine;

/// <summary>Resolves presentation text without imposing a localization library or changing machine identities.</summary>
public interface ICommandTextResolver
{
    /// <summary>Returns localized text for a key and ordered safe arguments, or null to use the supplied fallback.</summary>
    string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments);
}

/// <summary>Immutable culture and text resolution for one presentation.</summary>
public sealed class CommandTextContext
{
    /// <summary>Initializes a context with a defensively copied culture and optional resolver.</summary>
    public CommandTextContext(CultureInfo culture, ICommandTextResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(culture);
        Culture = CultureInfo.ReadOnly((CultureInfo)culture.Clone());
        Resolver = resolver;
    }
    /// <summary>Gets the presentation culture.</summary>
    public CultureInfo Culture { get; }
    /// <summary>Gets the optional application-owned text resolver.</summary>
    public ICommandTextResolver? Resolver { get; }
    /// <summary>Gets the default English help context.</summary>
    public static CommandTextContext English { get; } = new(CultureInfo.GetCultureInfo("en"));
    /// <summary>Resolves a key, preserving the literal fallback when no translation exists.</summary>
    public string Resolve(string? key, string fallback, IReadOnlyList<string>? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (key is null || Resolver is null) return fallback;
        string? value = Resolver.Resolve(key, Culture, arguments ?? Array.Empty<string>());
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
    /// <summary>Resolves a description using its literal help text as fallback; keys are never displayed as prose.</summary>
    public string Description(CommandHelp help, string? descriptionKey)
    {
        ArgumentNullException.ThrowIfNull(help);
        return Resolve(descriptionKey, help.Description ?? string.Empty);
    }
    /// <summary>Reports each command, option and argument <c>DescriptionKey</c> the resolver does not resolve in this culture.</summary>
    /// <remarks>
    /// An unresolved key falls back to the literal description, so a typo would otherwise go unnoticed. Each issue has code
    /// <c>RCLI0023</c> and the command path as its location. Without a resolver there is nothing to check and the list is empty.
    /// Call this from a test, or set <c>RUNIC_COMMANDLINE_DEBUG=1</c> to have <see cref="CommandApp"/> warn when it shows help.
    /// </remarks>
    public IReadOnlyList<CommandCatalogIssue> ValidateDescriptionKeys(CommandCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var issues = new List<CommandCatalogIssue>();
        if (Resolver is null) return issues;
        var globals = new HashSet<string>(StringComparer.Ordinal);
        foreach (CommandDescriptor command in catalog.Commands) Visit(command, command.Name);
        return issues.AsReadOnly();

        void Visit(CommandDescriptor command, string path)
        {
            Check(command.DescriptionKey, path, "command '" + path + "'");
            foreach (CommandOptionDescriptor option in command.Options)
                if (!option.IsGlobal || globals.Add(option.Id)) Check(option.DescriptionKey, path, "option '" + option.Name + "'");
            foreach (CommandArgumentDescriptor argument in command.Arguments) Check(argument.DescriptionKey, path, "argument '" + argument.Name + "'");
            foreach (CommandDescriptor child in command.Subcommands) Visit(child, path + " " + child.Name);
        }

        void Check(string? key, string path, string subject)
        {
            if (key is null || !string.IsNullOrWhiteSpace(Resolver!.Resolve(key, Culture, Array.Empty<string>()))) return;
            issues.Add(new CommandCatalogIssue("RCLI0023", path,
                $"Description key '{key}' for {subject} did not resolve in culture '{Culture.Name}'; the literal description is shown instead."));
        }
    }
    /// <summary>Resolves a diagnostic message while preserving its codes, arguments, phase and canonical path.</summary>
    public CommandDiagnostic Localize(CommandDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        string message = Resolve(diagnostic.MessageKey, diagnostic.Message, diagnostic.Arguments);
        return message == diagnostic.Message ? diagnostic : new CommandDiagnostic(diagnostic.Code, diagnostic.Kind, message,
            diagnostic.Phase, diagnostic.Severity, diagnostic.TokenIndex, diagnostic.Arguments, diagnostic.Path, diagnostic.MessageKey) { HelpUri = diagnostic.HelpUri };
    }
    internal CommandResponse<T> Localize<T>(CommandResponse<T> response)
    {
        if (Resolver is null) return response;
        var diagnostics = new CommandDiagnostic[response.Diagnostics.Count];
        CommandFault? fault = response.Fault;
        for (int index = 0; index < diagnostics.Length; index++)
        {
            CommandDiagnostic original = response.Diagnostics[index];
            diagnostics[index] = Localize(original);
            bool describesFault = fault is not null && original.Code == fault.Code && original.Message == fault.Message;
            // A translation of faults.{code} still applies when the more specific diagnostic key has none.
            if (describesFault && ReferenceEquals(diagnostics[index], original) &&
                Resolver.Resolve("faults." + original.Code, Culture, Array.Empty<string>()) is { } faultText && !string.IsNullOrWhiteSpace(faultText))
                diagnostics[index] = new CommandDiagnostic(original.Code, original.Kind, faultText, original.Phase, original.Severity, original.TokenIndex,
                    original.Arguments, original.Path, original.MessageKey) { HelpUri = original.HelpUri };
            if (describesFault && fault is not null)
                fault = new CommandFault(fault.Code, diagnostics[index].Message, fault.Details, fault.Retryable) { HelpUri = fault.HelpUri };
        }
        if (fault is not null && ReferenceEquals(fault, response.Fault))
            fault = new CommandFault(fault.Code, Resolve("faults." + fault.Code, fault.Message), fault.Details, fault.Retryable) { HelpUri = fault.HelpUri };
        return CommandResponse<T>.Read(response.RequestId, response.Command, response.Success, response.ExitCode,
            response.PayloadType, response.Payload, fault, diagnostics, response.FailureData);
    }
}
