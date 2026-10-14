using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

internal enum PromptUnavailableReason
{
    JsonOutput,
    NotInteractive,
    Disabled,
}

/// <summary>Escape, Ctrl+C as a key, or the end of input on a line prompt. The executor reports <c>RCLI4000</c>.</summary>
internal sealed class CommandPromptCancelledException() : OperationCanceledException("The prompt was cancelled.");

/// <summary>A prompt without a default in a run that cannot ask. The executor reports <c>RCLI4001</c> with this diagnostic.</summary>
internal sealed class CommandPromptUnavailableException(CommandDiagnostic diagnostic) : InvalidOperationException(diagnostic.Message)
{
    internal const string Code = "RCLI4001";
    internal CommandDiagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>Chooses between the redrawing terminal prompts and the plain line prompts, and owns their shared text.</summary>
internal sealed class PromptEngine
{
    private readonly ICommandConsole _console;
    private readonly ICommandTerminal? _terminal;
    private readonly CommandPath _path;

    internal PromptEngine(ICommandConsole console, CommandTextContext text, CommandPath path, PromptUnavailableReason? reason)
    {
        _console = console;
        _terminal = console as ICommandTerminal;
        Text = text;
        _path = path;
        Reason = reason;
        Rich = reason is null && _terminal is { SupportsAnsi: true };
    }

    internal PromptUnavailableReason? Reason { get; }
    internal CommandTextContext Text { get; }
    internal bool Rich { get; }

    internal string Resolve(string key, string fallback, params string[] arguments) => Text.Resolve(key, fallback, arguments);

    internal CommandPromptUnavailableException Unavailable(string message)
    {
        (string kind, string text) = Reason switch
        {
            PromptUnavailableReason.JsonOutput => ("prompt-json-output",
                $"{message} needs an answer, but JSON output never prompts. Pass the value on the command line."),
            PromptUnavailableReason.Disabled => ("prompt-disabled",
                $"{message} needs an answer, but prompts are disabled. Pass the value on the command line."),
            _ => ("prompt-requires-terminal",
                $"{message} needs an answer, but the command is not running in an interactive terminal. Pass the value on the command line."),
        };
        // English here; the output dispatcher localizes diagnostics.{kind} like any other diagnostic.
        string[] arguments = [message];
        return new CommandPromptUnavailableException(new CommandDiagnostic(CommandPromptUnavailableException.Code, kind, text,
            CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Error, arguments: arguments, path: _path));
    }

    // Plain output goes through the terminal when there is one, so a styling console such as Spectre's does not colour it.
    internal ValueTask WritePlainAsync(string value, CancellationToken cancellationToken) =>
        _terminal is not null ? _terminal.WriteAsync(value.AsMemory(), cancellationToken) : _console.WriteErrorAsync(value.AsMemory(), cancellationToken);

    internal async ValueTask<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        // The process console reads synchronously and ignores the token, so the read runs on the thread pool and the
        // prompt stops waiting when Ctrl+C cancels the token instead of when the user presses Enter.
        string? line;
        try
        {
            ICommandConsole console = _console;
            line = await Task.Run(() => console.ReadLineAsync(cancellationToken).AsTask(), CancellationToken.None)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WritePlainAsync("\n", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (line is null)
        {
            // End of input (Ctrl+D): end the question's line before the cancellation is reported.
            await WritePlainAsync("\n", CancellationToken.None).ConfigureAwait(false);
            throw new CommandPromptCancelledException();
        }

        return line;
    }

    internal ValueTask<bool> ConfirmAsync(string message, bool? defaultValue, CancellationToken cancellationToken) =>
        Rich ? new RichPrompts(this, _terminal!).ConfirmAsync(message, defaultValue, cancellationToken)
            : new LinePrompts(this).ConfirmAsync(message, defaultValue, cancellationToken);

    internal ValueTask<string> TextAsync(string message, string? defaultValue, Func<string, string?>? validate, CancellationToken cancellationToken) =>
        Rich ? new RichPrompts(this, _terminal!).TextAsync(message, defaultValue, validate, cancellationToken)
            : new LinePrompts(this).TextAsync(message, defaultValue, validate, cancellationToken);

    internal ValueTask<int> SelectAsync(string message, string[] labels, string?[] hints, int? defaultIndex, CancellationToken cancellationToken) =>
        Rich ? new RichPrompts(this, _terminal!).SelectAsync(message, labels, hints, defaultIndex, cancellationToken)
            : new LinePrompts(this).SelectAsync(message, labels, hints, defaultIndex, cancellationToken);

    internal ValueTask<bool[]> MultiSelectAsync(string message, string[] labels, string?[] hints, bool[] selected, bool required, CancellationToken cancellationToken) =>
        Rich ? new RichPrompts(this, _terminal!).MultiSelectAsync(message, labels, hints, selected, required, cancellationToken)
            : new LinePrompts(this).MultiSelectAsync(message, labels, hints, selected, required, cancellationToken);

    internal async ValueTask<CommandTaskLog> StartTaskLogAsync(string title, int visibleLines, CancellationToken cancellationToken)
    {
        var log = new CommandTaskLog(this, Rich && _terminal is not null ? _terminal : null, title, visibleLines);
        await log.StartAsync(cancellationToken).ConfigureAwait(false);
        return log;
    }

    internal string Validate(string value, Func<string, string?>? validate) =>
        validate is not null ? validate(value) ?? string.Empty
            : value.Length == 0 ? Resolve(CommandTextKeys.PromptRequired, "An answer is required.") : string.Empty;

    internal (string Yes, string No) YesNo() =>
        (Resolve(CommandTextKeys.PromptYes, "Yes"), Resolve(CommandTextKeys.PromptNo, "No"));

    internal static bool? ParseYesNo(string answer, string yes, string no)
    {
        answer = answer.Trim();
        if (answer.Length == 0) return null;
        bool distinct = char.ToUpperInvariant(yes[0]) != char.ToUpperInvariant(no[0]);
        if (answer.Equals(yes, StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            answer.Equals("y", StringComparison.OrdinalIgnoreCase) || (distinct && answer.Length == 1 && char.ToUpperInvariant(answer[0]) == char.ToUpperInvariant(yes[0])))
            return true;
        if (answer.Equals(no, StringComparison.OrdinalIgnoreCase) || answer.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            answer.Equals("n", StringComparison.OrdinalIgnoreCase) || (distinct && answer.Length == 1 && char.ToUpperInvariant(answer[0]) == char.ToUpperInvariant(no[0])))
            return false;
        return null;
    }

    internal static bool IsCancelKey(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Escape || (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0) || key.KeyChar == '\u0003';

    /// <summary>Removes control characters and ANSI sequences, so a line cannot move the cursor or break redraw arithmetic.</summary>
    internal static string Sanitize(string value)
    {
        StringBuilder? builder = null;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (!char.IsControl(character))
            {
                builder?.Append(character);
                continue;
            }

            builder ??= new StringBuilder(value, 0, index, value.Length);
            if (character == '\t') builder.Append(' ');
            else if (character == '\u001b' && index + 1 < value.Length && value[index + 1] == '[')
            {
                index += 2;
                while (index < value.Length && value[index] is < '@' or > '~') index++;
            }
            else if (character == '\u001b' && index + 1 < value.Length && value[index + 1] == ']')
            {
                // Operating system command, ended by BEL or ESC \.
                index += 2;
                while (index < value.Length && value[index] != '\u0007' && !(value[index] == '\u001b' && index + 1 < value.Length && value[index + 1] == '\\')) index++;
                if (index < value.Length && value[index] == '\u001b') index++;
            }
        }

        return builder?.ToString() ?? value;
    }
}

/// <summary>Plain prompts: one question and numbered choices per line, answered with a line of input.</summary>
internal sealed class LinePrompts(PromptEngine engine)
{
    internal async ValueTask<bool> ConfirmAsync(string message, bool? defaultValue, CancellationToken cancellationToken)
    {
        (string yes, string no) = engine.YesNo();
        bool distinct = char.ToUpperInvariant(yes[0]) != char.ToUpperInvariant(no[0]);
        string y = distinct ? yes[..1] : yes, n = distinct ? no[..1] : no;
        y = defaultValue == true ? y.ToUpperInvariant() : y.ToLowerInvariant();
        n = defaultValue == false ? n.ToUpperInvariant() : n.ToLowerInvariant();
        while (true)
        {
            await engine.WritePlainAsync($"{message} [{y}/{n}]: ", cancellationToken).ConfigureAwait(false);
            string answer = await engine.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (answer.Trim().Length == 0 && defaultValue is { } value) return value;
            if (PromptEngine.ParseYesNo(answer, yes, no) is { } parsed) return parsed;
            await engine.WritePlainAsync(engine.Resolve(CommandTextKeys.PromptAnswerYesNo, $"Answer {yes} or {no}.", yes, no) + "\n", cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<string> TextAsync(string message, string? defaultValue, Func<string, string?>? validate, CancellationToken cancellationToken)
    {
        while (true)
        {
            await engine.WritePlainAsync(defaultValue is null ? $"{message}: " : $"{message} [{defaultValue}]: ", cancellationToken).ConfigureAwait(false);
            string answer = await engine.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (answer.Length == 0 && defaultValue is not null) answer = defaultValue;
            string error = engine.Validate(answer, validate);
            if (error.Length == 0) return answer;
            await engine.WritePlainAsync(error + "\n", cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<int> SelectAsync(string message, string[] labels, string?[] hints, int? defaultIndex, CancellationToken cancellationToken)
    {
        await WriteChoicesAsync(message, labels, hints, cancellationToken).ConfigureAwait(false);
        string count = labels.Length.ToString(engine.Text.Culture);
        string instruction = engine.Resolve(CommandTextKeys.PromptChooseOne, $"Enter a number from 1 to {count}", count);
        if (defaultIndex is { } preset) instruction += $" [{labels[preset]}]";
        while (true)
        {
            await engine.WritePlainAsync(instruction + ": ", cancellationToken).ConfigureAwait(false);
            string answer = (await engine.ReadLineAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (answer.Length == 0 && defaultIndex is { } value) return value;
            if (Find(answer, labels) is { } index) return index;
        }
    }

    internal async ValueTask<bool[]> MultiSelectAsync(string message, string[] labels, string?[] hints, bool[] selected, bool required, CancellationToken cancellationToken)
    {
        await WriteChoicesAsync(message, labels, hints, cancellationToken).ConfigureAwait(false);
        string count = labels.Length.ToString(engine.Text.Culture);
        string instruction = engine.Resolve(CommandTextKeys.PromptChooseMany, $"Enter numbers from 1 to {count}, separated by commas", count);
        var defaults = new List<string>();
        for (int index = 0; index < selected.Length; index++)
            if (selected[index]) defaults.Add((index + 1).ToString(engine.Text.Culture));
        if (defaults.Count > 0) instruction += $" [{string.Join(",", defaults)}]";
        while (true)
        {
            await engine.WritePlainAsync(instruction + ": ", cancellationToken).ConfigureAwait(false);
            string answer = (await engine.ReadLineAsync(cancellationToken).ConfigureAwait(false)).Trim();
            bool[] result = (bool[])selected.Clone();
            bool valid = true;
            if (answer.Length > 0)
            {
                Array.Clear(result);
                foreach (string part in answer.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Find(part, labels) is { } index) result[index] = true;
                    else valid = false;
                }
            }

            if (!valid) continue;
            if (required && Array.IndexOf(result, true) < 0)
            {
                await engine.WritePlainAsync(engine.Resolve(CommandTextKeys.PromptRequired, "An answer is required.") + "\n", cancellationToken).ConfigureAwait(false);
                continue;
            }

            return result;
        }
    }

    private async ValueTask WriteChoicesAsync(string message, string[] labels, string?[] hints, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(message).Append('\n');
        for (int index = 0; index < labels.Length; index++)
        {
            builder.Append("  ").Append((index + 1).ToString(engine.Text.Culture)).Append(". ").Append(labels[index]);
            if (hints[index] is { } hint) builder.Append(" - ").Append(hint);
            builder.Append('\n');
        }

        await engine.WritePlainAsync(builder.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private int? Find(string answer, string[] labels)
    {
        if (int.TryParse(answer, NumberStyles.None, engine.Text.Culture, out int number) && number >= 1 && number <= labels.Length) return number - 1;
        for (int index = 0; index < labels.Length; index++)
            if (string.Equals(labels[index], answer, StringComparison.OrdinalIgnoreCase)) return index;
        return null;
    }
}

/// <summary>Symbols and colours of the redrawing prompts.</summary>
internal sealed class PromptStyle(bool unicode, int width)
{
    internal const string Accent = "36", Success = "32", Failure = "31", Warning = "33", Muted = "90", Inverse = "7";
    internal int Width { get; } = Math.Max(20, width);
    internal string Active => unicode ? "◆" : "*";
    internal string Done => unicode ? "◇" : "o";
    internal string Cancelled => unicode ? "■" : "x";
    internal string Error => unicode ? "▲" : "!";
    internal string Bar => unicode ? "│" : "|";
    internal string End => unicode ? "└" : "'";
    internal string Pointer => unicode ? "›" : ">";
    internal string RadioOn => unicode ? "●" : "(*)";
    internal string RadioOff => unicode ? "○" : "( )";
    internal string BoxOn => unicode ? "◼" : "[x]";
    internal string BoxOff => unicode ? "◻" : "[ ]";
    internal string More => unicode ? "…" : "...";
    internal string[] Spinner => unicode ? ["◒", "◐", "◓", "◑"] : ["|", "/", "-", "\\"];

    /// <summary>Builds one screen line, cut to the terminal width so it never wraps.</summary>
    internal LineBuilder Line() => new(Width - 1, More);
}

internal sealed class LineBuilder(int limit, string more)
{
    private readonly StringBuilder _text = new();
    private int _visible;
    private bool _full;

    internal LineBuilder Add(string text, string? style = null)
    {
        if (_full || text.Length == 0) return this;
        text = PromptEngine.Sanitize(text);
        if (_visible + text.Length > limit)
        {
            int keep = Math.Max(0, limit - _visible - more.Length);
            text = text[..Math.Min(text.Length, keep)] + more;
            _full = true;
        }

        _visible += text.Length;
        if (style is null) _text.Append(text);
        else _text.Append("\u001b[").Append(style).Append('m').Append(text).Append("\u001b[0m");
        return this;
    }

    public override string ToString() => _text.ToString();
}

/// <summary>Owns one redrawn block of lines: hides the cursor, redraws in place and restores the terminal however it ends.</summary>
internal sealed class TerminalFrame
{
    internal const string HideCursor = "\u001b[?25l", ShowCursor = "\u001b[?25h";
    private readonly ICommandTerminal _terminal;
    private readonly EventHandler _restoreOnExit;
    private int _lines;
    private int _ended;

    private TerminalFrame(ICommandTerminal terminal)
    {
        _terminal = terminal;
        // A second Ctrl+C exits the process at once; the exit still runs this, so the cursor comes back.
        _restoreOnExit = (_, _) =>
        {
            try { _terminal.WriteAsync(ShowCursor.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        };
    }

    internal static async ValueTask<TerminalFrame> StartAsync(ICommandTerminal terminal, CancellationToken cancellationToken)
    {
        var frame = new TerminalFrame(terminal);
        AppDomain.CurrentDomain.ProcessExit += frame._restoreOnExit;
        try
        {
            await terminal.WriteAsync(HideCursor.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            AppDomain.CurrentDomain.ProcessExit -= frame._restoreOnExit;
            throw;
        }

        return frame;
    }

    internal ValueTask DrawAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken) =>
        _terminal.WriteAsync(Compose(lines, final: false).AsMemory(), cancellationToken);

    /// <summary>Replaces the block with its final lines, ends them with a newline and shows the cursor again.</summary>
    internal async ValueTask EndAsync(IReadOnlyList<string> lines)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        try
        {
            await _terminal.WriteAsync((Compose(lines, final: true) + ShowCursor).AsMemory(), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= _restoreOnExit;
        }
    }

    private string Compose(IReadOnlyList<string> lines, bool final)
    {
        var builder = new StringBuilder();
        if (_lines > 0)
        {
            builder.Append('\r');
            if (_lines > 1) builder.Append("\u001b[").Append((_lines - 1).ToString(CultureInfo.InvariantCulture)).Append('A');
        }

        builder.Append("\u001b[J");
        for (int index = 0; index < lines.Count; index++)
        {
            if (index > 0) builder.Append('\n');
            builder.Append(lines[index]);
        }

        if (final && lines.Count > 0) builder.Append('\n');
        _lines = final ? 0 : lines.Count;
        return builder.ToString();
    }
}

/// <summary>Prompts that redraw in place and are answered with the arrow keys.</summary>
internal sealed class RichPrompts(PromptEngine engine, ICommandTerminal terminal)
{
    private const int MaximumVisibleChoices = 10;
    private readonly PromptStyle _style = new(terminal.SupportsUnicode, terminal.Width);

    internal async ValueTask<bool> ConfirmAsync(string message, bool? defaultValue, CancellationToken cancellationToken)
    {
        (string yes, string no) = engine.YesNo();
        bool? value = defaultValue;
        string error = string.Empty;
        PromptStyle style = _style;
        return await RunAsync<bool>(message, () =>
        {
            LineBuilder line = style.Line().Add(style.End + "  ", PromptStyle.Accent);
            line.Add(value == true ? style.RadioOn : style.RadioOff, value == true ? PromptStyle.Accent : PromptStyle.Muted).Add(" ")
                .Add(yes, value == true ? null : PromptStyle.Muted).Add(" / ", PromptStyle.Muted)
                .Add(value == false ? style.RadioOn : style.RadioOff, value == false ? PromptStyle.Accent : PromptStyle.Muted).Add(" ")
                .Add(no, value == false ? null : PromptStyle.Muted);
            if (error.Length > 0) line.Add("  " + style.Error + " " + error, PromptStyle.Warning);
            return [line.ToString()];
        }, key =>
        {
            error = string.Empty;
            switch (key.Key)
            {
                case ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Tab:
                    value = !(value ?? false);
                    return default;
                case ConsoleKey.Enter when value is { } answer:
                    return (true, answer);
                case ConsoleKey.Enter:
                    error = engine.Resolve(CommandTextKeys.PromptAnswerYesNo, $"Answer {yes} or {no}.", yes, no);
                    return default;
            }

            if (key.KeyChar != '\0' && PromptEngine.ParseYesNo(key.KeyChar.ToString(), yes, no) is { } typed) return (true, typed);
            return default;
        }, answer => answer ? yes : no, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<string> TextAsync(string message, string? defaultValue, Func<string, string?>? validate, CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        int caret = 0;
        string error = string.Empty;
        PromptStyle style = _style;
        PromptEngine owner = engine;
        return await RunAsync<string>(message, () =>
        {
            LineBuilder input = style.Line().Add(style.Bar + "  ", PromptStyle.Accent);
            if (buffer.Length == 0 && defaultValue is not null)
                input.Add(defaultValue.Length > 0 ? defaultValue[..1] : " ", PromptStyle.Inverse).Add(defaultValue.Length > 1 ? defaultValue[1..] : string.Empty, PromptStyle.Muted);
            else
            {
                string text = buffer.ToString();
                input.Add(text[..caret]).Add(caret < text.Length ? text[caret].ToString() : " ", PromptStyle.Inverse).Add(caret < text.Length ? text[(caret + 1)..] : string.Empty);
            }

            LineBuilder end = style.Line().Add(style.End, PromptStyle.Accent);
            if (error.Length > 0) end.Add("  " + style.Error + " " + error, PromptStyle.Warning);
            return [input.ToString(), end.ToString()];
        }, key =>
        {
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    string answer = buffer.Length == 0 && defaultValue is not null ? defaultValue : buffer.ToString();
                    error = owner.Validate(answer, validate);
                    return error.Length == 0 ? (true, answer) : default;
                case ConsoleKey.Backspace when caret > 0:
                    buffer.Remove(--caret, 1);
                    break;
                case ConsoleKey.Delete when caret < buffer.Length:
                    buffer.Remove(caret, 1);
                    break;
                case ConsoleKey.LeftArrow when caret > 0:
                    caret--;
                    break;
                case ConsoleKey.RightArrow when caret < buffer.Length:
                    caret++;
                    break;
                case ConsoleKey.Home:
                    caret = 0;
                    break;
                case ConsoleKey.End:
                    caret = buffer.Length;
                    break;
                default:
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) buffer.Insert(caret++, key.KeyChar);
                    break;
            }

            error = string.Empty;
            return default;
        }, answer => answer, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<int> SelectAsync(string message, string[] labels, string?[] hints, int? defaultIndex, CancellationToken cancellationToken)
    {
        int cursor = defaultIndex ?? 0;
        PromptStyle style = _style;
        string hint = engine.Resolve(CommandTextKeys.PromptSelectHint, "Arrow keys to move, Enter to confirm");
        return await RunAsync<int>(message, () => ChoiceLines(style, labels, hints, cursor, index => index == cursor ? style.RadioOn : style.RadioOff,
            index => index == cursor ? PromptStyle.Accent : PromptStyle.Muted, hint, string.Empty), key =>
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow or ConsoleKey.LeftArrow or ConsoleKey.K: cursor = (cursor + labels.Length - 1) % labels.Length; break;
                case ConsoleKey.DownArrow or ConsoleKey.RightArrow or ConsoleKey.J or ConsoleKey.Tab: cursor = (cursor + 1) % labels.Length; break;
                case ConsoleKey.Home or ConsoleKey.PageUp: cursor = 0; break;
                case ConsoleKey.End or ConsoleKey.PageDown: cursor = labels.Length - 1; break;
                case ConsoleKey.Enter: return (true, cursor);
            }

            return default;
        }, index => labels[index], cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<bool[]> MultiSelectAsync(string message, string[] labels, string?[] hints, bool[] selected, bool required, CancellationToken cancellationToken)
    {
        int cursor = Math.Max(0, Array.IndexOf(selected, true));
        bool[] state = (bool[])selected.Clone();
        string error = string.Empty;
        PromptStyle style = _style;
        PromptEngine owner = engine;
        string hint = engine.Resolve(CommandTextKeys.PromptMultiSelectHint, "Arrow keys to move, Space to select, A for all, Enter to confirm");
        return await RunAsync<bool[]>(message, () => ChoiceLines(style, labels, hints, cursor, index => state[index] ? style.BoxOn : style.BoxOff,
            index => state[index] ? PromptStyle.Success : index == cursor ? PromptStyle.Accent : PromptStyle.Muted, hint, error), key =>
        {
            error = string.Empty;
            switch (key.Key)
            {
                case ConsoleKey.UpArrow or ConsoleKey.K: cursor = (cursor + labels.Length - 1) % labels.Length; break;
                case ConsoleKey.DownArrow or ConsoleKey.J or ConsoleKey.Tab: cursor = (cursor + 1) % labels.Length; break;
                case ConsoleKey.Home or ConsoleKey.PageUp: cursor = 0; break;
                case ConsoleKey.End or ConsoleKey.PageDown: cursor = labels.Length - 1; break;
                case ConsoleKey.Spacebar or ConsoleKey.RightArrow or ConsoleKey.LeftArrow: state[cursor] = !state[cursor]; break;
                case ConsoleKey.A:
                    bool all = Array.TrueForAll(state, static value => value);
                    Array.Fill(state, !all);
                    break;
                case ConsoleKey.Enter when required && Array.IndexOf(state, true) < 0:
                    error = owner.Resolve(CommandTextKeys.PromptRequired, "An answer is required.");
                    break;
                case ConsoleKey.Enter:
                    return (true, (bool[])state.Clone());
            }

            return default;
        }, answer =>
        {
            var chosen = new List<string>();
            for (int index = 0; index < answer.Length; index++)
                if (answer[index]) chosen.Add(labels[index]);
            return string.Join(", ", chosen);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static List<string> ChoiceLines(PromptStyle style, string[] labels, string?[] hints, int cursor,
        Func<int, string> marker, Func<int, string> markerStyle, string hint, string error)
    {
        int first = labels.Length <= MaximumVisibleChoices ? 0 : Math.Clamp(cursor - MaximumVisibleChoices / 2, 0, labels.Length - MaximumVisibleChoices);
        int last = Math.Min(labels.Length, first + MaximumVisibleChoices);
        var lines = new List<string>();
        if (first > 0) lines.Add(style.Line().Add(style.Bar + "  ", PromptStyle.Accent).Add("  " + style.More, PromptStyle.Muted).ToString());
        for (int index = first; index < last; index++)
        {
            bool active = index == cursor;
            LineBuilder line = style.Line().Add(style.Bar + "  ", PromptStyle.Accent)
                .Add(active ? style.Pointer + " " : "  ", PromptStyle.Accent)
                .Add(marker(index), markerStyle(index)).Add(" ")
                .Add(labels[index], active ? null : PromptStyle.Muted);
            if (hints[index] is { } text) line.Add(" (" + text + ")", PromptStyle.Muted);
            lines.Add(line.ToString());
        }

        if (last < labels.Length) lines.Add(style.Line().Add(style.Bar + "  ", PromptStyle.Accent).Add("  " + style.More, PromptStyle.Muted).ToString());
        LineBuilder end = style.Line().Add(style.End + "  ", PromptStyle.Accent);
        lines.Add((error.Length > 0 ? end.Add(style.Error + " " + error, PromptStyle.Warning) : end.Add(hint, PromptStyle.Muted)).ToString());
        return lines;
    }

    /// <summary>Draws the prompt, feeds keys to it until it answers, and leaves one summary line however it ends.</summary>
    private async ValueTask<T> RunAsync<T>(string message, Func<List<string>> body, Func<ConsoleKeyInfo, (bool Done, T Value)> onKey, Func<T, string> summary,
        CancellationToken cancellationToken)
    {
        PromptStyle style = _style;
        TerminalFrame frame = await TerminalFrame.StartAsync(terminal, cancellationToken).ConfigureAwait(false);
        bool answered = false;
        T answer = default!;
        try
        {
            while (true)
            {
                var lines = new List<string> { style.Line().Add(style.Active, PromptStyle.Accent).Add("  " + message).ToString() };
                lines.AddRange(body());
                await frame.DrawAsync(lines, cancellationToken).ConfigureAwait(false);
                ConsoleKeyInfo key = await terminal.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
                if (PromptEngine.IsCancelKey(key)) throw new CommandPromptCancelledException();
                (bool done, T result) = onKey(key);
                if (done)
                {
                    answer = result;
                    answered = true;
                    break;
                }
            }
        }
        finally
        {
            string line = answered
                ? style.Line().Add(style.Done, PromptStyle.Success).Add("  " + message + "  ").Add(summary(answer), PromptStyle.Muted).ToString()
                : style.Line().Add(style.Cancelled, PromptStyle.Failure).Add("  " + message + "  ")
                    .Add(engine.Resolve(CommandTextKeys.PromptCancelled, "Cancelled"), PromptStyle.Failure).ToString();
            await frame.EndAsync([line]).ConfigureAwait(false);
        }

        return answer;
    }
}
