using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>
/// A running task shown as a spinner with a title, an optional status and the most recent lines written to it.
/// Create one with <see cref="CommandPrompts.StartTaskLogAsync"/> or <see cref="CommandPrompts.StartSpinnerAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// On a terminal that can redraw the block animates on standard error, also when prompts are disabled, until <see cref="SucceedAsync"/> or
/// <see cref="FailAsync"/> replaces it with one summary line, followed by the log when requested. Otherwise, including
/// with JSON output, the title, each line, each status and the summary are written as plain lines to standard error.
/// </para>
/// <para>
/// Members may be called from several threads, for example from process output callbacks. Lines are stripped of
/// control characters and ANSI sequences. Write other output only after the task has ended. Disposing a task that has
/// not ended ends it as cancelled.
/// </para>
/// </remarks>
public sealed class CommandTaskLog : IAsyncDisposable
{
    private const int RetainedLines = 1000;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(80);
    private readonly PromptEngine _engine;
    private readonly ICommandTerminal? _terminal;
    private readonly PromptStyle? _style;
    private readonly string _title;
    private readonly int _visibleLines;
    private readonly Queue<string> _lines = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TerminalFrame? _frame;
    private Task? _animation;
    private string? _status;
    private int _tick;
    private bool _ended;

    internal CommandTaskLog(PromptEngine engine, ICommandTerminal? terminal, string title, int visibleLines)
    {
        _engine = engine;
        _terminal = terminal;
        _style = terminal is null ? null : new PromptStyle(terminal);
        _title = title;
        _visibleLines = visibleLines;
    }

    internal async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_terminal is null)
        {
            await _engine.WritePlainAsync(PromptEngine.Sanitize(_title) + "\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        _frame = await TerminalFrame.StartAsync(_terminal, cancellationToken).ConfigureAwait(false);
        try
        {
            await _frame.DrawAsync(Frame(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ended = true;
            await _frame.EndAsync([]).ConfigureAwait(false);
            throw;
        }

        _animation = AnimateAsync();
    }

    /// <summary>Adds a line to the log. A redrawing terminal shows only the most recent lines while the task runs.</summary>
    /// <param name="line">The text; line breaks in it start new lines.</param>
    /// <param name="cancellationToken">Cancels a plain write.</param>
    public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        string[] parts = line.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ended) return;
            foreach (string part in parts)
            {
                string clean = PromptEngine.Sanitize(part);
                if (_terminal is null) await _engine.WritePlainAsync(clean + "\n", cancellationToken).ConfigureAwait(false);
                else
                {
                    _lines.Enqueue(clean);
                    if (_lines.Count > RetainedLines) _lines.Dequeue();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Shows a short status after the title, such as the elapsed time or the current step.</summary>
    /// <param name="status">The status, or null to clear it.</param>
    /// <param name="cancellationToken">Cancels a plain write.</param>
    /// <remarks>Without a redrawing terminal each change is written as a <c>title: status</c> line.</remarks>
    public async ValueTask SetStatusAsync(string? status, CancellationToken cancellationToken = default)
    {
        status = string.IsNullOrWhiteSpace(status) ? null : PromptEngine.Sanitize(status);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ended || status == _status) return;
            _status = status;
            if (_terminal is null && status is not null)
                await _engine.WritePlainAsync(PromptEngine.Sanitize(_title) + ": " + status + "\n", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the task successfully, replacing the block with one summary line.</summary>
    /// <param name="message">The summary, or null for the translated <c>Done</c>.</param>
    /// <param name="showLog">True to keep the retained log below the summary on a redrawing terminal.</param>
    /// <param name="cancellationToken">Cancels a plain write.</param>
    public ValueTask SucceedAsync(string? message = null, bool showLog = false, CancellationToken cancellationToken = default) =>
        EndAsync(true, message ?? _engine.Resolve(CommandTextKeys.PromptDone, "Done"), showLog, cancellationToken);

    /// <summary>Ends the task as failed, replacing the block with one summary line followed by the retained log.</summary>
    /// <param name="message">The failure summary.</param>
    /// <param name="showLog">False to omit the log on a redrawing terminal.</param>
    /// <param name="cancellationToken">Cancels a plain write.</param>
    public ValueTask FailAsync(string message, bool showLog = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return EndAsync(false, message, showLog, cancellationToken);
    }

    /// <summary>Ends a task that has not ended as cancelled and stops its animation.</summary>
    public async ValueTask DisposeAsync()
    {
        await EndAsync(false, _engine.Resolve(CommandTextKeys.PromptCancelled, "Cancelled"), false, CancellationToken.None).ConfigureAwait(false);
        _stop.Dispose();
    }

    private async ValueTask EndAsync(bool success, string message, bool showLog, CancellationToken cancellationToken)
    {
        message = PromptEngine.Sanitize(message);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_ended) return;
            _ended = true;
            _stop.Cancel();
            if (_terminal is null || _frame is null || _style is null)
            {
                await _engine.WritePlainAsync(PromptEngine.Sanitize(_title) + ": " + message + "\n", cancellationToken).ConfigureAwait(false);
                return;
            }

            PromptStyle style = _style;
            var lines = new List<string>
            {
                style.Line().Add(success ? style.Done : style.Cancelled, success ? PromptStyle.Success : PromptStyle.Failure)
                    .Add("  " + _title + "  ").Add(message, success ? PromptStyle.Muted : PromptStyle.Failure).ToString(),
            };
            if (showLog)
                foreach (string line in _lines) lines.Add(style.Line().Add(style.Bar + "  ", PromptStyle.Muted).Add(line).ToString());
            await _frame.EndAsync(lines).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (_animation is not null)
        {
            // The animation is cosmetic: a failed frame write stops it without failing the command.
            try { await _animation.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException)) { }
        }
    }

    private async Task AnimateAsync()
    {
        CancellationToken stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(FrameInterval, stop).ConfigureAwait(false);
            await _gate.WaitAsync(stop).ConfigureAwait(false);
            try
            {
                if (_ended) return;
                _tick++;
                await _frame!.DrawAsync(Frame(), stop).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private List<string> Frame()
    {
        PromptStyle style = _style!;
        string[] spinner = style.Spinner;
        LineBuilder title = style.Line().Add(spinner[_tick % spinner.Length], PromptStyle.Accent).Add("  " + _title);
        if (_status is not null) title.Add("  " + _status, PromptStyle.Muted);
        var lines = new List<string> { title.ToString() };
        // The title and the visible lines stay below the terminal height, so a short terminal never scrolls copies.
        int visible = Math.Min(_visibleLines, Math.Max(0, style.Height - 2));
        int skip = Math.Max(0, _lines.Count - visible);
        foreach (string line in _lines)
        {
            if (skip-- > 0) continue;
            lines.Add(style.Line().Add(style.Bar + "  ", PromptStyle.Muted).Add(line, PromptStyle.Muted).ToString());
        }

        return lines;
    }
}
