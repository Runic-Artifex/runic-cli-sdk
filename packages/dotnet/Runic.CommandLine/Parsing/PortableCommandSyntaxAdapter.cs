using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Runic.CommandLine;

/// <summary>
/// Implements the version 1 portable command grammar using only library-owned
/// descriptors and BCL facilities.
/// </summary>
/// <remarks>The adapter is stateless, thread-safe, deterministic, and Native-AOT compatible.</remarks>
public sealed class PortableCommandSyntaxAdapter : ICommandSyntaxAdapter
{
    private const string UnknownOptionCode = "RCLI1001";
    private const string UnknownCommandCode = "RCLI1002";
    private const string MissingOptionValueCode = "RCLI1003";
    private const string UnexpectedOptionValueCode = "RCLI1004";
    private const string MissingArgumentCode = "RCLI1005";
    private const string UnexpectedArgumentCode = "RCLI1006";
    private const string DuplicateOptionCode = "RCLI1007";
    private const string UnsupportedShortBundleCode = "RCLI1008";
    private const string InvalidOutputModeCode = "RCLI1010";
    private const string TransportOutputCollisionCode = "RCLI1011";
    private const string MissingRequiredOptionCode = "RCLI1012";
    private const string UnexpectedRootHelpArgumentCode = "RCLI1013";
    private const string ConflictingOutputSelectorCode = "RCLI1016";
    private const int MaximumDiagnosticArgumentLength = 128;

    /// <summary>Gets the shared stateless adapter.</summary>
    public static PortableCommandSyntaxAdapter Instance { get; } = new();

    /// <inheritdoc />
    public ParseOutcome Parse(
        CommandCatalog catalog,
        ReadOnlySpan<string> args,
        ParseSettings settings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);

        string[] tokens = args.ToArray();
        ValidateTokens(tokens);
        TransportOutput transport = TransportOutput.From(catalog);
        ResolvedCommand? preScanResolved = null;
        if (tokens.Length > 0)
        {
            _ = TryResolveCommand(catalog, tokens, out preScanResolved, transport);
        }

        TransportOutputScan transportOutput = ScanTransportOutput(
            tokens,
            transport,
            preScanResolved?.Command);
        CommandOutputClassification initialOutputClassification = ClassifyOutput(
            transportOutput.ExplicitOutputMode,
            settings);
        // The legacy contract of a payload-shaped alias covers the whole command line that gives it, including
        // its framework output, so the shape follows the flag rather than the selection it produced.
        if (transportOutput.AliasGiven && catalog.JsonOutputAliasShape == JsonOutputShape.Payload)
        {
            initialOutputClassification = initialOutputClassification.WithShape(JsonOutputShape.Payload);
        }

        // The transport is framework-owned. Validate its shape before command
        // resolution so a command error cannot make the selected presentation
        // depend on where the transport happens to occur in argv.
        if (transportOutput.Issue is TransportOutputIssue issue)
        {
            return Error(
                issue.Code,
                issue.Kind,
                issue.TokenIndex,
                arguments: issue.Arguments,
                outputClassification: initialOutputClassification);
        }

        if (tokens.Length == 0 && catalog.DefaultCommand is null)
        {
            return Error(UnknownCommandCode, "unknown-command", 0, outputClassification: initialOutputClassification);
        }

        var helpTokens = new List<string>();
        foreach (string token in tokens) helpTokens.Add(token);
        for (int i = 0; i < helpTokens.Count; i++)
        {
            if (helpTokens[i] == "--") break;
            if (TryGetGlobalOption(catalog, helpTokens[i], out CommandOptionDescriptor? globalHelp, out string? globalHelpValue))
            {
                helpTokens.RemoveAt(i);
                if (globalHelp!.Arity.Maximum != 0 && globalHelpValue is null && i < helpTokens.Count) helpTokens.RemoveAt(i);
                i--;
                continue;
            }
            if (TrySplitOutput(helpTokens[i], transport, out string? inline))
            {
                helpTokens.RemoveAt(i);
                if (inline is null && i < helpTokens.Count) helpTokens.RemoveAt(i);
                i--;
            }
        }
        if (helpTokens.Count > 1 && helpTokens[0] == "help")
        {
            string[] pathTokens = helpTokens.GetRange(1, helpTokens.Count - 1).ToArray();
            if (catalog.TryResolve(pathTokens, out CommandDescriptor? helpCommand, out int consumed) && consumed == pathTokens.Length)
            {
                TryResolveCommand(catalog, pathTokens, out ResolvedCommand? helpResolved, transport);
                return CompleteSpecial(initialOutputClassification, classification => ParseOutcome.FromHelp(new HelpRequest(helpResolved!.Path), classification));
            }
        }

        if (TryFindRootSpecial(
            catalog,
            tokens,
            transport,
            out RootSpecial rootSpecial,
            out int unexpectedHelpTokenIndex))
        {
            if (unexpectedHelpTokenIndex >= 0)
            {
                return Error(
                    UnexpectedRootHelpArgumentCode,
                    "unexpected-root-help-argument",
                    unexpectedHelpTokenIndex,
                    outputClassification: initialOutputClassification);
            }

            return rootSpecial == RootSpecial.Help
                ? CompleteSpecial(
                    initialOutputClassification,
                    classification => ParseOutcome.FromHelp(
                        new HelpRequest(CommandPath.Root),
                        classification))
                : CompleteSpecial(initialOutputClassification, ParseOutcome.FromVersion);
        }

        if (!TryResolveCommand(catalog, tokens, out ResolvedCommand? resolved, transport) || resolved is null)
        {
            // The argument is the token given where a command belongs, after any leading output or global
            // options; it is absent when the command line holds only those options.
            int commandIndex = LeadingCommandIndex(catalog, tokens, transport);
            return Error(
                UnknownCommandCode,
                "unknown-command",
                0,
                arguments: commandIndex < tokens.Length ? [SafeUnknownCommand(tokens[commandIndex])] : null,
                outputClassification: initialOutputClassification,
                suggestion: commandIndex < tokens.Length
                    ? FormerSelectorSuggestion(tokens, commandIndex, transport) ?? CommandSuggestions.Commands(tokens[commandIndex], catalog.Commands)
                    : null);
        }

        if (resolved.Command.TryGetOption(transport.Name, out _))
        {
            return Error(
                TransportOutputCollisionCode,
                "transport-output-option-collision",
                resolved.Consumed,
                resolved.Path,
                outputClassification: initialOutputClassification);
        }

        ParseOutcome parsed = ParseCommand(catalog, resolved, tokens, settings, transport, initialOutputClassification);
        return resolved.Command.IsGroup && parsed.Kind == ParseOutcomeKind.Invocation
            ? ParseOutcome.FromHelp(new HelpRequest(resolved.Path), initialOutputClassification)
            : parsed;
    }

    private static ParseOutcome ParseCommand(
        CommandCatalog catalog,
        ResolvedCommand resolved,
        string[] tokens,
        ParseSettings settings,
        TransportOutput transport,
        CommandOutputClassification selectedOutputClassification)
    {
        var positionalTokens = new List<IndexedToken>();
        CommandArgumentDescriptor? trailing = null;
        foreach (CommandArgumentDescriptor argument in resolved.Command.Arguments) if (argument.IsTrailing) trailing = argument;
        var trailingValues = new List<string>();
        var optionBindings = new List<MutableBinding>();
        var optionBindingIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        CommandOutputMode? explicitOutputMode = null;
        string? outputSpelling = null;
        bool recognizeOptions = true;
        int index = 0;

        CommandOutputClassification CurrentOutputClassification() => selectedOutputClassification;

        while (index < tokens.Length)
        {
            if (index == resolved.Start && resolved.Consumed > index) { index = resolved.Consumed; continue; }
            string token = tokens[index];
            if (recognizeOptions && string.Equals(token, "--", StringComparison.Ordinal))
            {
                recognizeOptions = false;
                index++;
                continue;
            }

            if (recognizeOptions && IsHelp(token))
            {
                if (!selectedOutputClassification.IsValid)
                {
                    return OutputError(selectedOutputClassification, 0, resolved.Path);
                }

                // The root command's help is the application's help.
                return ParseOutcome.FromHelp(
                    new HelpRequest(ReferenceEquals(resolved.Command, catalog.RootCommand) ? CommandPath.Root : resolved.Path),
                    selectedOutputClassification);
            }

            if (recognizeOptions && TrySplitOutput(token, transport, out string? inlineOutputValue, out string spelling))
            {
                if (outputSpelling is not null)
                {
                    return DuplicateOutputSelector(index, resolved.Path, outputSpelling, spelling, CurrentOutputClassification());
                }

                outputSpelling = spelling;
                int valueIndex = index;
                string value;
                if (transport.IsAlias(spelling))
                {
                    if (!string.Equals(token, spelling, StringComparison.Ordinal))
                    {
                        return Error(
                            UnexpectedOptionValueCode,
                            "unexpected-option-value",
                            index,
                            resolved.Path,
                            [spelling],
                            CurrentOutputClassification());
                    }

                    value = inlineOutputValue!;
                }
                else if (inlineOutputValue is null)
                {
                    index++;
                    if (index >= tokens.Length)
                    {
                        return Error(
                            MissingOptionValueCode,
                            "missing-option-value",
                            tokens.Length,
                            resolved.Path,
                            [transport.Name],
                            CurrentOutputClassification());
                    }

                    if (IsOptionBoundary(resolved.Command, tokens[index], transport))
                    {
                        return Error(
                            MissingOptionValueCode,
                            "missing-option-value",
                            index,
                            resolved.Path,
                            [transport.Name],
                            CurrentOutputClassification());
                    }

                    valueIndex = index;
                    value = tokens[index];
                }
                else
                {
                    value = inlineOutputValue;
                }

                if (!TryParseOutputMode(value, out CommandOutputMode mode))
                {
                    return Error(
                        InvalidOutputModeCode,
                        "invalid-output-mode",
                        valueIndex,
                        resolved.Path,
                        [transport.Name],
                        CurrentOutputClassification());
                }

                explicitOutputMode = mode;
                index++;
                continue;
            }

            bool negativeArgument = ArgumentAt(resolved.Command.Arguments, positionalTokens.Count)?.Help.AcceptsNegativeNumbers == true && token.StartsWith('-') && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            if (recognizeOptions && !negativeArgument && TryResolveOption(
                    resolved.Command,
                    token,
                    out CommandOptionDescriptor? option,
                    out string? inlineValue,
                    out OptionTokenKind tokenKind))
            {
                if (option is null)
                {
                    return tokenKind == OptionTokenKind.UnsupportedShortBundle
                        ? Error(
                            UnsupportedShortBundleCode,
                            "unsupported-short-bundle",
                            index,
                            resolved.Path,
                            outputClassification: CurrentOutputClassification())
                        : Error(
                            UnknownOptionCode,
                            "unknown-option",
                            index,
                            resolved.Path,
                            [SafeUnknownOption(token)],
                            CurrentOutputClassification(),
                            FormerSelectorSuggestion(tokens, index, transport) ??
                                CommandSuggestions.Options(token, resolved.Command, transport.Name, transport.JsonAlias));
                }

                if (optionBindingIndexes.TryGetValue(option.Id, out int bindingIndex) &&
                    option.RepeatPolicy == CommandOptionRepeatPolicy.Error)
                {
                    return Error(
                        DuplicateOptionCode,
                        "duplicate-option",
                        index,
                        resolved.Path,
                        [option.Name],
                        CurrentOutputClassification());
                }

                if (inlineValue is not null && option.Arity.Maximum == 0)
                {
                    return Error(
                        UnexpectedOptionValueCode,
                        "unexpected-option-value",
                        index,
                        resolved.Path,
                        [option.Name],
                        CurrentOutputClassification());
                }

                var occurrenceValues = new List<string>();
                if (inlineValue is not null)
                {
                    occurrenceValues.Add(inlineValue);
                }

                index++;
                ParseOutcome? valueError = ConsumeOptionValues(
                    resolved.Command,
                    resolved.Path,
                    option,
                    tokens,
                    ref index,
                    recognizeOptions,
                    transport,
                    occurrenceValues,
                    CurrentOutputClassification());
                if (valueError is not null)
                {
                    return valueError;
                }

                if (!option.Arity.Accepts(occurrenceValues.Count))
                {
                    return Error(
                        MissingOptionValueCode,
                        "missing-option-value",
                        tokens.Length,
                        resolved.Path,
                        [option.Name],
                        CurrentOutputClassification());
                }

                if (optionBindingIndexes.TryGetValue(option.Id, out bindingIndex))
                {
                    optionBindings[bindingIndex].Values.AddRange(occurrenceValues);
                }
                else
                {
                    optionBindingIndexes.Add(option.Id, optionBindings.Count);
                    optionBindings.Add(new MutableBinding(option.Id, occurrenceValues));
                }

                continue;
            }

            // After --, a command with trailing arguments gives every token to them rather than to its positional arguments.
            if (!recognizeOptions && trailing is not null) trailingValues.Add(token);
            else positionalTokens.Add(new IndexedToken(token, index));
            index++;
        }

        var environmentOptionIds = new List<string>();
        foreach (CommandOptionDescriptor option in resolved.Command.Options)
        {
            if (!optionBindingIndexes.ContainsKey(option.Id) && option.Help.EnvironmentVariable is { } variable && settings.GetEnvironmentVariable?.Invoke(variable) is { } environmentValue)
            {
                bool flag = option.Arity.Maximum == 0;
                bool enabled = false;
                if (flag && !TryParseEnvironmentFlag(environmentValue, out enabled))
                    return Error("RCLI1014", "invalid-environment-value", tokens.Length, resolved.Path, [option.Name, variable], CurrentOutputClassification());
                if (!flag || enabled)
                {
                    optionBindingIndexes.Add(option.Id, optionBindings.Count);
                    optionBindings.Add(new MutableBinding(option.Id, flag ? [] : [environmentValue]));
                    environmentOptionIds.Add(option.Id);
                }
            }
            if (option.IsRequired && !optionBindingIndexes.ContainsKey(option.Id))
            {
                return Error(
                    MissingRequiredOptionCode,
                    "missing-required-option",
                    tokens.Length,
                    resolved.Path,
                    [option.Name, resolved.Path.ToString()],
                    CurrentOutputClassification());
            }
        }

        if (resolved.Command.IsGroup && positionalTokens.Count > 0)
            return Error(UnknownCommandCode, "unknown-command", positionalTokens[0].Index, resolved.Path, [SafeUnknownCommand(positionalTokens[0].Value)],
                outputClassification: CurrentOutputClassification(), suggestion:CommandSuggestions.Commands(positionalTokens[0].Value, resolved.Command.Subcommands));

        ParseOutcome? argumentError = BindArguments(
            PositionalArguments(resolved.Command.Arguments),
            resolved.Path,
            positionalTokens,
            tokens.Length,
            CurrentOutputClassification(),
            out IReadOnlyList<CommandValueBinding>? argumentBindings);
        if (argumentError is not null)
        {
            // A default command swallows a mistyped command name as its first argument; when that
            // token resembles a root command, report the command typo instead.
            if (resolved.Consumed == 0 && catalog.DefaultCommand is not null && positionalTokens.Count > 0 &&
                argumentError.Diagnostics[0].Code == UnexpectedArgumentCode && argumentError.Diagnostics[0].TokenIndex == positionalTokens[0].Index &&
                CommandSuggestions.Commands(positionalTokens[0].Value, catalog.Commands) is { } suggestion)
            {
                return Error(UnknownCommandCode, "unknown-command", positionalTokens[0].Index,
                    arguments: [SafeUnknownCommand(positionalTokens[0].Value)], outputClassification: CurrentOutputClassification(), suggestion: suggestion);
            }

            return argumentError;
        }

        CommandOutputClassification outputClassification = selectedOutputClassification;
        if (!outputClassification.IsValid)
        {
            return OutputError(outputClassification, 0, resolved.Path);
        }

        foreach (MutableBinding binding in optionBindings)
        {
            CommandOptionDescriptor? descriptor = null;
            foreach (CommandOptionDescriptor candidate in resolved.Command.Options) if (candidate.Id == binding.Id) descriptor = candidate;
            string[]? canonical = null;
            if (descriptor is not null && !TryCanonicalizeChoices(binding.Values, descriptor.Help, out canonical))
                return Error("RCLI1015", "invalid-choice", tokens.Length, resolved.Path, ChoiceArguments(descriptor.Name, descriptor.Help, descriptor.IsSensitive), CurrentOutputClassification(),
                    environmentVariable: environmentOptionIds.Contains(descriptor.Id) ? descriptor.Help.EnvironmentVariable : null);
            if (canonical is not null)
            {
                binding.Values.Clear();
                binding.Values.AddRange(canonical);
            }
        }
        if (trailing is not null && trailingValues.Count > 0)
            argumentBindings = [.. argumentBindings!, new CommandValueBinding(trailing.Id, trailingValues.ToArray())];
        var canonicalArguments = new CommandValueBinding[argumentBindings!.Count];
        for (int argumentIndex = 0; argumentIndex < canonicalArguments.Length; argumentIndex++)
        {
            CommandValueBinding binding = argumentBindings[argumentIndex];
            canonicalArguments[argumentIndex] = binding;
            foreach (CommandArgumentDescriptor descriptor in resolved.Command.Arguments)
            {
                if (descriptor.Id != binding.Id) continue;
                if (!TryCanonicalizeChoices(binding.Values, descriptor.Help, out string[]? canonical))
                    return Error("RCLI1015", "invalid-choice", tokens.Length, resolved.Path,
                        ChoiceArguments("<" + (descriptor.Help.ValueName ?? descriptor.Name) + ">", descriptor.Help, descriptor.IsSensitive), CurrentOutputClassification());
                if (canonical is not null) canonicalArguments[argumentIndex] = new CommandValueBinding(binding.Id, canonical);
            }
        }
        var frozenOptions = new CommandValueBinding[optionBindings.Count];
        for (int optionIndex = 0; optionIndex < optionBindings.Count; optionIndex++)
        {
            MutableBinding binding = optionBindings[optionIndex];
            frozenOptions[optionIndex] = new CommandValueBinding(binding.Id, binding.Values);
        }

        return ParseOutcome.FromInvocation(
            new ParsedInvocation(
                resolved.Command,
                resolved.Path,
                frozenOptions,
                canonicalArguments,
                outputClassification) { EnvironmentOptionIds = environmentOptionIds });
    }

    // Trailing arguments are always last and receive only the tokens after --.
    private static IReadOnlyList<CommandArgumentDescriptor> PositionalArguments(IReadOnlyList<CommandArgumentDescriptor> arguments)
    {
        int count = arguments.Count;
        while (count > 0 && arguments[count - 1].IsTrailing) count--;
        if (count == arguments.Count) return arguments;
        var positional = new CommandArgumentDescriptor[count];
        for (int index = 0; index < count; index++) positional[index] = arguments[index];
        return positional;
    }

    private static CommandArgumentDescriptor? ArgumentAt(IReadOnlyList<CommandArgumentDescriptor> arguments, int position)
    {
        foreach (CommandArgumentDescriptor argument in arguments)
        {
            if (argument.IsTrailing) break;
            if (argument.Arity.Maximum is null || position < argument.Arity.Maximum) return argument;
            position -= argument.Arity.Maximum.Value;
        }
        return null;
    }

    private static string[] ChoiceArguments(string name, CommandHelp help, bool sensitive) =>
        CommandValueDiagnostics.Choices(help.Choices, sensitive) is { } choices ? [name, choices] : [name];

    // Choices match case-insensitively, so handlers receive the declared spelling rather than the user's casing.
    // An exact match wins when declared choices differ only by case. canonical is null when nothing changes.
    private static bool TryCanonicalizeChoices(IReadOnlyList<string> values, CommandHelp help, out string[]? canonical)
    {
        canonical = null;
        if (help.Choices.Count == 0) return true;
        for (int index = 0; index < values.Count; index++)
        {
            string? match = null;
            foreach (string choice in help.Choices)
            {
                if (string.Equals(values[index], choice, StringComparison.Ordinal)) { match = choice; break; }
                if (match is null && string.Equals(values[index], choice, StringComparison.OrdinalIgnoreCase)) match = choice;
            }
            if (match is null) return false;
            if (!string.Equals(match, values[index], StringComparison.Ordinal))
            {
                canonical ??= [.. values];
                canonical[index] = match;
            }
        }
        return true;
    }

    // Accepts the spellings people put in environment files for a flag.
    private static bool TryParseEnvironmentFlag(string value, out bool enabled)
    {
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value == "1")
        {
            enabled = true;
            return true;
        }

        enabled = false;
        return value.Equals("false", StringComparison.OrdinalIgnoreCase) || value.Equals("no", StringComparison.OrdinalIgnoreCase) || value == "0";
    }

    private static ParseOutcome? ConsumeOptionValues(
        CommandDescriptor command,
        CommandPath path,
        CommandOptionDescriptor option,
        string[] tokens,
        ref int index,
        bool recognizeOptions,
        TransportOutput transport,
        List<string> values,
        CommandOutputClassification outputClassification)
    {
        int maximum = option.Arity.Maximum ?? int.MaxValue;
        while (values.Count < maximum && index < tokens.Length)
        {
            string candidate = tokens[index];
            bool negativeNumber = option.Help.AcceptsNegativeNumbers && candidate.StartsWith('-')
                && double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            bool boundary = recognizeOptions && !negativeNumber && IsOptionBoundary(command, candidate, transport);
            if (boundary)
            {
                if (values.Count < option.Arity.Minimum)
                {
                    return Error(
                        MissingOptionValueCode,
                        "missing-option-value",
                        index,
                        path,
                        [option.Name],
                        outputClassification);
                }

                break;
            }

            values.Add(candidate);
            index++;
        }

        if (values.Count < option.Arity.Minimum)
        {
            return Error(
                MissingOptionValueCode,
                "missing-option-value",
                tokens.Length,
                path,
                [option.Name],
                outputClassification);
        }

        return null;
    }

    private static ParseOutcome? BindArguments(
        IReadOnlyList<CommandArgumentDescriptor> descriptors,
        CommandPath path,
        IReadOnlyList<IndexedToken> tokens,
        int endTokenIndex,
        CommandOutputClassification outputClassification,
        out IReadOnlyList<CommandValueBinding>? bindings)
    {
        // Too few tokens: the arguments bind left to right, so the first argument whose minimum the remaining
        // tokens cannot meet is the one missing, not an earlier argument that a token was reserved away from.
        int requiredSoFar = 0;
        foreach (CommandArgumentDescriptor descriptor in descriptors)
        {
            requiredSoFar = checked(requiredSoFar + descriptor.Arity.Minimum);
            if (requiredSoFar > tokens.Count)
            {
                bindings = null;
                return Error(
                    MissingArgumentCode,
                    "missing-argument",
                    endTokenIndex,
                    path,
                    [descriptor.Name],
                    outputClassification: outputClassification);
            }
        }

        // Enough tokens for every minimum: each argument takes as many as it accepts while leaving the minimums of
        // the arguments after it, so an optional argument before a required one is filled only by surplus tokens.
        var result = new List<CommandValueBinding>(descriptors.Count);
        int tokenIndex = 0;
        int requiredAfter = requiredSoFar;
        foreach (CommandArgumentDescriptor descriptor in descriptors)
        {
            requiredAfter -= descriptor.Arity.Minimum;
            int available = tokens.Count - tokenIndex;
            int maximum = descriptor.Arity.Maximum ?? int.MaxValue;
            int count = Math.Min(available - requiredAfter, maximum);
            if (count > 0)
            {
                var values = new string[count];
                for (int valueIndex = 0; valueIndex < count; valueIndex++)
                {
                    values[valueIndex] = tokens[tokenIndex + valueIndex].Value;
                }

                result.Add(new CommandValueBinding(descriptor.Id, values));
                tokenIndex += count;
            }
        }

        if (tokenIndex < tokens.Count)
        {
            bindings = null;
            return Error(
                UnexpectedArgumentCode,
                "unexpected-argument",
                tokens[tokenIndex].Index,
                path,
                outputClassification: outputClassification);
        }

        bindings = result;
        return null;
    }

    private static bool TryGetGlobalOption(CommandCatalog catalog, string token, out CommandOptionDescriptor? option, out string? value)
    {
        foreach (CommandDescriptor command in catalog.Commands)
        {
            if (TryResolveOption(command, token, out option, out value, out _) && option?.IsGlobal == true) return true;
        }
        option = null;
        value = null;
        return false;
    }

    private static int LeadingCommandIndex(CommandCatalog catalog, string[] tokens, TransportOutput transport)
    {
        int start = 0;
        while (start < tokens.Length)
        {
            if (TrySplitOutput(tokens[start], transport, out string? inline)) { start += inline is null ? 2 : 1; continue; }
            CommandOptionDescriptor? global = null;
            if (TryGetGlobalOption(catalog, tokens[start], out CommandOptionDescriptor? candidate, out string? value))
            {
                global = candidate!;
                start += value is not null || global.Arity.Maximum == 0 ? 1 : 2;
                continue;
            }
            break;
        }
        return start;
    }

    // The command or group path the leading tokens name explicitly, after any leading transport or global
    // options. Unlike command resolution, an implicit default command does not count as a match.
    internal static CommandPath? MatchExplicitPath(CommandCatalog catalog, IReadOnlyList<string> arguments)
    {
        string[] tokens = [.. arguments];
        int start = LeadingCommandIndex(catalog, tokens, TransportOutput.From(catalog));
        if (start >= tokens.Length || !catalog.TryGetCommand(tokens[start], out CommandDescriptor? command) || command is null) return null;
        var path = new List<string> { command.Name };
        for (int index = start + 1; index < tokens.Length && command.TryGetSubcommand(tokens[index], out CommandDescriptor? child) && child is not null; index++)
        {
            command = child;
            path.Add(command.Name);
        }

        return new CommandPath(path);
    }

    private static bool TryResolveCommand(
        CommandCatalog catalog,
        string[] tokens,
        out ResolvedCommand? resolved,
        TransportOutput transport)
    {
        resolved = null;
        int start = LeadingCommandIndex(catalog, tokens, transport);
        CommandDescriptor? command = null;
        if (start >= tokens.Length || !catalog.TryGetCommand(tokens[start], out command) || command is null)
        {
            command = catalog.DefaultCommand;
            if (command is null) return false;
            resolved = new ResolvedCommand(command, new CommandPath([command.Name]), 0);
            return true;
        }

        var path = new List<string> { command.Name };
        int consumed = start + 1;
        while (consumed < tokens.Length &&
               command.TryGetSubcommand(tokens[consumed], out CommandDescriptor? child) &&
               child is not null)
        {
            command = child;
            path.Add(command.Name);
            consumed++;
        }

        resolved = new ResolvedCommand(command, new CommandPath(path), consumed, start);
        return true;
    }

    private static bool TryResolveOption(
        CommandDescriptor command,
        string token,
        out CommandOptionDescriptor? option,
        out string? inlineValue,
        out OptionTokenKind tokenKind)
    {
        option = null;
        inlineValue = null;
        tokenKind = OptionTokenKind.NotOption;

        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            tokenKind = OptionTokenKind.Option;
            int equalsIndex = token.IndexOf('=', 2);
            string name = equalsIndex < 0 ? token : token[..equalsIndex];
            if (command.TryGetOption(name, out option) && option is not null)
            {
                if (equalsIndex >= 0)
                {
                    inlineValue = token[(equalsIndex + 1)..];
                }

                return true;
            }

            return true;
        }

        if (token.Length >= 2 && token[0] == '-')
        {
            tokenKind = token.Length == 2
                ? OptionTokenKind.Option
                : OptionTokenKind.UnsupportedShortBundle;
            if (token.Length == 2)
            {
                _ = command.TryGetOption(token, out option);
            }

            return true;
        }

        if (token.Length > 0 && token[0] == '/' &&
            command.TryGetOption(token, out option) &&
            option is not null)
        {
            tokenKind = OptionTokenKind.Option;
            return true;
        }

        return false;
    }

    private static bool IsOptionBoundary(CommandDescriptor command, string token, TransportOutput transport)
    {
        if (string.Equals(token, "--", StringComparison.Ordinal) ||
            IsHelp(token) ||
            string.Equals(token, "--version", StringComparison.Ordinal) ||
            TrySplitOutput(token, transport, out _))
        {
            return true;
        }

        return TryResolveOption(command, token, out _, out _, out _);
    }

    private static bool TrySplitOutput(string token, TransportOutput transport, out string? inlineValue) =>
        TrySplitOutput(token, transport, out inlineValue, out _);

    // Recognizes the selector ("--format value" or "--format=value"), its hidden aliases, which parse exactly like it,
    // and the declared JSON alias ("--json"), which reads as "--format=json". "--json=value" is recognized too, so that it is reported as a flag with a value; its
    // inline value is then that value. Either way an inline value means the occurrence spans one token.
    private static bool TrySplitOutput(string token, TransportOutput transport, out string? inlineValue, out string spelling)
    {
        if (transport.JsonAlias is { } alias)
        {
            if (string.Equals(token, alias, StringComparison.Ordinal))
            {
                inlineValue = "json";
                spelling = alias;
                return true;
            }

            if (token.Length > alias.Length && token[alias.Length] == '=' && token.StartsWith(alias, StringComparison.Ordinal))
            {
                inlineValue = token[(alias.Length + 1)..];
                spelling = alias;
                return true;
            }
        }

        if (TrySplitSelector(token, transport.Name, out inlineValue))
        {
            spelling = transport.Name;
            return true;
        }

        foreach (string hiddenAlias in transport.HiddenAliases)
        {
            if (TrySplitSelector(token, hiddenAlias, out inlineValue))
            {
                spelling = hiddenAlias;
                return true;
            }
        }

        spelling = transport.Name;
        inlineValue = null;
        return false;
    }

    private static bool TrySplitSelector(string token, string name, out string? inlineValue)
    {
        inlineValue = null;
        if (string.Equals(token, name, StringComparison.Ordinal)) return true;
        if (token.Length > name.Length && token[name.Length] == '=' && token.StartsWith(name, StringComparison.Ordinal))
        {
            inlineValue = token[(name.Length + 1)..];
            return true;
        }

        return false;
    }

    // A second selector occurrence: the same spelling twice is a duplicate, the selector and its alias conflict.
    private static ParseOutcome DuplicateOutputSelector(
        int tokenIndex,
        CommandPath path,
        string firstSpelling,
        string spelling,
        CommandOutputClassification outputClassification) =>
        string.Equals(firstSpelling, spelling, StringComparison.Ordinal)
            ? Error(DuplicateOptionCode, "duplicate-option", tokenIndex, path, [spelling], outputClassification)
            : Error(ConflictingOutputSelectorCode, "conflicting-output-selector", tokenIndex, path,
                [spelling, firstSpelling], outputClassification);

    // "--output human|json" selected the output before 0.8.0. Where that spelling is not recognized, an unknown
    // "--output" whose value is an output mode is an old command line, so the diagnostic suggests the selector that
    // replaced it. Any other value suggests nothing: "--output report.txt" names an option the application lacks.
    private static string? FormerSelectorSuggestion(string[] tokens, int index, TransportOutput transport)
    {
        const string FormerSelector = "--output";
        if (!TrySplitSelector(tokens[index], FormerSelector, out string? value)) return null;
        value ??= index + 1 < tokens.Length ? tokens[index + 1] : null;
        return value is not null && TryParseOutputMode(value, out _) ? transport.Name : null;
    }

    private static bool TryParseOutputMode(string value, out CommandOutputMode mode)
    {
        if (string.Equals(value, "human", StringComparison.OrdinalIgnoreCase))
        {
            mode = CommandOutputMode.Human;
            return true;
        }

        if (string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
        {
            mode = CommandOutputMode.Json;
            return true;
        }

        mode = default;
        return false;
    }

    private static CommandOutputClassification ClassifyOutput(
        CommandOutputMode? explicitMode,
        ParseSettings settings) =>
        CommandOutputClassifier.Classify(
            explicitMode,
            settings.OutputEnvironmentValue,
            settings.DefaultOutputMode);

    private static TransportOutputScan ScanTransportOutput(
        string[] tokens,
        TransportOutput transport,
        CommandDescriptor? command)
    {
        CommandOutputMode? explicitOutputMode = null;
        string? seenSpelling = null;
        bool aliasGiven = false;

        for (int index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index];
            if (string.Equals(token, "--", StringComparison.Ordinal))
            {
                break;
            }

            if (!TrySplitOutput(token, transport, out string? inlineValue, out string spelling))
            {
                continue;
            }

            aliasGiven |= transport.IsAlias(spelling);

            if (seenSpelling is not null)
            {
                bool conflict = !string.Equals(seenSpelling, spelling, StringComparison.Ordinal);
                return new TransportOutputScan(
                    explicitOutputMode, aliasGiven,
                    new TransportOutputIssue(
                        conflict ? ConflictingOutputSelectorCode : DuplicateOptionCode,
                        conflict ? "conflicting-output-selector" : "duplicate-option",
                        index,
                        conflict ? [spelling, seenSpelling] : [spelling]));
            }

            seenSpelling = spelling;
            if (transport.IsAlias(spelling) && !string.Equals(token, spelling, StringComparison.Ordinal))
            {
                return new TransportOutputScan(
                    explicitOutputMode, aliasGiven,
                    new TransportOutputIssue(UnexpectedOptionValueCode, "unexpected-option-value", index, [spelling]));
            }

            int valueIndex = index;
            string value;
            if (inlineValue is null)
            {
                int nextIndex = index + 1;
                if (nextIndex >= tokens.Length || IsTransportValueBoundary(
                    command,
                    tokens[nextIndex],
                    transport))
                {
                    return new TransportOutputScan(
                        explicitOutputMode, aliasGiven,
                        new TransportOutputIssue(
                            MissingOptionValueCode,
                            "missing-option-value",
                            nextIndex,
                            [transport.Name]));
                }

                valueIndex = nextIndex;
                value = tokens[nextIndex];
                index = nextIndex;
            }
            else
            {
                value = inlineValue;
            }

            if (!TryParseOutputMode(value, out CommandOutputMode mode))
            {
                return new TransportOutputScan(
                    explicitOutputMode, aliasGiven,
                    new TransportOutputIssue(
                        InvalidOutputModeCode,
                        "invalid-output-mode",
                        valueIndex,
                        [transport.Name]));
            }

            explicitOutputMode = mode;
        }

        return new TransportOutputScan(explicitOutputMode, aliasGiven, null);
    }

    private static bool IsTransportValueBoundary(
        CommandDescriptor? command,
        string token,
        TransportOutput transport) =>
        string.Equals(token, "--", StringComparison.Ordinal) ||
        IsHelp(token) ||
        string.Equals(token, "--version", StringComparison.Ordinal) ||
        token.StartsWith('-') ||
        TrySplitOutput(token, transport, out _) ||
        (command is not null && TryResolveOption(command, token, out _, out _, out _));

    private static bool TryFindRootSpecial(
        CommandCatalog catalog,
        string[] tokens,
        TransportOutput transport,
        out RootSpecial special,
        out int unexpectedHelpTokenIndex)
    {
        for (int index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index];
            if (string.Equals(token, "--", StringComparison.Ordinal))
            {
                break;
            }

            if (TrySplitOutput(token, transport, out string? inlineValue))
            {
                if (inlineValue is null)
                {
                    index++;
                }

                continue;
            }

            if (TryGetGlobalOption(catalog, token, out CommandOptionDescriptor? global, out string? globalValue))
            {
                if (global!.Arity.Maximum != 0 && globalValue is null) index++;
                continue;
            }

            if (string.Equals(token, "help", StringComparison.Ordinal))
            {
                special = RootSpecial.Help;
                unexpectedHelpTokenIndex = FindUnexpectedRootHelpToken(
                    tokens,
                    index + 1,
                    transport);
                return true;
            }

            if (IsHelp(token))
            {
                special = RootSpecial.Help;
                unexpectedHelpTokenIndex = -1;
                return true;
            }

            if (string.Equals(token, "--version", StringComparison.Ordinal))
            {
                special = RootSpecial.Version;
                unexpectedHelpTokenIndex = -1;
                return true;
            }

            break;
        }

        special = default;
        unexpectedHelpTokenIndex = -1;
        return false;
    }

    private static int FindUnexpectedRootHelpToken(
        string[] tokens,
        int index,
        TransportOutput transport)
    {
        while (index < tokens.Length)
        {
            if (!TrySplitOutput(tokens[index], transport, out string? inlineValue))
            {
                return index;
            }

            index += inlineValue is null ? 2 : 1;
        }

        return -1;
    }

    private static ParseOutcome CompleteSpecial(
        CommandOutputClassification classification,
        Func<CommandOutputClassification, ParseOutcome> create)
    {
        return classification.IsValid
            ? create(classification)
            : OutputError(classification, 0);
    }

    private static ParseOutcome OutputError(
        CommandOutputClassification classification,
        int tokenIndex,
        CommandPath? path = null) =>
        ParseOutcome.FromOutputError(
            Diagnostic(
                InvalidOutputModeCode,
                "invalid-output-mode",
                FormattedMessage("invalid-output-mode", [CommandOutputClassifier.EnvironmentVariableName])!,
                tokenIndex,
                path ?? CommandPath.Root,
                [CommandOutputClassifier.EnvironmentVariableName]),
            classification);

    private static ParseOutcome Error(
        string code,
        string kind,
        int tokenIndex,
        CommandPath? path = null,
        IReadOnlyList<string>? arguments = null,
        CommandOutputClassification? outputClassification = null,
        string? suggestion = null,
        string? environmentVariable = null)
    {
        string message = (FormattedMessage(kind, arguments) ?? MessageFor(kind)) +
            (suggestion is not null ? " Did you mean '" + suggestion + "'?" : "");
        string? messageKey = CommandValueDiagnostics.MessageKey(kind, arguments ?? []);
        // A suggestion is a trailing argument with its own key, so a translation can still offer it.
        if (suggestion is not null)
        {
            arguments = [.. arguments ?? [], suggestion];
            messageKey = CommandTextKeys.Diagnostic(kind + "-suggestion");
        }
        CommandDiagnostic diagnostic = Diagnostic(code, kind, message, tokenIndex, path ?? CommandPath.Root, arguments, messageKey);
        if (outputClassification is not CommandOutputClassification classification)
        {
            return ParseOutcome.FromError(diagnostic);
        }

        if (environmentVariable is not null && classification.IsValid)
        {
            string[] noteArguments = [arguments![0], environmentVariable];
            return ParseOutcome.FromErrors(
                [diagnostic, new CommandDiagnostic(CommandValueDiagnostics.EnvironmentSourceCode, "environment-value-source",
                    CommandValueDiagnostics.Format("environment-value-source", noteArguments), CommandDiagnosticPhase.Parse,
                    CommandDiagnosticSeverity.Information, tokenIndex, noteArguments, path ?? CommandPath.Root)],
                classification);
        }

        return classification.IsValid
            ? ParseOutcome.FromError(diagnostic, classification)
            : ParseOutcome.FromOutputError(diagnostic, classification);
    }

    /// <summary>
    /// The usage error for a response file that could not be used. Its output selection is the one the arguments as
    /// given make, so <c>--format json</c> on the command line still selects JSON; an invalid output selection is
    /// reported instead, as the parser reports it before any other error.
    /// </summary>
    internal static ParseOutcome ResponseFileError(CommandCatalog catalog, string[] args, ParseSettings settings, ResponseFileFailure failure)
    {
        ParseOutcome given = Instance.Parse(catalog, args, settings);
        TransportOutput transport = TransportOutput.From(catalog);
        ResolvedCommand? resolved = null;
        if (args.Length > 0) _ = TryResolveCommand(catalog, args, out resolved, transport);
        if (ScanTransportOutput(args, transport, resolved?.Command).Issue is not null || given.OutputClassification is { IsValid: false }) return given;
        // Output redacts a diagnostic that names a home or temporary directory, so such a reference is named by its file.
        string reference = failure.Reference;
        if (CommandFaultSanitizer.ContainsTechnicalContent(reference) && Path.GetFileName(reference) is { Length: > 0 } name) reference = name;
        return Error(failure.Code, failure.Kind, failure.TokenIndex, arguments: [SafeDiagnosticArgument(reference)],
            outputClassification: given.OutputClassification);
    }

    // Messages name their subject in the sentence: a declared option or argument as declared, a token the user
    // typed in quotes. A kind without its argument falls back to MessageFor.
    private static string? FormattedMessage(string kind, IReadOnlyList<string>? arguments) => (kind, arguments) switch
    {
        ("unknown-option", { Count: > 0 }) => "The option '" + arguments[0] + "' is not recognized.",
        ("unknown-command", { Count: > 0 }) => "The command '" + arguments[0] + "' is not recognized.",
        ("missing-option-value", { Count: > 0 }) => "The " + arguments[0] + " option requires a value.",
        ("unexpected-option-value", { Count: > 0 }) => "The " + arguments[0] + " option does not take a value.",
        ("missing-argument", { Count: > 0 }) => "The " + arguments[0] + " argument is missing.",
        ("duplicate-option", { Count: > 0 }) => "The " + arguments[0] + " option was given more than once.",
        ("missing-required-option", { Count: > 0 }) => "The " + arguments[0] + " option is required.",
        ("invalid-choice", { Count: > 0 }) => CommandValueDiagnostics.Format(kind, arguments),
        ("invalid-output-mode", { Count: > 0 }) => arguments[0] + " must be human or json.",
        ("conflicting-output-selector", { Count: > 1 }) => arguments[0] + " and " + arguments[1] + " both select the output format. Use one of them.",
        ("invalid-environment-value", { Count: > 1 }) => arguments[1] + " must be true/false, yes/no or 1/0 for " + arguments[0] + ".",
        (CommandResponseFiles.UnreadableKind, { Count: > 0 }) => "The response file '" + arguments[0] + "' could not be read.",
        (CommandResponseFiles.InvalidUtf8Kind, { Count: > 0 }) => "The response file '" + arguments[0] + "' is not valid UTF-8.",
        (CommandResponseFiles.UnterminatedQuoteKind, { Count: > 0 }) => "The response file '" + arguments[0] + "' has an unterminated quote.",
        (CommandResponseFiles.TooDeepKind, { Count: > 0 }) => "The response file '" + arguments[0] + "' is nested more than 16 levels deep.",
        (CommandResponseFiles.CycleKind, { Count: > 0 }) => "The response file '" + arguments[0] + "' includes itself.",
        _ => null,
    };

    private static CommandDiagnostic Diagnostic(
        string code,
        string kind,
        string message,
        int tokenIndex,
        CommandPath path,
        IReadOnlyList<string>? arguments = null,
        string? messageKey = null) =>
        new(
            code,
            kind,
            message,
            CommandDiagnosticPhase.Parse,
            CommandDiagnosticSeverity.Error,
            tokenIndex,
            arguments,
            path: path,
            messageKey: messageKey);

    private static string MessageFor(string kind) => kind switch
    {
        "invalid-environment-value" => "The environment fallback is not valid for this option.",
        "invalid-choice" => "The value must be one of the choices shown in --help.",
        "unknown-option" => "The option is not recognized.",
        "unknown-command" => "No command was given.",
        "missing-option-value" => "An option requires a value.",
        "unexpected-option-value" => "A flag does not take a value.",
        "missing-argument" => "A required argument is missing.",
        "unexpected-argument" => "Too many arguments were given.",
        "duplicate-option" => "An option was given more than once.",
        "missing-required-option" => "A required option is missing.",
        "unexpected-root-help-argument" => "The application's help takes no other arguments.",
        "unsupported-short-bundle" => "Short options cannot be combined; give each one separately.",
        _ => "The command line is invalid.",
    };

    private static bool IsHelp(string token) =>
        string.Equals(token, "--help", StringComparison.Ordinal) ||
        string.Equals(token, "-h", StringComparison.Ordinal);

    private static string SafeUnknownCommand(string token) => SafeDiagnosticArgument(token);

    private static string SafeUnknownOption(string token)
    {
        int valueSeparator = token.IndexOf('=');
        string value = valueSeparator >= 0 ? token[..valueSeparator] : token;
        return SafeDiagnosticArgument(value);
    }

    private static string SafeDiagnosticArgument(string value)
    {
        if (value.Length == 0 || value.Length > MaximumDiagnosticArgumentLength)
        {
            return "<invalid>";
        }

        // Control characters, and the formatting characters that reorder or hide text on a terminal.
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index])) return "<invalid>";
            int codePoint = char.IsSurrogatePair(value, index) ? char.ConvertToUtf32(value[index], value[index + 1]) : value[index];
            if (CommandFaultSanitizer.IsInvisibleFormatCharacter(codePoint)) return "<invalid>";
        }

        return value;
    }

    private static void ValidateTokens(string[] tokens)
    {
        for (int index = 0; index < tokens.Length; index++)
        {
            if (tokens[index] is null)
            {
                throw new ArgumentException("Argument tokens cannot contain null.", nameof(tokens));
            }
        }
    }

    private sealed record ResolvedCommand(
        CommandDescriptor Command,
        CommandPath Path,
        int Consumed, int Start = 0);

    private sealed record IndexedToken(string Value, int Index);

    // The selector, its hidden aliases (which parse exactly like it) and the JSON output alias flag.
    private readonly record struct TransportOutput(string Name, IReadOnlyList<string> HiddenAliases, string? JsonAlias)
    {
        internal static TransportOutput From(CommandCatalog catalog) =>
            new(catalog.OutputSelector, catalog.OutputSelectorAliases, catalog.JsonOutputAlias);

        internal bool IsAlias(string spelling) => string.Equals(spelling, JsonAlias, StringComparison.Ordinal);
    }

    private sealed record TransportOutputScan(
        CommandOutputMode? ExplicitOutputMode,
        bool AliasGiven,
        TransportOutputIssue? Issue);

    private sealed record TransportOutputIssue(
        string Code,
        string Kind,
        int TokenIndex,
        IReadOnlyList<string>? Arguments);

    private sealed class MutableBinding
    {
        internal MutableBinding(string id, List<string> values)
        {
            Id = id;
            Values = values;
        }

        internal string Id { get; }

        internal List<string> Values { get; }
    }

    private enum OptionTokenKind
    {
        NotOption = 0,
        Option = 1,
        UnsupportedShortBundle = 2,
    }

    private enum RootSpecial
    {
        Help = 0,
        Version = 1,
    }
}
