using System;
using System.Collections.Generic;
using System.Text;

namespace Runic.CommandLine;

// Spells commands, options and framework requests in a catalog's dialect for help, hints and diagnostics, and
// normalizes a command line in a dialect into the portable grammar, token for token, so token indexes are unchanged.
internal static class CommandDialect
{
    internal static bool IsSlash(CommandCatalog catalog) => catalog.Syntax == CommandSyntaxDialect.WindowsSlash;

    // The option as the dialect writes it: /count for --count, /n for -n and /json for /json under the slash dialect.
    internal static string Option(CommandSyntaxDialect syntax, string spelling) =>
        syntax == CommandSyntaxDialect.WindowsSlash ? "/" + Bare(spelling) : spelling;

    internal static string Option(CommandCatalog catalog, string spelling) => Option(catalog.Syntax, spelling);

    // The spelling without its prefix: count for --count, c for -c, json for /json.
    internal static string Bare(string spelling) =>
        spelling.StartsWith("--", StringComparison.Ordinal) ? spelling[2..] :
        spelling.Length > 1 && spelling[0] is '-' or '/' ? spelling[1..] : spelling;

    internal static string Command(CommandCatalog catalog, CommandDescriptor command) => Word(catalog, command.Spelling);

    // A command word, such as a command alias, in the dialect.
    internal static string Word(CommandCatalog catalog, string word) => IsSlash(catalog) ? "/" + word : word;

    // A canonical command path as the dialect writes it, with display spellings: /policy /resetAll.
    internal static string Path(CommandCatalog catalog, CommandPath path)
    {
        var text = new StringBuilder();
        CommandDescriptor? command = null;
        foreach (string segment in path.Segments)
        {
            CommandDescriptor? next = null;
            if (command is null) catalog.TryGetCommand(segment, out next);
            else command.TryGetSubcommand(segment, out next);
            command = next;
            if (text.Length > 0) text.Append(' ');
            text.Append(next is null ? Word(catalog, segment) : Command(catalog, next));
        }

        return text.ToString();
    }

    internal static string Help(CommandCatalog catalog) => IsSlash(catalog) ? "/?" : "--help";

    internal static string HelpTerm(CommandCatalog catalog) => IsSlash(catalog) ? "/?, /help" : "-h, --help";

    internal static string Version(CommandCatalog catalog) => Option(catalog, "--version");

    // A catalog whose command lines need rewriting before the portable grammar applies. Display spellings alone do
    // not: commands match them through their descriptors.
    internal static bool Normalizes(CommandCatalog catalog) => IsSlash(catalog) || catalog.IsCaseInsensitive;

    /// <summary>
    /// Rewrites the command words of the command path and the framework requests (help, version, output selector,
    /// JSON output alias, completion option) into their portable canonical spellings. Command options are left as
    /// given: the descriptors resolve their slash and case-insensitive spellings. Every token after <c>--</c>, and every
    /// token that spells nothing the catalog knows, is unchanged.
    /// </summary>
    internal static string[] Normalize(CommandCatalog catalog, IReadOnlyList<string> args)
    {
        string[] tokens = [.. args];
        if (!Normalizes(catalog)) return tokens;
        bool slash = IsSlash(catalog);
        StringComparison comparison = catalog.IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        CommandDescriptor? level = null;
        bool inPath = true;
        bool helpWord = false;
        for (int index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index];
            if (token is null || string.Equals(token, "--", StringComparison.Ordinal)) break;
            if (inPath)
            {
                if (CommandWord(token, slash) is { } word)
                {
                    CommandDescriptor? match = null;
                    if (level is null) catalog.TryGetCommand(word, out match);
                    else level.TryGetSubcommand(word, out match);
                    if (match is not null)
                    {
                        tokens[index] = match.Name;
                        level = match;
                        continue;
                    }

                    // "help policy link" asks for the help of the path that follows it.
                    if (level is null && !helpWord && string.Equals(word, "help", comparison))
                    {
                        tokens[index] = "help";
                        helpWord = true;
                        continue;
                    }

                    if (level is null && !helpWord && catalog.HasCompletionCommand && string.Equals(word, "completion", comparison))
                    {
                        tokens[index] = "completion";
                        inPath = false;
                        continue;
                    }
                }

                // Output and global options may precede the command; their values are never command words.
                if (level is null)
                {
                    if (TryFramework(catalog, token, slash, comparison, out string? canonical, out bool takesValue))
                    {
                        tokens[index] = canonical!;
                        if (takesValue) index++;
                        continue;
                    }

                    if (IsGlobalOption(catalog, token, slash, out bool globalTakesValue))
                    {
                        if (globalTakesValue) index++;
                        continue;
                    }
                }

                inPath = false;
            }

            if (TryFramework(catalog, token, slash, comparison, out string? framework, out _)) tokens[index] = framework!;
        }

        return tokens;
    }

    // The word a token gives where a command belongs: "policy", and "/policy" under the slash dialect.
    private static string? CommandWord(string token, bool slash)
    {
        if (token.Length == 0) return null;
        if (slash && token[0] == '/') return token.Length > 1 && token.IndexOf('=', StringComparison.Ordinal) < 0 ? token[1..] : null;
        return token[0] is '-' or '/' or '@' ? null : token;
    }

    private static bool IsGlobalOption(CommandCatalog catalog, string token, bool slash, out bool takesValue)
    {
        takesValue = false;
        if (token.Length < 2 || token[0] is not ('-' or '/')) return false;
        int separator = token.IndexOf('=', StringComparison.Ordinal);
        string name = separator < 0 ? token : token[..separator];
        foreach (CommandDescriptor command in catalog.Commands)
        {
            CommandOptionDescriptor? option = null;
            if ((name[0] == '/' && slash ? command.TryGetSlashOption(name, out option) : command.TryGetOption(name, out option)) && option is { IsGlobal: true })
            {
                takesValue = separator < 0 && option.Arity.Maximum != 0;
                return true;
            }
        }

        return false;
    }

    // Help, version, the output selector and its hidden aliases, the JSON output alias and the completion option,
    // in their portable spelling, under either dialect's spelling and the catalog's case sensitivity.
    private static bool TryFramework(CommandCatalog catalog, string token, bool slash, StringComparison comparison, out string? canonical, out bool takesValue)
    {
        canonical = null;
        takesValue = false;
        if (token.Length < 2 || token[0] is not ('-' or '/')) return false;
        if (slash && token is "/?")
        {
            canonical = "--help";
            return true;
        }

        if (Matches(token, "--help", slash, comparison) || Matches(token, "-h", slash: false, comparison))
        {
            canonical = Matches(token, "-h", slash: false, comparison) ? "-h" : "--help";
            return true;
        }

        if (Matches(token, "--version", slash, comparison))
        {
            canonical = "--version";
            return true;
        }

        int separator = token.IndexOf('=', StringComparison.Ordinal);
        string name = separator < 0 ? token : token[..separator];
        string rest = separator < 0 ? "" : token[separator..];
        if (Matches(name, catalog.OutputSelector, slash, comparison))
        {
            canonical = catalog.OutputSelector + rest;
            takesValue = separator < 0;
            return true;
        }

        foreach (string alias in catalog.OutputSelectorAliases)
        {
            if (!Matches(name, alias, slash, comparison)) continue;
            canonical = alias + rest;
            takesValue = separator < 0;
            return true;
        }

        if (catalog.JsonOutputAlias is { } json && Matches(name, json, slash, comparison))
        {
            canonical = json + rest;
            return true;
        }

        if (catalog.CompletionOption is { } completion && Matches(name, completion, slash, comparison))
        {
            canonical = completion + rest;
            takesValue = separator < 0;
            return true;
        }

        return false;
    }

    private static bool Matches(string token, string spelling, bool slash, StringComparison comparison) =>
        string.Equals(token, spelling, comparison) ||
        (slash && token.Length > 1 && token[0] == '/' && token.AsSpan(1).Equals(Bare(spelling).AsSpan(), comparison));

    /// <summary>
    /// Rewrites the parse diagnostics of a slash-dialect catalog so they name options, commands and suggestions as
    /// the user spells them: "The /name option requires a value.", "Did you mean '/policy'?".
    /// </summary>
    internal static ParseOutcome Present(CommandCatalog catalog, ParseOutcome outcome)
    {
        if (!IsSlash(catalog) || outcome.Diagnostics.Count == 0) return outcome;
        var diagnostics = new CommandDiagnostic[outcome.Diagnostics.Count];
        for (int index = 0; index < diagnostics.Length; index++) diagnostics[index] = Present(catalog, outcome.Diagnostics[index]);
        return outcome.WithDiagnostics(diagnostics);
    }

    private static CommandDiagnostic Present(CommandCatalog catalog, CommandDiagnostic diagnostic)
    {
        if (diagnostic.Phase != CommandDiagnosticPhase.Parse || diagnostic.Arguments.Count == 0) return diagnostic;
        string[] arguments = [.. diagnostic.Arguments];
        bool hasSuggestion = string.Equals(diagnostic.MessageKey, CommandTextKeys.Diagnostic(diagnostic.Kind + "-suggestion"), StringComparison.Ordinal);
        string? suggestion = hasSuggestion ? arguments[^1] : null;
        if (hasSuggestion) arguments = arguments[..^1];
        switch (diagnostic.Kind)
        {
            case "missing-option-value" or "unexpected-option-value" or "duplicate-option" or "invalid-output-mode" or
                "invalid-environment-value" or "environment-value-source":
                arguments[0] = Option(catalog, arguments[0]);
                break;
            case "invalid-choice" when arguments[0].StartsWith('-') || arguments[0].StartsWith('/'):
                arguments[0] = Option(catalog, arguments[0]);
                break;
            case "missing-required-option":
                arguments[0] = Option(catalog, arguments[0]);
                if (arguments.Length > 1) arguments[1] = Path(catalog, diagnostic.Path);
                break;
            case "conflicting-output-selector":
                for (int index = 0; index < arguments.Length; index++) arguments[index] = Option(catalog, arguments[index]);
                break;
            case "unknown-option":
                if (suggestion is not null) suggestion = Option(catalog, suggestion);
                break;
            case "unknown-command":
                // The former output selector is suggested as an option; any other suggestion is a command, matched
                // again without the token's slash and spelled as help shows it.
                suggestion = suggestion is not null && suggestion.StartsWith('-')
                    ? Option(catalog, suggestion)
                    : SuggestCommand(catalog, diagnostic.Path, arguments[0]);
                break;
            default:
                return diagnostic;
        }

        string message = PortableCommandSyntaxAdapter.MessageText(diagnostic.Kind, arguments, suggestion);
        string? messageKey = diagnostic.Kind == "environment-value-source" ? null : CommandValueDiagnostics.MessageKey(diagnostic.Kind, arguments);
        if (suggestion is not null)
        {
            arguments = [.. arguments, suggestion];
            messageKey = CommandTextKeys.Diagnostic(diagnostic.Kind + "-suggestion");
        }

        return new CommandDiagnostic(diagnostic.Code, diagnostic.Kind, message, diagnostic.Phase, diagnostic.Severity,
            diagnostic.TokenIndex, arguments, diagnostic.Path, messageKey) { HelpUri = diagnostic.HelpUri };
    }

    private static string? SuggestCommand(CommandCatalog catalog, CommandPath path, string token)
    {
        IReadOnlyList<CommandDescriptor> siblings = catalog.Commands;
        if (path.Count > 0)
        {
            CommandDescriptor? command = null;
            foreach (string segment in path.Segments)
            {
                if (command is null) catalog.TryGetCommand(segment, out command);
                else command.TryGetSubcommand(segment, out command);
                if (command is null) return null;
            }

            siblings = command!.Subcommands;
        }

        string word = token.Length > 1 && token[0] == '/' ? token[1..] : token;
        return CommandSuggestions.Commands(word, siblings) is { } match ? Word(catalog, match) : null;
    }
}
