using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>One choice of a select or multi-select prompt.</summary>
/// <typeparam name="T">The value the prompt returns for this choice.</typeparam>
public sealed class CommandPromptChoice<T>
{
    /// <summary>Initializes a choice.</summary>
    /// <param name="value">The value returned when the choice is selected.</param>
    /// <param name="label">The nonblank text shown for the choice.</param>
    /// <param name="hint">Optional text shown after the label.</param>
    public CommandPromptChoice(T value, string label, string? hint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        Value = value;
        Label = label;
        Hint = string.IsNullOrWhiteSpace(hint) ? null : hint;
    }

    /// <summary>Gets the value returned when the choice is selected.</summary>
    public T Value { get; }

    /// <summary>Gets the text shown for the choice.</summary>
    public string Label { get; }

    /// <summary>Gets the optional text shown after the label.</summary>
    public string? Hint { get; }
}

/// <summary>
/// Asks the user questions during a command, and shows a spinner or a task log, while keeping the command scriptable.
/// </summary>
/// <remarks>
/// <para>
/// Create one from the handler's <see cref="CommandExecutionContext"/> and pass the invocation's cancellation token, so
/// that Ctrl+C cancels a waiting prompt with the <see cref="CommandExitCategory.Cancelled"/> exit code (4, <c>RCLI4000</c>).
/// Escape, and the end of input on a line prompt, cancel the same way.
/// </para>
/// <para>
/// A prompt asks only when <see cref="IsInteractive"/> is true. Otherwise it returns its default without writing
/// anything, and a prompt without a default fails the command with the usage exit code (2) and <c>RCLI4001</c>.
/// Prompts never ask with JSON output, when the console is not interactive (for example when standard input or output
/// is redirected), when <c>RUNIC_COMMANDLINE_NO_INPUT</c> is <c>1</c>, <c>true</c> or <c>yes</c>, or when the handler
/// passes <c>interactive: false</c>, for example for its own <c>--yes</c> option.
/// </para>
/// <para>
/// On a terminal whose console implements <see cref="ICommandTerminal"/> with <see cref="ICommandTerminal.SupportsAnsi"/>,
/// prompts redraw in place on standard error and are answered with the arrow keys. Otherwise, including with
/// <c>NO_COLOR</c> and <c>TERM=dumb</c>, each prompt writes its question and numbered choices as plain lines to standard
/// error and reads one line of input. Framework text such as hints, Yes and No resolves through the application's
/// <see cref="ICommandTextResolver"/>; see <see cref="CommandTextKeys"/>.
/// </para>
/// </remarks>
public sealed class CommandPrompts
{
    /// <summary>The environment variable that disables prompts when it is <c>1</c>, <c>true</c> or <c>yes</c>.</summary>
    internal const string NoInputEnvironmentVariable = "RUNIC_COMMANDLINE_NO_INPUT";

    private readonly PromptEngine _engine;

    /// <summary>Initializes prompts for one command invocation.</summary>
    /// <param name="context">The handler's execution context.</param>
    /// <param name="interactive">False to never ask, for example when the user passed the application's <c>--yes</c> option.</param>
    public CommandPrompts(CommandExecutionContext context, bool interactive = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        PromptUnavailableReason? reason =
            context.OutputMode == CommandOutputMode.Json ? PromptUnavailableReason.JsonOutput
            : !interactive || context.PromptsDisabled ? PromptUnavailableReason.Disabled
            : !context.Console.IsInteractive ? PromptUnavailableReason.NotInteractive
            : null;
        _engine = new PromptEngine(context.Console, new CommandTextContext(context.Culture, context.TextResolver), context.Path, reason);
    }

    /// <summary>Gets whether prompts ask the user; when false they return their defaults or fail with <c>RCLI4001</c>.</summary>
    public bool IsInteractive => _engine.Reason is null;

    internal static bool IsDisabledBy(Func<string, string?>? getEnvironmentVariable) =>
        getEnvironmentVariable?.Invoke(NoInputEnvironmentVariable)?.Trim() is { } value &&
        (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>Asks a yes-or-no question.</summary>
    /// <param name="message">The question.</param>
    /// <param name="defaultValue">The answer for Enter alone and for noninteractive runs, or null to require an answer.</param>
    /// <param name="cancellationToken">The invocation's cancellation token.</param>
    public ValueTask<bool> ConfirmAsync(string message, bool? defaultValue = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (_engine.Reason is not null) return ValueTask.FromResult(defaultValue ?? throw _engine.Unavailable(message));
        return _engine.ConfirmAsync(message, defaultValue, cancellationToken);
    }

    /// <summary>Asks for a line of text.</summary>
    /// <param name="message">The question.</param>
    /// <param name="defaultValue">The answer for Enter alone and for noninteractive runs, or null to require an answer.</param>
    /// <param name="validate">
    /// Returns an error message for an unacceptable answer, or null to accept it; the prompt asks again after an error.
    /// Without a validator an empty answer is rejected. Noninteractive runs return the default without validating it.
    /// </param>
    /// <param name="cancellationToken">The invocation's cancellation token.</param>
    public ValueTask<string> TextAsync(string message, string? defaultValue = null, Func<string, string?>? validate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (_engine.Reason is not null) return ValueTask.FromResult(defaultValue ?? throw _engine.Unavailable(message));
        return _engine.TextAsync(message, defaultValue, validate, cancellationToken);
    }

    /// <summary>Asks the user to choose one of several choices.</summary>
    /// <param name="message">The question.</param>
    /// <param name="choices">At least one choice.</param>
    /// <param name="defaultIndex">The index of the initially selected choice and the answer for noninteractive runs, or null to require an answer.</param>
    /// <param name="cancellationToken">The invocation's cancellation token.</param>
    /// <returns>The value of the selected choice.</returns>
    public async ValueTask<T> SelectAsync<T>(string message, IReadOnlyList<CommandPromptChoice<T>> choices, int? defaultIndex = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        string[] labels = Labels(choices, out string?[] hints);
        if (defaultIndex is < 0 || defaultIndex >= choices.Count) throw new ArgumentOutOfRangeException(nameof(defaultIndex));
        if (_engine.Reason is not null) return choices[defaultIndex ?? throw _engine.Unavailable(message)].Value;
        return choices[await _engine.SelectAsync(message, labels, hints, defaultIndex, cancellationToken).ConfigureAwait(false)].Value;
    }

    /// <summary>Asks the user to choose any number of several choices.</summary>
    /// <param name="message">The question.</param>
    /// <param name="choices">At least one choice.</param>
    /// <param name="defaultIndexes">The indexes of the initially selected choices, which are also the answer for noninteractive runs.</param>
    /// <param name="required">True to require at least one selected choice; a noninteractive run then needs <paramref name="defaultIndexes"/>.</param>
    /// <param name="cancellationToken">The invocation's cancellation token.</param>
    /// <returns>The values of the selected choices, in choice order.</returns>
    public async ValueTask<IReadOnlyList<T>> MultiSelectAsync<T>(string message, IReadOnlyList<CommandPromptChoice<T>> choices,
        IReadOnlyList<int>? defaultIndexes = null, bool required = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        string[] labels = Labels(choices, out string?[] hints);
        bool[] selected = new bool[choices.Count];
        foreach (int index in defaultIndexes ?? Array.Empty<int>())
        {
            if (index < 0 || index >= choices.Count) throw new ArgumentOutOfRangeException(nameof(defaultIndexes));
            selected[index] = true;
        }

        if (_engine.Reason is not null)
        {
            if (required && Array.IndexOf(selected, true) < 0) throw _engine.Unavailable(message);
        }
        else
        {
            selected = await _engine.MultiSelectAsync(message, labels, hints, selected, required, cancellationToken).ConfigureAwait(false);
        }

        var values = new List<T>();
        for (int index = 0; index < selected.Length; index++)
            if (selected[index]) values.Add(choices[index].Value);
        return new ReadOnlyCollection<T>(values);
    }

    /// <summary>Starts a spinner with a message until the returned log succeeds or fails.</summary>
    /// <param name="message">What the command is doing.</param>
    /// <param name="cancellationToken">Cancels writing the first frame.</param>
    /// <remarks>Without a redrawing terminal, the message is written once as a plain line.</remarks>
    public ValueTask<CommandTaskLog> StartSpinnerAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return _engine.StartTaskLogAsync(message, 0, cancellationToken);
    }

    /// <summary>
    /// Starts a task log: a spinner with a title and the last lines written to it, which are cleared when the task
    /// succeeds and shown in full when it fails.
    /// </summary>
    /// <param name="title">The name of the task.</param>
    /// <param name="visibleLines">How many of the most recent lines stay visible while the task runs, from 0 to 50.</param>
    /// <param name="cancellationToken">Cancels writing the first frame.</param>
    /// <remarks>Without a redrawing terminal, including with JSON output, the title and each line are written as plain lines to standard error.</remarks>
    public ValueTask<CommandTaskLog> StartTaskLogAsync(string title, int visibleLines = 6, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegative(visibleLines);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(visibleLines, 50);
        return _engine.StartTaskLogAsync(title, visibleLines, cancellationToken);
    }

    private static string[] Labels<T>(IReadOnlyList<CommandPromptChoice<T>> choices, out string?[] hints)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0) throw new ArgumentException("A prompt needs at least one choice.", nameof(choices));
        var labels = new string[choices.Count];
        hints = new string?[choices.Count];
        for (int index = 0; index < labels.Length; index++)
        {
            CommandPromptChoice<T> choice = choices[index] ?? throw new ArgumentException("A choice is null.", nameof(choices));
            labels[index] = choice.Label;
            hints[index] = choice.Hint;
        }

        return labels;
    }
}
