using System.Diagnostics;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

internal static partial class CompletionHelpTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("help/root-globals-render-once-in-human-and-json", GlobalHelp),
        new("completion/query-follows-command-value-and-position", QueryContexts),
        new("completion/native-shells-return-contextual-candidates", NativeShells),
        new("completion/native-registration-handles-spaces-and-bash-wordbreaks", NativeIntegration),
        new("completion/native-shells-complete-inline-and-positional-paths", NativePaths),
        new("completion/bash-readline-preserves-literal-arguments", BashReadline),
        new("completion/zsh-line-editor-preserves-literal-arguments", ZshReadline),
    ];

    private static CommandCatalog Catalog()
    {
        var builder = new CommandCatalogBuilder()
            .GlobalOption("verbose", "--verbose", CommandArity.Zero, new CommandHelp("Detailed output"), "-v")
            .GlobalOption("profile", "--profile", CommandArity.ExactlyOne, new CommandHelp(choices: ["work", "home office"]))
            .GlobalOption("internal", "--internal", CommandArity.Zero, new CommandHelp { Hidden = true });
        var parent = CatalogTests.ValidCommand(builder, "config").Alias("cfg").Option("parent", "--parent-only", CommandArity.Zero);
        parent.Subcommand<TestOptions, TestHandler, TestResult>("set")
            .BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory()).Produces(new TestCodec())
            .Alias("write")
            .Option("mode", "--mode", CommandArity.ExactlyOne, aliases: ["-m"])
            .Option("many", "--many", new CommandArity(1, 2))
            .Option("tag", "--tag", CommandArity.OneOrMore)
            .Option("directory", "--directory", CommandArity.ExactlyOne)
            .Option("file", "--file", CommandArity.ExactlyOne)
            .Option("secret", "--secret", CommandArity.ExactlyOne, isSensitive: true)
            .Option("hidden", "--hidden", CommandArity.Zero)
            .Argument("key", "key", CommandArity.ExactlyOne)
            .Argument("path", "path", CommandArity.ZeroOrOne)
            .ParameterHelp("mode", new CommandHelp(choices: ["fast", "careful mode", "quote's value", "$literal;value", "--special", "a\"b", "#literal", "@literal", "Fast"]))
            .ParameterHelp("many", new CommandHelp(choices: ["one", "two"]))
            .ParameterHelp("tag", new CommandHelp(choices: ["red", "blue"]))
            .ParameterHelp("directory", new CommandHelp { PathKind = CommandPathKind.Directory })
            .ParameterHelp("file", new CommandHelp { PathKind = CommandPathKind.File })
            .ParameterHelp("secret", new CommandHelp(choices: ["secret-choice"]) { PathKind = CommandPathKind.File })
            .ParameterHelp("hidden", new CommandHelp { Hidden = true })
            .ParameterHelp("key", new CommandHelp(choices: ["theme", "font size"]))
            .ParameterHelp("path", new CommandHelp { PathKind = CommandPathKind.File });
        parent.Subcommand<TestOptions, TestHandler, TestResult>("get")
            .BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory()).Produces(new TestCodec())
            .Option("other", "--sibling-only", CommandArity.Zero);
        CatalogTests.ValidCommand(builder, "deploy").Option("remote", "--remote-only", CommandArity.Zero);
        CatalogTests.ValidCommand(builder, "hidden-command").WithHelp(new CommandHelp { Hidden = true });
        return builder.DefaultCommand("deploy").Build();
    }

    private static async ValueTask GlobalHelp()
    {
        CommandCatalog catalog = Catalog();
        foreach (string[] args in new[] { new[] { "--help" }, new[] { "help", "cfg", "write" } })
        {
            foreach (bool json in new[] { false, true })
            {
                var console = new TestCommandConsole();
                var app = new CommandApp(catalog) { Name = "fixture", Console = console, HandleCancelKeyPress = false };
                AssertEx.Equal(0, await app.RunAsync(json ? [.. args, "--output=json"] : args));
                string help;
                if (json)
                {
                    using var envelope = CommandTestEnvelope.Parse(console.StandardOutput);
                    help = envelope.RootElement.GetProperty("payload").GetString()!;
                    AssertEx.Equal("runic.text/1", envelope.RootElement.GetProperty("payloadType").GetString());
                }
                else help = console.StandardOutput;
                AssertEx.Equal(1, help.Split("--verbose", StringSplitOptions.None).Length - 1);
                AssertEx.True(help.Contains("--verbose, -v", StringComparison.Ordinal));
                AssertEx.True(!help.Contains("--internal", StringComparison.Ordinal));
                AssertEx.True(!help.Contains("--hidden", StringComparison.Ordinal));
                if (args.Length == 1) AssertEx.True(!help.Contains("--mode", StringComparison.Ordinal));
                else AssertEx.True(help.Contains("--mode, -m", StringComparison.Ordinal));
            }
        }
    }

    private static readonly string[] Shells = ["bash", "zsh", "fish", "pwsh"];
    private static readonly string[] FishQuotedPrefixes = ["fixture cfg write --mode 'care", "fixture cfg write --mode careful\\ m"];
    private static readonly string[] PathSelectors = ["--directory", "--file"];
    private static readonly string[] RootCommands = ["config", "deploy", "cfg"];

    private static readonly string[][] Contexts =
    [
        [""], ["config", ""], ["cfg", "write", ""], ["cfg", "write", "--"],
        ["config", "set", "--mode", ""], ["config", "set", "--mode", "care"],
        ["config", "set", "--mode=care"], ["config", "set", "--mode=quote"],
        ["config", "set", "--mode", "fast", ""], ["config", "set", "--many", "one", ""],
        ["config", "set", "--many", "one", "two", ""],
        ["config", "set", "--many", "one", "--mode", ""],
        ["config", "set", "--tag", "red", "--mode=fast", "th"],
        ["config", "set", "--secret", ""], ["--profile", "home"],
        ["--profile", "work", "config", ""], ["config", "--verbose", ""],
        ["config", "set", "--tag", "red", "--", "th"], ["config", "set", "theme", "--"],
        ["config", "set", "--", ""], ["help", "cfg", ""], ["completion", ""],
        ["config", "get", "--"], ["deploy", "--"], ["--remote-only", ""],
    ];

    private static ValueTask QueryContexts()
    {
        CommandCatalog catalog = Catalog();
        AssertEx.SequenceEqual(RootCommands.Order(StringComparer.Ordinal), CommandCompletion.Query(catalog, [""]).Candidates.Where(value => !value.StartsWith('-') && value is not "help" and not "completion"));
        AssertEx.SequenceEqual(["careful mode"], CommandCompletion.Query(catalog, ["config", "set", "--mode", "care"]).Candidates);
        AssertEx.SequenceEqual(["--mode=careful mode"], CommandCompletion.Query(catalog, ["cfg", "write", "--mode=care"]).Candidates);
        AssertEx.SequenceEqual(["theme"], CommandCompletion.Query(catalog, ["config", "set", "th"]).Candidates);
        AssertEx.SequenceEqual(["font size", "theme"], CommandCompletion.Query(catalog, ["config", "set", "--", ""]).Candidates);
        AssertEx.SequenceEqual(Array.Empty<string>(), CommandCompletion.Query(catalog, ["config", "set", "--secret", ""]).Candidates);
        foreach (string[] context in Contexts)
        {
            CommandCompletionResult result = CommandCompletion.Query(catalog, context);
            AssertEx.True(!result.Candidates.Contains("hidden-command") && !result.Candidates.Contains("--hidden") && !result.Candidates.Contains("--internal"));
            if (context.Contains("set") || context.Contains("write"))
                AssertEx.True(!result.Candidates.Contains("--sibling-only") && !result.Candidates.Contains("--remote-only") && !result.Candidates.Contains("--parent-only"));
        }
        AssertEx.Equal(CommandPathKind.Directory, CommandCompletion.Query(catalog, ["config", "set", "--directory=spa"]).PathKind);
        AssertEx.Equal("--directory=", CommandCompletion.Query(catalog, ["config", "set", "--directory=spa"]).ValuePrefix);
        AssertEx.Equal(CommandPathKind.File, CommandCompletion.Query(catalog, ["config", "set", "theme", ""]).PathKind);
        AssertEx.Equal(CommandPathKind.File, CommandCompletion.Query(catalog, ["config", "set", "--tag", "red", "--mode=fast", "theme", ""]).PathKind);
        AssertEx.SequenceEqual(["theme"], CommandCompletion.Query(catalog, ["config", "set", "--tag", "red", "--mode=fast", "th"]).Candidates);
        AssertEx.Equal(CommandPathKind.None, CommandCompletion.Query(catalog, ["config", "set", "--secret", ""]).PathKind);
        var owned = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(owned, "completion");
        AssertEx.SequenceEqual(["completion"], CommandCompletion.Query(owned.Build(), ["help", "com"]).Candidates);
        return ValueTask.CompletedTask;
    }

    private static async ValueTask NativeShells()
    {
        await WithDirectory(async directory =>
        {
            CommandCatalog catalog = Catalog();
            foreach (string shell in Shells)
            {
                string script = CommandCompletion.Generate(catalog, "fixture", shell);
                string path = Path.Combine(directory, "completion." + (shell == "pwsh" ? "ps1" : shell));
                await File.WriteAllTextAsync(path, script);
                foreach (string[] context in Contexts.Where(words => !(words.Length > 2 && words[^2] == "theme")))
                {
                    string[]? actual = await RunShell(shell, QueryScript(shell, path, context), directory);
                    if (actual is null) break;
                    AssertEx.SequenceEqual(CommandCompletion.Query(catalog, context).Candidates, actual.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), shell + ": " + string.Join(" | ", context));
                }
            }
        });
    }

    private static async ValueTask NativeIntegration()
    {
        await WithDirectory(async directory =>
        {
            foreach (string shell in Shells)
            {
                string path = Path.Combine(directory, "completion." + (shell == "pwsh" ? "ps1" : shell));
                await File.WriteAllTextAsync(path, CommandCompletion.Generate(Catalog(), "fixture", shell));
                const string command = "fixture cfg write --mode=care";
                string script = shell switch
                {
                    "bash" => $"source {Sh(path)}; COMP_WORDS=(fixture cfg write --mode = care); COMP_CWORD=5; _runic_fixture; printf '%s\\n' \"${{COMPREPLY[@]}}\"",
                    "zsh" => $"compdef() {{ :; }}; compadd() {{ shift; printf '%s\\n' \"$@\"; }}; source {Sh(path)}; words=(fixture cfg write --mode=care); CURRENT=4; _runic_fixture",
                    "fish" => $"source {Fish(path)}; complete -C {Fish(command)}",
                    _ => $". {Ps(path)}; (TabExpansion2 {Ps(command)} {command.Length}).CompletionMatches | ForEach-Object {{ $_.ListItemText }}",
                };
                string[]? values = await RunShell(shell, script, directory);
                if (values is null) continue;
                AssertEx.SequenceEqual([shell == "bash" ? "careful\\ mode" : "--mode=careful mode"], values, shell);
                if (shell == "zsh")
                {
                    string raw = "--mode=\\$lit";
                    values = await RunShell(shell, $"compdef() {{ :; }}; compadd() {{ shift; printf '%s\\n' \"$@\"; }}; source {Sh(path)}; words=(fixture cfg write {Sh(raw)}); CURRENT=4; _runic_fixture", directory);
                    AssertEx.SequenceEqual(["--mode=$literal;value"], values!, "zsh escaped literal prefix");
                }
                if (shell == "fish")
                {
                    foreach (string quoted in FishQuotedPrefixes)
                    {
                        values = await RunShell(shell, $"source {Fish(path)}; complete -C {Fish(quoted)}", directory);
                        AssertEx.SequenceEqual(["careful mode"], values!, "fish quoted prefix: " + string.Join(" | ", values!));
                    }
                }
                if (shell == "pwsh")
                {
                    const string middle = "fixture cfg write --mode=careless --profile work";
                    values = await RunShell(shell, $". {Ps(path)}; (TabExpansion2 {Ps(middle)} {command.Length}).CompletionMatches | ForEach-Object {{ $_.ListItemText }}", directory);
                    AssertEx.SequenceEqual(["--mode=careful mode"], values!, "pwsh cursor within token");
                    const string choiceCommand = "fixture cfg write --mode ";
                    string roundtrip = $". {Ps(path)}; $matches = (TabExpansion2 {Ps(choiceCommand)} {choiceCommand.Length}).CompletionMatches; foreach ($match in $matches) {{ $tokens = $null; $errors = $null; $ast = [System.Management.Automation.Language.Parser]::ParseInput(({Ps(choiceCommand)} + $match.CompletionText), [ref]$tokens, [ref]$errors); $cmd = $ast.Find({{ param($node) $node -is [System.Management.Automation.Language.CommandAst] }}, $true); if ($errors.Count -ne 0 -or $cmd.CommandElements.Count -ne 5 -or $cmd.CommandElements[-1].Value -cne $match.ListItemText) {{ throw ('Candidate changed on insertion: ' + $match.ListItemText) }}; Write-Output $match.ListItemText }}";
                    values = await RunShell(shell, roundtrip, directory);
                    AssertEx.True(values!.Contains("a\"b") && values.Contains("#literal") && values.Contains("@literal"), "pwsh literal choice quoting");
                }
            }
        });
    }

    private static async ValueTask NativePaths()
    {
        await WithDirectory(async directory =>
        {
            Directory.CreateDirectory(Path.Combine(directory, "space directory"));
            await File.WriteAllTextAsync(Path.Combine(directory, "space file.txt"), "fixture");
            foreach (string shell in Shells)
            {
                string path = Path.Combine(directory, "completion." + (shell == "pwsh" ? "ps1" : shell));
                await File.WriteAllTextAsync(path, CommandCompletion.Generate(Catalog(), "fixture", shell));
                foreach (string selector in PathSelectors)
                {
                    string[] words = ["config", "set", selector + "=space"];
                    string[]? values = await RunShell(shell, PathScript(shell, path, words), directory);
                    if (values is null) break;
                    AssertEx.True(values.Any(value => value.Replace("\\ ", " ", StringComparison.Ordinal).Contains("space directory", StringComparison.Ordinal)), shell + " missing spaced directory: " + string.Join("|", values));
                    AssertEx.Equal(selector == "--file", values.Any(value => value.Replace("\\ ", " ", StringComparison.Ordinal).Contains("space file.txt", StringComparison.Ordinal)), shell + " path kind: " + string.Join("|", values));
                }
            }
        });
    }

    private static string QueryScript(string shell, string path, string[] words) => shell switch
    {
        "bash" => $"source {Sh(path)}; _runic_fixture_query {string.Join(' ', words.Select(Sh))}; printf '%s\\n' \"${{reply[@]}}\"",
        "zsh" => $"compdef() {{ :; }}; source {Sh(path)}; _runic_fixture_query {string.Join(' ', words.Select(Sh))}; printf '%s\\n' \"${{reply[@]}}\"",
        "fish" => $"source {Fish(path)}; _runic_fixture_query {string.Join(' ', words.Select(Fish))}",
        _ => $". {Ps(path)}; $result = _runic_fixture_query -Words @({string.Join(',', words.Select(Ps))}); $result.Candidates | ForEach-Object {{ Write-Output $_ }}",
    };
    private static string PathScript(string shell, string path, string[] words) => shell switch
    {
        "bash" => $"source {Sh(path)}; COMP_WORDS=(fixture {string.Join(' ', words.Select(Sh))}); COMP_CWORD={words.Length}; _runic_fixture; printf '%s\\n' \"${{COMPREPLY[@]}}\"",
        "zsh" => $"compdef() {{ :; }}; compadd() {{ :; }}; compset() {{ IPREFIX=\"$IPREFIX${{PREFIX%%=*}}=\"; PREFIX=\"${{PREFIX#*=}}\"; }}; _files() {{ [[ \"$IPREFIX\" == {Sh(words[^1].Contains('=') ? words[^1].Split('=')[0] + "=" : "")} ]] || return 99; if [[ \"$1\" == '-/' ]]; then print -rl -- *(/); else print -rl -- *; fi; }}; source {Sh(path)}; words=(fixture {string.Join(' ', words.Select(Sh))}); CURRENT={words.Length + 1}; PREFIX={Sh(words[^1])}; IPREFIX=''; _runic_fixture",
        "fish" => $"source {Fish(path)}; complete -C {Fish("fixture " + string.Join(' ', words))}",
        _ => $". {Ps(path)}; (TabExpansion2 {Ps("fixture " + string.Join(' ', words))} {("fixture " + string.Join(' ', words)).Length}).CompletionMatches | ForEach-Object {{ $_.CompletionText }}",
    };
    private static async ValueTask<string[]?> RunShell(string shell, string script, string directory)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory } };
        if (shell == "pwsh") { process.StartInfo.ArgumentList.Add("-NoProfile"); process.StartInfo.ArgumentList.Add("-NonInteractive"); process.StartInfo.ArgumentList.Add("-Command"); }
        else { process.StartInfo.ArgumentList.Add(shell == "bash" ? "--noprofile" : shell == "zsh" ? "-f" : "--no-config"); process.StartInfo.ArgumentList.Add("-c"); }
        process.StartInfo.ArgumentList.Add(script);
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { Console.WriteLine("SKIP unavailable native completion shell: " + shell); return null; }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellation.Token);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        string output = await stdout;
        AssertEx.Equal(0, process.ExitCode, shell + ": " + await stderr);
        AssertEx.True((await stderr).Length == 0, shell + ": " + await stderr);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('\t')[0]).ToArray();
    }
    private static async ValueTask WithDirectory(Func<string, Task> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "runic-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await action(directory); } finally { Directory.Delete(directory, recursive: true); }
    }
    private static string Sh(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string Fish(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    private static string Ps(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
