using System.Diagnostics;

namespace Runic.CommandLine.Tests;

internal static partial class CompletionHelpTests
{
    private static ValueTask BashReadline() => ShellReadline("bash");
    private static ValueTask ZshReadline() => ShellReadline("zsh");

    private static async ValueTask ShellReadline(string shell)
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP native shell PTY requires a Unix terminal."); return; }
        await WithDirectory(async directory =>
        {
            string completion = Path.Combine(directory, "completion." + shell);
            await File.WriteAllTextAsync(completion, CommandCompletion.Generate(Catalog(), "fixture", shell));
            Directory.CreateDirectory(Path.Combine(directory, "careful mode"));
            Directory.CreateDirectory(Path.Combine(directory, "space directory"));
            await File.WriteAllTextAsync(Path.Combine(directory, "space file.txt"), "fixture");
            string harness = Path.Combine(directory, "readline.py");
            await File.WriteAllTextAsync(harness, """
                import errno, json, os, pty, re, select, shlex, shutil, signal, sys, time
                shell = sys.argv[2]
                executable = shutil.which(shell)
                if not executable:
                    print('SKIP unavailable native terminal shell: ' + shell)
                    sys.exit(0)
                pid, terminal = pty.fork()
                if pid == 0:
                    os.environ.update(PS1='__READY__ ', PS2='__MORE__ ', TERM='dumb', INPUTRC='/dev/null')
                    arguments = [executable, '--noprofile', '--norc', '-i'] if shell == 'bash' else [executable, '-f', '-i']
                    os.execv(executable, arguments)
                def prompt():
                    output = b''
                    deadline = time.monotonic() + 5
                    while time.monotonic() < deadline:
                        ready, _, _ = select.select([terminal], [], [], max(0, deadline - time.monotonic()))
                        if not ready: break
                        try: chunk = os.read(terminal, 8192)
                        except OSError as error:
                            if error.errno == errno.EIO: break
                            raise
                        if not chunk: break
                        output += chunk
                        if len(output) > 65536: raise AssertionError('PTY output exceeded fixture bound')
                        if re.sub(rb'\x1b\[[0-?]*[ -/]*[@-~]', b'', output).endswith(b'__READY__ '): return output.decode(errors='replace')
                    raise AssertionError(shell + ' prompt timeout: ' + repr(output))
                def submit(line):
                    os.write(terminal, line.encode() + b'\n')
                    return prompt()
                try:
                    prompt()
                    if shell == 'zsh': submit('autoload -Uz compinit; compinit -D; unsetopt beep')
                    submit('source ' + shlex.quote(sys.argv[1]) + '; fixture() { printf "\\n__COUNT__%s\\n" "$#"; for value in "$@"; do printf "__VALUE__%s\\n" "$value"; done; }')
                    cases = [
                        ('fixture cfg write --mode care\t', 4, 'careful mode'),
                        ("fixture cfg write --mode quote\t", 4, "quote's value"),
                        ('fixture cfg write --mode \\$lit\t', 4, '$literal;value'),
                        ("fixture cfg write --mode 'care\t", 4, 'careful mode'),
                        ("fixture cfg write --mode 'quote\t", 4, "quote's value"),
                        ('fixture cfg write --mode "care\t', 4, 'careful mode'),
                        ('fixture cfg write --mode "\\$lit\t', 4, '$literal;value'),
                        ('fixture cfg write --mode careful\\ m\t', 4, 'careful mode'),
                        ('fixture cfg write --mode=care\t', 3, '--mode=careful mode'),
                        ('fixture cfg write --directory=spa\t', 3, '--directory=space directory/'),
                        ('fixture cfg write --file=space\\ f\t', 3, '--file=space file.txt'),
                    ]
                    for line, count, value in cases:
                        output = submit(line)
                        expected = [value]
                        # ZLE removes its automatically added directory slash when Enter accepts the line.
                        if shell == 'zsh' and value.startswith('--directory=') and value.endswith('/'):
                            expected.append(value[:-1])
                        if '\n__COUNT__' + str(count) + '\r\n' not in output or not any('\n__VALUE__' + item + '\r\n' in output for item in expected):
                            raise AssertionError(json.dumps({'line': line, 'count': count, 'value': value, 'output': output}))
                    print('PASS native literal argument insertion: ' + shell)
                finally:
                    try: os.kill(pid, signal.SIGTERM)
                    except ProcessLookupError: pass
                    os.close(terminal)
                    os.waitpid(pid, 0)
                """);
            using var process = new Process { StartInfo = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory } };
            process.StartInfo.ArgumentList.Add(harness);
            process.StartInfo.ArgumentList.Add(completion);
            process.StartInfo.ArgumentList.Add(shell);
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception) { Console.WriteLine("SKIP native shell PTY requires Python 3."); return; }
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellation.Token);
            try { await process.WaitForExitAsync(cancellation.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            AssertEx.Equal(0, process.ExitCode, await errors);
            string actual = await output;
            if (actual.StartsWith("SKIP", StringComparison.Ordinal)) { Console.WriteLine(actual.Trim()); return; }
            AssertEx.True(actual.Contains("PASS native literal argument insertion", StringComparison.Ordinal));
        });
    }
}
