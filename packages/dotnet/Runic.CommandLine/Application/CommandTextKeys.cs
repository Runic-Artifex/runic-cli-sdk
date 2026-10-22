using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Runic.CommandLine;

/// <summary>Describes one framework text key and the ordered arguments an <see cref="ICommandTextResolver"/> receives for it.</summary>
public sealed class CommandTextKey
{
    internal CommandTextKey(string key, params string[] arguments)
    {
        Key = key;
        Arguments = new ReadOnlyCollection<string>(arguments);
    }

    /// <summary>Gets the stable key, such as <c>help.usage</c> or <c>diagnostics.out-of-range</c>.</summary>
    public string Key { get; }

    /// <summary>
    /// Gets the names of the ordered arguments, in the order <see cref="ICommandTextResolver.Resolve"/> receives them.
    /// The names describe each position; resolvers may use them as named message inputs.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <inheritdoc />
    public override string ToString() => Arguments.Count == 0 ? Key : Key + "(" + string.Join(", ", Arguments) + ")";
}

/// <summary>Lists every framework text key the command line resolves through <see cref="ICommandTextResolver"/>.</summary>
/// <remarks>
/// Keys are stable identities: they appear as diagnostic <c>messageKey</c> values in JSON output and never change with the
/// culture. A resolver returns null for a key it does not translate, and the built-in English text is shown instead.
/// Applications add their own keys through <c>DescriptionKey</c> metadata and custom diagnostics. Open: a later minor
/// release may add keys; a resolver that does not know one returns null.
/// </remarks>
public static class CommandTextKeys
{
    /// <summary>The usage line label (<c>Usage</c>).</summary>
    public const string HelpUsage = "help.usage";
    /// <summary>The command placeholder in the root usage line (<c>command</c>).</summary>
    public const string HelpCommandPlaceholder = "help.command-placeholder";
    /// <summary>The options placeholder in the usage line (<c>options</c>).</summary>
    public const string HelpOptionsPlaceholder = "help.options-placeholder";
    /// <summary>The marker after the default command (<c>default</c>).</summary>
    public const string HelpDefaultCommand = "help.default-command";
    /// <summary>The description of the built-in <c>completion</c> command and of a declared completion option.</summary>
    public const string HelpCompletion = "help.completion";
    /// <summary>The commands section heading (<c>Commands</c>).</summary>
    public const string HelpCommands = "help.commands";
    /// <summary>The arguments section heading (<c>Arguments</c>).</summary>
    public const string HelpArguments = "help.arguments";
    /// <summary>The options section heading (<c>Options</c>).</summary>
    public const string HelpOptions = "help.options";
    /// <summary>The description of <c>--help</c> (<c>Show help</c>).</summary>
    public const string HelpShowHelp = "help.show-help";
    /// <summary>The description of <c>--version</c> (<c>Show version</c>).</summary>
    public const string HelpShowVersion = "help.show-version";
    /// <summary>The description of the output-mode option (<c>Select output format</c>).</summary>
    public const string HelpOutputFormat = "help.output-format";
    /// <summary>The description of the JSON output alias (<c>Same as {option} json</c>); argument: <c>option</c>.</summary>
    public const string HelpJsonOutputAlias = "help.json-output-alias";
    /// <summary>The description of a JSON output alias of payload shape (<c>Write the result as JSON</c>).</summary>
    public const string HelpJsonPayloadOutputAlias = "help.json-payload-output-alias";
    /// <summary>The description of <c>@&lt;file&gt;</c> when the catalog reads response files (<c>Read more arguments from a file</c>).</summary>
    public const string HelpResponseFiles = "help.response-files";
    /// <summary>The examples section heading (<c>Examples</c>).</summary>
    public const string HelpExamples = "help.examples";
    /// <summary>The note for a required option or argument (<c>required</c>).</summary>
    public const string HelpRequired = "help.required";
    /// <summary>The label of a default value note (<c>default</c>).</summary>
    public const string HelpDefault = "help.default";
    /// <summary>The label of a choices note (<c>choices</c>).</summary>
    public const string HelpChoices = "help.choices";
    /// <summary>The label of an environment-variable note (<c>env</c>).</summary>
    public const string HelpEnvironment = "help.environment";
    /// <summary>The prefix of a path note whose path must exist (<c>existing</c>).</summary>
    public const string HelpExisting = "help.existing";
    /// <summary>The path note of a file path (<c>file</c>).</summary>
    public const string HelpPathKindFile = "help.path-kind.file";
    /// <summary>The path note of a directory path (<c>directory</c>).</summary>
    public const string HelpPathKindDirectory = "help.path-kind.directory";
    /// <summary>The label of a minimum note (<c>min</c>).</summary>
    public const string HelpMinimum = "help.minimum";
    /// <summary>The label of a maximum note (<c>max</c>).</summary>
    public const string HelpMaximum = "help.maximum";
    /// <summary>The label of a requires note (<c>requires</c>).</summary>
    public const string HelpRequires = "help.requires";
    /// <summary>The label of a conflicts note (<c>conflicts</c>).</summary>
    public const string HelpConflicts = "help.conflicts";

    /// <summary>The error for an unsupported completion shell.</summary>
    public const string CompletionInvalidShell = "completion.invalid-shell";

    /// <summary>The key hint of a redrawn select prompt (<c>Arrow keys to move, Enter to confirm</c>).</summary>
    public const string PromptSelectHint = "prompts.select-hint";
    /// <summary>The key hint of a redrawn multi-select prompt.</summary>
    public const string PromptMultiSelectHint = "prompts.multi-select-hint";
    /// <summary>The affirmative answer of a confirm prompt (<c>Yes</c>); its first letter is also accepted.</summary>
    public const string PromptYes = "prompts.yes";
    /// <summary>The negative answer of a confirm prompt (<c>No</c>); its first letter is also accepted.</summary>
    public const string PromptNo = "prompts.no";
    /// <summary>The input request of a plain select prompt; argument: <c>count</c>.</summary>
    public const string PromptChooseOne = "prompts.choose-one";
    /// <summary>The input request of a plain multi-select prompt; argument: <c>count</c>.</summary>
    public const string PromptChooseMany = "prompts.choose-many";
    /// <summary>The error after an unrecognized confirm answer; arguments: <c>yes</c>, <c>no</c>.</summary>
    public const string PromptAnswerYesNo = "prompts.answer-yes-no";
    /// <summary>The error after an empty answer or an empty required multi-select.</summary>
    public const string PromptRequired = "prompts.required";
    /// <summary>The summary of a cancelled prompt or task log (<c>Cancelled</c>).</summary>
    public const string PromptCancelled = "prompts.cancelled";
    /// <summary>The default summary of a successful task log (<c>Done</c>).</summary>
    public const string PromptDone = "prompts.done";

    /// <summary>The <c>RCLI4000</c> fault message of a cancelled command.</summary>
    public const string FaultCancelled = "faults.RCLI4000";
    /// <summary>The <c>RCLI4001</c> fault message of a prompt that needed an answer the command could not ask for.</summary>
    public const string FaultPromptUnavailable = "faults.RCLI4001";
    /// <summary>The <c>RCLI5000</c> fault message of an unexpected failure.</summary>
    public const string FaultHostFailure = "faults.RCLI5000";
    /// <summary>The human-output hint after an <c>RCLI5000</c> fault that explains how to see the exception.</summary>
    /// <remarks>It is not under <c>faults.</c> so that catalogs which nest keys can hold it beside <see cref="FaultHostFailure"/>.</remarks>
    public const string FaultHostFailureHint = "hints.RCLI5000";

    /// <summary>An unrecognized option; argument: <c>option</c>.</summary>
    public const string DiagnosticUnknownOption = "diagnostics.unknown-option";
    /// <summary>An unrecognized option with a close match; arguments: <c>option</c>, <c>suggestion</c>.</summary>
    public const string DiagnosticUnknownOptionSuggestion = "diagnostics.unknown-option-suggestion";
    /// <summary>An unrecognized command; argument: <c>command</c>, absent when no command was typed.</summary>
    public const string DiagnosticUnknownCommand = "diagnostics.unknown-command";
    /// <summary>An unrecognized command with a close match; arguments: <c>command</c>, <c>suggestion</c>.</summary>
    public const string DiagnosticUnknownCommandSuggestion = "diagnostics.unknown-command-suggestion";
    /// <summary>An option without its value; argument: <c>option</c>.</summary>
    public const string DiagnosticMissingOptionValue = "diagnostics.missing-option-value";
    /// <summary>A flag given a value; argument: <c>option</c>.</summary>
    public const string DiagnosticUnexpectedOptionValue = "diagnostics.unexpected-option-value";
    /// <summary>A missing required argument; argument: <c>argument</c>.</summary>
    public const string DiagnosticMissingArgument = "diagnostics.missing-argument";
    /// <summary>A surplus positional argument; no arguments.</summary>
    public const string DiagnosticUnexpectedArgument = "diagnostics.unexpected-argument";
    /// <summary>An option repeated where repetition is an error; argument: <c>option</c>.</summary>
    public const string DiagnosticDuplicateOption = "diagnostics.duplicate-option";
    /// <summary>Bundled short options such as <c>-abc</c>; no arguments.</summary>
    public const string DiagnosticUnsupportedShortBundle = "diagnostics.unsupported-short-bundle";
    /// <summary>An output mode other than human or json; argument: <c>option</c> (the option spelling or environment variable).</summary>
    public const string DiagnosticInvalidOutputMode = "diagnostics.invalid-output-mode";
    /// <summary>
    /// The output selector given together with its JSON alias; arguments: <c>option</c> (the later spelling) and
    /// <c>conflict</c> (the earlier one).
    /// </summary>
    public const string DiagnosticConflictingOutputSelector = "diagnostics.conflicting-output-selector";
    /// <summary>A command that defines the framework output option itself; no arguments.</summary>
    public const string DiagnosticTransportOutputOptionCollision = "diagnostics.transport-output-option-collision";
    /// <summary>A required option that was not supplied; arguments: <c>option</c>, <c>command</c>.</summary>
    public const string DiagnosticMissingRequiredOption = "diagnostics.missing-required-option";
    /// <summary>Root help followed by other arguments; no arguments.</summary>
    public const string DiagnosticUnexpectedRootHelpArgument = "diagnostics.unexpected-root-help-argument";
    /// <summary>A response file that cannot be read; argument: <c>file</c>, as written after <c>@</c>, or its file name when it names a home or temporary directory.</summary>
    public const string DiagnosticResponseFileUnreadable = "diagnostics.response-file-unreadable";
    /// <summary>A response file that is not valid UTF-8; argument: <c>file</c>.</summary>
    public const string DiagnosticResponseFileInvalidUtf8 = "diagnostics.response-file-invalid-utf8";
    /// <summary>A response file with an unterminated quote; argument: <c>file</c>.</summary>
    public const string DiagnosticResponseFileUnterminatedQuote = "diagnostics.response-file-unterminated-quote";
    /// <summary>A response file nested more than 16 levels deep; argument: <c>file</c>.</summary>
    public const string DiagnosticResponseFileTooDeep = "diagnostics.response-file-too-deep";
    /// <summary>A response file that includes itself; argument: <c>file</c>.</summary>
    public const string DiagnosticResponseFileCycle = "diagnostics.response-file-cycle";
    /// <summary>A flag's environment fallback that is not a Boolean; arguments: <c>option</c>, <c>variable</c>.</summary>
    public const string DiagnosticInvalidEnvironmentValue = "diagnostics.invalid-environment-value";
    /// <summary>A value outside the declared choices; arguments: <c>parameter</c>, <c>choices</c>.</summary>
    public const string DiagnosticInvalidChoice = "diagnostics.invalid-choice";
    /// <summary>A value outside sensitive or very long choices that are not listed; argument: <c>parameter</c>.</summary>
    public const string DiagnosticInvalidChoiceHidden = "diagnostics.invalid-choice-hidden";
    /// <summary>A value that is not a whole number; argument: <c>parameter</c>.</summary>
    public const string DiagnosticInvalidInteger = "diagnostics.invalid-integer";
    /// <summary>A value that is not a number; argument: <c>parameter</c>.</summary>
    public const string DiagnosticInvalidNumber = "diagnostics.invalid-number";
    /// <summary>A value that is not a GUID; argument: <c>parameter</c>.</summary>
    public const string DiagnosticInvalidGuid = "diagnostics.invalid-guid";
    /// <summary>A value that is not true or false; argument: <c>parameter</c>.</summary>
    public const string DiagnosticInvalidBoolean = "diagnostics.invalid-boolean";
    /// <summary>A value a converter rejected; argument: <c>parameter</c>.</summary>
    public const string DiagnosticInvalidValue = "diagnostics.invalid-value";
    /// <summary>A value a validator rejected; argument: <c>parameter</c>.</summary>
    public const string DiagnosticValidationFailed = "diagnostics.validation-failed";
    /// <summary>A required value that is missing after binding; argument: <c>parameter</c>.</summary>
    public const string DiagnosticMissingValue = "diagnostics.missing-value";
    /// <summary>A number outside both bounds; arguments: <c>parameter</c>, <c>minimum</c>, <c>maximum</c>.</summary>
    public const string DiagnosticOutOfRange = "diagnostics.out-of-range";
    /// <summary>A number below its minimum; arguments: <c>parameter</c>, <c>minimum</c>.</summary>
    public const string DiagnosticBelowMinimum = "diagnostics.below-minimum";
    /// <summary>A number above its maximum; arguments: <c>parameter</c>, <c>maximum</c>.</summary>
    public const string DiagnosticAboveMaximum = "diagnostics.above-maximum";
    /// <summary>An option used without an option it requires; arguments: <c>option</c>, <c>required</c>.</summary>
    public const string DiagnosticOptionRequires = "diagnostics.option-requires";
    /// <summary>Two options that cannot be combined; arguments: <c>option</c>, <c>conflict</c>.</summary>
    public const string DiagnosticOptionConflict = "diagnostics.option-conflict";
    /// <summary>A value that is not a valid file path; argument: <c>parameter</c>.</summary>
    public const string DiagnosticInvalidFilePath = "diagnostics.invalid-file-path";
    /// <summary>A value that is not a valid directory path; argument: <c>parameter</c>.</summary>
    public const string DiagnosticInvalidDirectoryPath = "diagnostics.invalid-directory-path";
    /// <summary>A file that must exist but does not; argument: <c>parameter</c>.</summary>
    public const string DiagnosticFileNotFound = "diagnostics.file-not-found";
    /// <summary>A directory that must exist but does not; argument: <c>parameter</c>.</summary>
    public const string DiagnosticDirectoryNotFound = "diagnostics.directory-not-found";
    /// <summary>The note naming the environment variable an invalid value came from; arguments: <c>parameter</c>, <c>variable</c>.</summary>
    public const string DiagnosticEnvironmentValueSource = "diagnostics.environment-value-source";
    /// <summary>A prompt without a default outside an interactive terminal; argument: <c>prompt</c>.</summary>
    public const string DiagnosticPromptRequiresTerminal = "diagnostics.prompt-requires-terminal";
    /// <summary>A prompt without a default with JSON output; argument: <c>prompt</c>.</summary>
    public const string DiagnosticPromptJsonOutput = "diagnostics.prompt-json-output";
    /// <summary>A prompt without a default while prompts are disabled; argument: <c>prompt</c>.</summary>
    public const string DiagnosticPromptDisabled = "diagnostics.prompt-disabled";

    private const string DiagnosticPrefix = "diagnostics.";
    private const string FaultPrefix = "faults.";

    /// <summary>Gets every framework key with its ordered argument names, in documentation order.</summary>
    public static IReadOnlyList<CommandTextKey> All { get; } = new ReadOnlyCollection<CommandTextKey>(
    [
        new(HelpUsage), new(HelpCommandPlaceholder), new(HelpOptionsPlaceholder), new(HelpDefaultCommand), new(HelpCompletion),
        new(HelpCommands), new(HelpArguments), new(HelpOptions), new(HelpShowHelp), new(HelpShowVersion), new(HelpOutputFormat), new(HelpJsonOutputAlias, "option"), new(HelpJsonPayloadOutputAlias),
        new(HelpResponseFiles),
        new(HelpExamples), new(HelpRequired), new(HelpDefault), new(HelpChoices), new(HelpEnvironment), new(HelpExisting),
        new(HelpPathKindFile), new(HelpPathKindDirectory), new(HelpMinimum), new(HelpMaximum), new(HelpRequires), new(HelpConflicts),
        new(CompletionInvalidShell),
        new(PromptSelectHint), new(PromptMultiSelectHint), new(PromptYes), new(PromptNo), new(PromptChooseOne, "count"),
        new(PromptChooseMany, "count"), new(PromptAnswerYesNo, "yes", "no"), new(PromptRequired), new(PromptCancelled), new(PromptDone),
        new(FaultCancelled), new(FaultPromptUnavailable), new(FaultHostFailure), new(FaultHostFailureHint),
        new(DiagnosticUnknownOption, "option"),
        new(DiagnosticUnknownOptionSuggestion, "option", "suggestion"),
        new(DiagnosticUnknownCommand, "command"),
        new(DiagnosticUnknownCommandSuggestion, "command", "suggestion"),
        new(DiagnosticMissingOptionValue, "option"),
        new(DiagnosticUnexpectedOptionValue, "option"),
        new(DiagnosticMissingArgument, "argument"),
        new(DiagnosticUnexpectedArgument),
        new(DiagnosticDuplicateOption, "option"),
        new(DiagnosticUnsupportedShortBundle),
        new(DiagnosticInvalidOutputMode, "option"),
        new(DiagnosticConflictingOutputSelector, "option", "conflict"),
        new(DiagnosticTransportOutputOptionCollision),
        new(DiagnosticMissingRequiredOption, "option", "command"),
        new(DiagnosticUnexpectedRootHelpArgument),
        new(DiagnosticResponseFileUnreadable, "file"),
        new(DiagnosticResponseFileInvalidUtf8, "file"),
        new(DiagnosticResponseFileUnterminatedQuote, "file"),
        new(DiagnosticResponseFileTooDeep, "file"),
        new(DiagnosticResponseFileCycle, "file"),
        new(DiagnosticInvalidEnvironmentValue, "option", "variable"),
        new(DiagnosticInvalidChoice, "parameter", "choices"),
        new(DiagnosticInvalidChoiceHidden, "parameter"),
        new(DiagnosticInvalidInteger, "parameter"),
        new(DiagnosticInvalidNumber, "parameter"),
        new(DiagnosticInvalidGuid, "parameter"),
        new(DiagnosticInvalidBoolean, "parameter"),
        new(DiagnosticInvalidValue, "parameter"),
        new(DiagnosticValidationFailed, "parameter"),
        new(DiagnosticMissingValue, "parameter"),
        new(DiagnosticOutOfRange, "parameter", "minimum", "maximum"),
        new(DiagnosticBelowMinimum, "parameter", "minimum"),
        new(DiagnosticAboveMaximum, "parameter", "maximum"),
        new(DiagnosticOptionRequires, "option", "required"),
        new(DiagnosticOptionConflict, "option", "conflict"),
        new(DiagnosticInvalidFilePath, "parameter"),
        new(DiagnosticInvalidDirectoryPath, "parameter"),
        new(DiagnosticFileNotFound, "parameter"),
        new(DiagnosticDirectoryNotFound, "parameter"),
        new(DiagnosticEnvironmentValueSource, "parameter", "variable"),
        new(DiagnosticPromptRequiresTerminal, "prompt"),
        new(DiagnosticPromptJsonOutput, "prompt"),
        new(DiagnosticPromptDisabled, "prompt"),
    ]);

    /// <summary>Returns the key <c>diagnostics.{kind}</c> a diagnostic of <paramref name="kind"/> uses unless it names its own key.</summary>
    public static string Diagnostic(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return DiagnosticPrefix + kind;
    }

    /// <summary>Returns the key <c>faults.{code}</c> used for a fault without a translated diagnostic, with no arguments.</summary>
    public static string Fault(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return FaultPrefix + code;
    }

    /// <summary>Finds the description of a framework key, or returns null for an application key.</summary>
    public static CommandTextKey? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (CommandTextKey candidate in All)
            if (string.Equals(candidate.Key, key, StringComparison.Ordinal)) return candidate;
        return null;
    }
}
