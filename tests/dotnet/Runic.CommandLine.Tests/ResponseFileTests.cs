using System.Globalization;
using System.Text;
using System.Text.Json;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// CommandCatalogBuilder.ResponseFiles (rcl-11, D031.5). The expansion cases are those of runic-translations'
// CommandLine.ExpandResponseFiles and its CliIntegrationTests, so that tool can replace its own expander.
internal sealed class ResponseFileTests
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // Ported from runic-translations CliIntegrationTests.ResponseFilesUseMsvcrtBackslashRules.
    [Test, DisplayName("response-files/keep-backslashes-unless-they-precede-a-quote")]
    public async ValueTask MsvcrtBackslashRules()
    {
        using var directory = new TemporaryDirectory();
        string responseFile = directory.Write("arguments.rsp", string.Join('\n',
            "# comment",
            "\"\\\\server\\share\\translations\\runic.json\"",
            "C:\\work\\obj\\",
            "\"a\\\\\\\"b\"",
            "\"trailing\\\\\"",
            "plain\\\"quote",
            "\"two words\""));

        string[] expected =
        [
            @"\\server\share\translations\runic.json",
            @"C:\work\obj\",
            "a\\\"b",
            "trailing\\",
            "plain\"quote",
            "two words",
        ];
        AssertEx.SequenceEqual(expected, (await RunAsync(["@" + responseFile])).Words!);
    }

    [Test, DisplayName("response-files/tokens-follow-msvcrt-quoting")]
    public async ValueTask Quoting()
    {
        using var directory = new TemporaryDirectory();
        string responseFile = directory.Write("quoting.rsp",
            "one\ttwo   three\r\nfour\n\"\"\nab\"c d\"ef \"multi\nline\" \\\\\\\\\" spaced \"\n");
        AssertEx.SequenceEqual(["one", "two", "three", "four", "", "abc def", "multi\nline", @"\\ spaced "],
            (await RunAsync(["@" + responseFile])).Words!);
    }

    [Test, DisplayName("response-files/hash-starts-a-comment-only-at-the-start-of-a-line")]
    public async ValueTask Comments()
    {
        using var directory = new TemporaryDirectory();
        string responseFile = directory.Write("comments.rsp",
            "# leading comment \"with an open quote\n   \t# indented comment\r\nfirst #second\n\"#quoted\"\n\"line\n# inside quotes\"\nlast # trailing\n#");
        AssertEx.SequenceEqual(["first", "#second", "#quoted", "line\n# inside quotes", "last", "#", "trailing"],
            (await RunAsync(["@" + responseFile])).Words!);
    }

    [Test, DisplayName("response-files/strict-utf-8-with-an-optional-byte-order-mark")]
    public async ValueTask Utf8Encoding()
    {
        using var directory = new TemporaryDirectory();
        string withBom = directory.Write("bom.rsp", [0xEF, 0xBB, 0xBF, .. Utf8.GetBytes("grüße\n# Kommentar\n\"日本 語\"")]);
        AssertEx.SequenceEqual(["grüße", "日本 語"], (await RunAsync(["@" + withBom])).Words!);

        // A byte order mark is only skipped at the start: a second one is part of the token.
        string doubleBom = directory.Write("double-bom.rsp", [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, (byte)'x']);
        AssertEx.SequenceEqual(["\uFEFFx"], (await RunAsync(["@" + doubleBom])).Words!);

        foreach (byte[] invalid in new byte[][] { [(byte)'a', 0xFF, (byte)'b'], [0xC3], [0xED, 0xA0, 0x80], [0xC0, 0xAF] })
        {
            string file = directory.Write("invalid.rsp", invalid);
            Run run = await RunAsync(["--force", "@" + file]);
            AssertUsageError(run, "RCLI1018", "response-file-invalid-utf8", 1, "invalid.rsp");
        }
    }

    [Test, DisplayName("response-files/double-at-escapes-a-literal-at")]
    public async ValueTask Escapes()
    {
        using var directory = new TemporaryDirectory();
        string responseFile = directory.Write("escapes.rsp", "@@scope/package @ @@@twice @@");
        AssertEx.SequenceEqual(["@scope/package", "@", "@@twice", "@", "@alpha", "@"],
            (await RunAsync(["@" + responseFile, "@@alpha", "@"])).Words!);
    }

    [Test, DisplayName("response-files/nested-files-resolve-against-the-naming-file")]
    public async ValueTask NestedRelativePaths()
    {
        using var directory = new TemporaryDirectory();
        directory.Write(Path.Combine("nested", "deeper", "inner.rsp"), "inner");
        directory.Write(Path.Combine("nested", "middle.rsp"), "middle-before @deeper/inner.rsp middle-after");
        directory.Write(Path.Combine("nested", "space dir", "spaced.rsp"), "spaced");
        directory.Write(Path.Combine("nested", "quoted.rsp"), "\"@space dir/spaced.rsp\"");
        string outer = directory.Write("outer.rsp", "outer @nested/middle.rsp @nested/quoted.rsp @nested/middle.rsp");
        AssertEx.SequenceEqual(
            ["outer", "middle-before", "inner", "middle-after", "spaced", "middle-before", "inner", "middle-after", "after"],
            (await RunAsync(["@" + outer, "after"])).Words!);
    }

    // The only case that changes the process's current directory, so it runs alone.
    [Test, NotInParallel, DisplayName("response-files/command-line-paths-resolve-against-the-current-directory")]
    public async ValueTask CurrentDirectory()
    {
        using var directory = new TemporaryDirectory();
        directory.Write(Path.Combine("sub", "args.rsp"), "from-current-directory");
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = directory.Path;
        try
        {
            AssertEx.SequenceEqual(["from-current-directory"], (await RunAsync(["@sub/args.rsp"])).Words!);
            Run missing = await RunAsync(["@args.rsp"]);
            AssertUsageError(missing, "RCLI1017", "response-file-unreadable", 0, "args.rsp");
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Test, DisplayName("response-files/nest-at-most-sixteen-levels")]
    public async ValueTask NestingDepth()
    {
        using var directory = new TemporaryDirectory();
        // level-0 names level-1 and so on; the command line names level-0 at depth zero. Sixteen files expand.
        for (int level = 0; level <= 16; level++)
            directory.Write("level-" + level + ".rsp", "word-" + level + (level < 15 ? " @level-" + (level + 1) + ".rsp" : ""));
        AssertEx.SequenceEqual(Enumerable.Range(0, 16).Select(level => "word-" + level),
            (await RunAsync(["@" + Path.Combine(directory.Path, "level-0.rsp")])).Words!);

        // A seventeenth level is a usage error at the command-line argument that started the chain.
        directory.Write("level-15.rsp", "word-15 @level-16.rsp");
        Run tooDeep = await RunAsync(["first", "@" + Path.Combine(directory.Path, "level-0.rsp")]);
        AssertUsageError(tooDeep, "RCLI1020", "response-file-too-deep", 1, "level-16.rsp");
    }

    [Test, DisplayName("response-files/a-file-may-not-include-itself")]
    public async ValueTask Cycles()
    {
        using var directory = new TemporaryDirectory();
        string self = directory.Write("self.rsp", "before @self.rsp");
        AssertUsageError(await RunAsync(["@" + self]), "RCLI1021", "response-file-cycle", 0, "self.rsp");

        directory.Write("b.rsp", "b @./a.rsp");
        string a = directory.Write("a.rsp", "a @b.rsp");
        AssertUsageError(await RunAsync(["x", "y", "@" + a]), "RCLI1021", "response-file-cycle", 2, "./a.rsp");

        // The same file named twice, one after the other, is not a cycle.
        string leaf = directory.Write("leaf.rsp", "leaf");
        string twice = directory.Write("twice.rsp", "@leaf.rsp @leaf.rsp");
        AssertEx.SequenceEqual(["leaf", "leaf", "leaf"], (await RunAsync(["@" + twice, "@" + leaf])).Words!);
    }

    [Test, DisplayName("response-files/unreadable-files-are-usage-errors")]
    public async ValueTask Unreadable()
    {
        using var directory = new TemporaryDirectory();
        AssertUsageError(await RunAsync(["@missing.rsp"]), "RCLI1017", "response-file-unreadable", 0, "missing.rsp");
        AssertUsageError(await RunAsync(["--force", "@" + directory.Path]), "RCLI1017", "response-file-unreadable", 1, Path.GetFileName(directory.Path));
        directory.Write("names-missing.rsp", "ok @not-there.rsp");
        AssertUsageError(await RunAsync(["@" + Path.Combine(directory.Path, "names-missing.rsp")]), "RCLI1017", "response-file-unreadable", 0, "not-there.rsp");
        AssertUsageError(await RunAsync(["@bad\0name"]), "RCLI1017", "response-file-unreadable", 0, "<invalid>");
    }

    // SEC-41: expansion is bounded, so a hostile response file is a usage error rather than exhausted memory.
    [Test, DisplayName("response-files/expansion-is-bounded-in-size-and-arguments")]
    public async ValueTask ExpansionIsBounded()
    {
        using var directory = new TemporaryDirectory();
        // One file over 1 MiB.
        byte[] large = new byte[(1 << 20) + 1];
        Array.Fill(large, (byte)'a');
        string tooLarge = directory.Write("large.rsp", large);
        AssertUsageError(await RunAsync(["x", "@" + tooLarge]), "RCLI1017", "response-file-unreadable", 1, "large.rsp");

        // Files of at most 1 MiB each, but more than 4 MiB together.
        byte[] part = new byte[900 * 1024];
        Array.Fill(part, (byte)'b');
        for (int index = 0; index < 5; index++) directory.Write("part-" + index + ".rsp", part);
        string parts = directory.Write("parts.rsp", "@part-0.rsp @part-1.rsp @part-2.rsp @part-3.rsp @part-4.rsp");
        AssertUsageError(await RunAsync(["@" + parts]), "RCLI1017", "response-file-unreadable", 0, "part-4.rsp");

        // Files that name the next level four times each: 4^9 arguments without the bound.
        directory.Write("level-9.rsp", "leaf");
        for (int level = 8; level >= 0; level--)
            directory.Write("level-" + level + ".rsp", string.Join(' ', Enumerable.Repeat("@level-" + (level + 1) + ".rsp", 4)));
        Run fanOut = await RunAsync(["@" + System.IO.Path.Combine(directory.Path, "level-0.rsp")]);
        AssertEx.Equal(CommandExitCodes.Usage, fanOut.ExitCode, fanOut.StandardError);
        AssertEx.True(fanOut.StandardError.Contains("RCLI1017", StringComparison.Ordinal), fanOut.StandardError);

        // Below the bounds, the same shape expands: 4^4 = 256 arguments.
        directory.Write("small-4.rsp", "leaf");
        for (int level = 3; level >= 0; level--)
            directory.Write("small-" + level + ".rsp", string.Join(' ', Enumerable.Repeat("@small-" + (level + 1) + ".rsp", 4)));
        AssertEx.Equal(256, (await RunAsync(["@" + System.IO.Path.Combine(directory.Path, "small-0.rsp")])).Words!.Length);
    }

    // SEC-41: a device or FIFO is not a response file. /dev/zero would be read without end, and opening a FIFO
    // would block until a writer appears.
    [Test, DisplayName("response-files/devices-and-fifos-are-unreadable")]
    public async ValueTask DevicesAndFifosAreUnreadable()
    {
        if (OperatingSystem.IsWindows())
        {
            AssertUsageError(await RunAsync(["@CON"]).AsTask().WaitAsync(TimeSpan.FromSeconds(30)), "RCLI1017", "response-file-unreadable", 0, "CON");
            return;
        }

        AssertUsageError(await RunAsync(["@/dev/zero"]).AsTask().WaitAsync(TimeSpan.FromSeconds(30)), "RCLI1017", "response-file-unreadable", 0, "/dev/zero");
        using var directory = new TemporaryDirectory();
        string fifo = System.IO.Path.Combine(directory.Path, "pipe.rsp");
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [fifo]))
        {
            await mkfifo.WaitForExitAsync();
            AssertEx.Equal(0, mkfifo.ExitCode);
        }

        AssertUsageError(await RunAsync(["@" + fifo]).AsTask().WaitAsync(TimeSpan.FromSeconds(30)), "RCLI1017", "response-file-unreadable", 0, "pipe.rsp");
    }

    [Test, DisplayName("response-files/an-unterminated-quote-is-a-usage-error")]
    public async ValueTask UnterminatedQuote()
    {
        using var directory = new TemporaryDirectory();
        string file = directory.Write("open.rsp", "complete \"open quote\nnext line");
        AssertUsageError(await RunAsync(["@" + file]), "RCLI1019", "response-file-unterminated-quote", 0, "open.rsp");
        string escaped = directory.Write("escaped.rsp", "a\\\"b");
        AssertEx.SequenceEqual(["a\"b"], (await RunAsync(["@" + escaped])).Words!);
    }

    [Test, DisplayName("response-files/errors-follow-the-selected-output-format")]
    public async ValueTask ErrorOutput()
    {
        using var directory = new TemporaryDirectory();
        Run human = await RunAsync(["@missing.rsp"]);
        AssertEx.Equal("", human.StandardOutput);
        AssertEx.True(human.StandardError.Contains("The response file 'missing.rsp' could not be read.", StringComparison.Ordinal), human.StandardError);

        Run json = await RunAsync(["--format", "json", "@missing.rsp"]);
        AssertEx.Equal(CommandExitCodes.Usage, json.ExitCode);
        using (JsonDocument document = CommandTestEnvelope.Parse(json.StandardOutput))
        {
            JsonElement diagnostic = document.RootElement.GetProperty("diagnostics")[0];
            AssertEx.Equal("RCLI1017", diagnostic.GetProperty("code").GetString());
            AssertEx.Equal("response-file-unreadable", diagnostic.GetProperty("kind").GetString());
            AssertEx.Equal(2, diagnostic.GetProperty("tokenIndex").GetInt32());
            AssertEx.Equal("missing.rsp", diagnostic.GetProperty("arguments")[0].GetString());
            AssertEx.Equal("diagnostics.response-file-unreadable", diagnostic.GetProperty("messageKey").GetString());
        }

        // An invalid output selection on the command line is reported first, as the parser reports it.
        Run invalidFormat = await RunAsync(["--format", "yaml", "@missing.rsp"]);
        AssertEx.Equal(CommandExitCodes.Usage, invalidFormat.ExitCode);
        AssertEx.True(invalidFormat.StandardError.Contains("RCLI1010", StringComparison.Ordinal) || invalidFormat.StandardError.Contains("must be human or json", StringComparison.Ordinal), invalidFormat.StandardError);

        // A response file may select the output format itself.
        string selects = directory.Write("json.rsp", "--format json word");
        Run fromFile = await RunAsync(["@" + selects]);
        AssertEx.Equal(0, fromFile.ExitCode);
        AssertEx.SequenceEqual(["word"], fromFile.Words!);
        using (JsonDocument document = CommandTestEnvelope.Parse(fromFile.StandardOutput))
            AssertEx.True(document.RootElement.GetProperty("success").GetBoolean(), fromFile.StandardOutput);
    }

    [Test, DisplayName("response-files/a-file-may-hold-commands-options-and-help")]
    public async ValueTask WholeCommandLines()
    {
        using var directory = new TemporaryDirectory();
        string options = directory.Write("options.rsp", "run --force -- --not-an-option");
        Run run = await RunAsync(["@" + options]);
        AssertEx.Equal(0, run.ExitCode);
        AssertEx.SequenceEqual(["--not-an-option"], run.Words!);

        // A response file named after -- is still expanded, because expansion precedes parsing; @@ keeps it literal.
        string words = directory.Write("words.rsp", "from-file");
        AssertEx.SequenceEqual(["from-file", "@kept"], (await RunAsync(["--", "@" + words, "@@kept"])).Words!);

        string help = directory.Write("help.rsp", "--help");
        Run helpRun = await RunAsync(["@" + help]);
        AssertEx.Equal(0, helpRun.ExitCode);
        AssertEx.True(helpRun.Words is null && helpRun.StandardOutput.StartsWith("Usage: fixture", StringComparison.Ordinal), helpRun.StandardOutput);

        // An empty response file leaves no arguments: the default command runs without words.
        string empty = directory.Write("empty.rsp", "# nothing\n\n");
        AssertEx.SequenceEqual([], (await RunAsync(["@" + empty])).Words!);

        // A completion request may come from a response file too.
        string completion = directory.Write("completion.rsp", "completion bash");
        Run script = await RunAsync(["@" + completion]);
        AssertEx.True(script.Words is null && script.StandardOutput.Contains("complete -F _runic_fixture", StringComparison.Ordinal), script.StandardOutput);
    }

    [Test, DisplayName("response-files/off-by-default-so-at-is-an-ordinary-argument")]
    public async ValueTask OffByDefault()
    {
        using var directory = new TemporaryDirectory();
        string file = directory.Write("args.rsp", "expanded");
        CommandCatalog catalog = Catalog(responseFiles: false, _ => { });
        AssertEx.True(!catalog.ResponseFiles);
        Run run = await RunAsync(["@" + file, "@@double", "@", "@missing.rsp"], responseFiles: false);
        AssertEx.Equal(0, run.ExitCode);
        AssertEx.SequenceEqual(["@" + file, "@@double", "@", "@missing.rsp"], run.Words!);

        string help = CommandHelpFormatter.Format(catalog, "fixture", CommandPath.Root);
        AssertEx.True(!help.Contains("@<file>", StringComparison.Ordinal), help);
        CommandCompletionResult query = CommandCompletion.Query(catalog, ["@ar"]);
        AssertEx.Equal(CommandPathKind.None, query.PathKind);
        AssertEx.Equal("", query.ValuePrefix);
        foreach (string shell in new[] { "bash", "zsh", "fish", "powershell" })
            AssertEx.True(!CommandCompletion.Generate(catalog, "fixture", shell).Contains("@@*", StringComparison.Ordinal), shell);
    }

    [Test, DisplayName("response-files/help-lists-the-at-file-syntax")]
    public async ValueTask Help()
    {
        CommandCatalog catalog = Catalog(responseFiles: true, _ => { });
        AssertEx.True(catalog.ResponseFiles);
        foreach (CommandPath path in new[] { CommandPath.Root, new CommandPath(["run"]) })
        {
            string help = CommandHelpFormatter.Format(catalog, "fixture", path);
            AssertEx.True(help.Contains("  @<file>", StringComparison.Ordinal) && help.Contains("Read more arguments from a file", StringComparison.Ordinal), help);
        }

        var resolver = new KeyResolver(CommandTextKeys.HelpResponseFiles, "Weitere Argumente aus einer Datei lesen");
        string localized = CommandHelpFormatter.Format(catalog, "fixture", CommandPath.Root, new CommandTextContext(CultureInfo.GetCultureInfo("de"), resolver));
        AssertEx.True(localized.Contains("Weitere Argumente aus einer Datei lesen", StringComparison.Ordinal), localized);

        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(catalog, console).RunAsync(["--help"]));
        AssertEx.True(console.StandardOutput.Contains("@<file>", StringComparison.Ordinal), console.StandardOutput);
    }

    [Test, DisplayName("response-files/diagnostics-are-translatable")]
    public async ValueTask LocalizedDiagnostic()
    {
        var resolver = new KeyResolver(CommandTextKeys.DiagnosticResponseFileUnreadable, "Die Antwortdatei {0} ist nicht lesbar.");
        var console = new TestCommandConsole();
        CommandApp app = new(Catalog(responseFiles: true, _ => { }))
        {
            Name = "fixture", Console = console, ParseSettings = ParseSettings.Default, HandleTerminationSignals = false, TextResolver = resolver,
        };
        AssertEx.Equal(CommandExitCodes.Usage, await app.RunAsync(["@missing.rsp"]));
        AssertEx.True(console.StandardError.Contains("Die Antwortdatei missing.rsp ist nicht lesbar.", StringComparison.Ordinal), console.StandardError);
        foreach (string key in new[]
        {
            CommandTextKeys.HelpResponseFiles, CommandTextKeys.DiagnosticResponseFileUnreadable, CommandTextKeys.DiagnosticResponseFileInvalidUtf8,
            CommandTextKeys.DiagnosticResponseFileUnterminatedQuote, CommandTextKeys.DiagnosticResponseFileTooDeep, CommandTextKeys.DiagnosticResponseFileCycle,
        })
            AssertEx.True(CommandTextKeys.Find(key) is not null, key);
        AssertEx.SequenceEqual(["file"], CommandTextKeys.Find(CommandTextKeys.DiagnosticResponseFileCycle)!.Arguments);
    }

    [Test, DisplayName("response-files/completion-query-completes-a-file-after-at")]
    public ValueTask CompletionQuery()
    {
        CommandCatalog catalog = Catalog(responseFiles: true, _ => { });
        foreach (string[] words in new string[][] { ["@"], ["@ar"], ["run", "--force", "@sub/"], ["--", "@x"] })
        {
            CommandCompletionResult result = CommandCompletion.Query(catalog, words);
            AssertEx.Equal(CommandPathKind.File, result.PathKind, string.Join(' ', words));
            AssertEx.Equal("@", result.ValuePrefix);
            AssertEx.SequenceEqual([], result.Candidates);
        }

        CommandCompletionResult escaped = CommandCompletion.Query(catalog, ["@@x"]);
        AssertEx.Equal(CommandPathKind.None, escaped.PathKind);
        AssertEx.SequenceEqual(["--force", "--format"], CommandCompletion.Query(catalog, ["@args.rsp", "--f"]).Candidates);
        return ValueTask.CompletedTask;
    }

    [Test, NotInParallel, DisplayName("response-files/native-shells-complete-a-file-after-at")]
    public async ValueTask NativeCompletion()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("space file.rsp", "x");
        Directory.CreateDirectory(Path.Combine(directory.Path, "space directory"));
        CommandCatalog catalog = Catalog(responseFiles: true, _ => { });
        foreach (string shell in new[] { "bash", "zsh", "fish", "pwsh" })
        {
            string script = Path.Combine(directory.Path, "completion." + (shell == "pwsh" ? "ps1" : shell));
            await File.WriteAllTextAsync(script, CommandCompletion.Generate(catalog, "fixture", shell));
            string command = shell switch
            {
                "bash" => $"source {CompletionHelpTests.Sh(script)}; COMP_WORDS=(fixture run @spa); COMP_CWORD=2; _runic_fixture; printf '%s\\n' \"${{COMPREPLY[@]}}\"",
                "zsh" => $"compdef() {{ :; }}; compadd() {{ :; }}; compset() {{ [[ \"$1 $2\" == '-P @' ]] || return 98; IPREFIX=\"$IPREFIX@\"; PREFIX=\"${{PREFIX#@}}\"; }}; _files() {{ [[ \"$IPREFIX\" == @ ]] || return 99; print -rl -- \"$PREFIX\"*; }}; source {CompletionHelpTests.Sh(script)}; words=(fixture run @spa); CURRENT=3; PREFIX=@spa; IPREFIX=''; _runic_fixture",
                "fish" => $"source {CompletionHelpTests.Fish(script)}; complete -C {CompletionHelpTests.Fish("fixture run @spa")}",
                _ => $". {CompletionHelpTests.Ps(script)}; (TabExpansion2 {CompletionHelpTests.Ps("fixture run '@spa")} {"fixture run '@spa".Length}).CompletionMatches | ForEach-Object {{ $_.CompletionText }}",
            };
            string[]? values = await CompletionHelpTests.RunShell(shell, command, directory.Path);
            if (values is null) continue;
            string[] normalized = values.Select(value => value.Replace("\\ ", " ", StringComparison.Ordinal).TrimEnd('/', '\\')).ToArray();
            string expectedFile = shell switch
            {
                "zsh" => "space file.rsp",
                "pwsh" => "'@" + Path.Combine(".", "space file.rsp") + "'",
                _ => "@space file.rsp",
            };
            AssertEx.True(normalized.Contains(expectedFile), shell + ": " + string.Join(" | ", values));
        }
    }

    [Test, DisplayName("response-files/hosting-adapter-expands-them")]
    public async ValueTask Hosting()
    {
        using var directory = new TemporaryDirectory();
        string file = directory.Write("args.rsp", "run hosted");
        string[]? received = null;
        var console = new TestCommandConsole();
        var adapter = new CommandLineHostingAdapter(App(Catalog(responseFiles: true, words => received = words), console));
        HostedCommandLineDecision decision = adapter.Classify(new HostedCommandLineLaunchInput(["@" + file]));
        AssertEx.Equal(HostedCommandLineDecisionKind.Invocation, decision.Kind);
        AssertEx.Equal("run", decision.CommandName);
        AssertEx.SequenceEqual(["@" + file], decision.Arguments);
        AssertEx.Equal(0, await adapter.RunAsync(decision));
        AssertEx.SequenceEqual(["hosted"], received!);

        decision = adapter.Classify(new HostedCommandLineLaunchInput(["@missing.rsp"]));
        AssertEx.Equal(HostedCommandLineDecisionKind.Invalid, decision.Kind);
        AssertEx.True(decision.IsCommandLineRequest && decision.SuggestedCommand is null);
        AssertEx.Equal("RCLI1017", decision.Diagnostics.Single().Code);
        AssertEx.Equal(CommandExitCodes.Usage, await adapter.RunAsync(decision));
        AssertEx.True(console.StandardError.Contains("missing.rsp", StringComparison.Ordinal), console.StandardError);

        string completion = directory.Write("completion.rsp", "completion fish");
        console = new TestCommandConsole();
        decision = adapter.Classify(new HostedCommandLineLaunchInput(["@" + completion]));
        AssertEx.Equal(HostedCommandLineDecisionKind.Completion, decision.Kind);
        AssertEx.Equal(0, await adapter.RunAsync(decision, console));
        AssertEx.True(console.StandardOutput.Contains("complete -c 'fixture'", StringComparison.Ordinal), console.StandardOutput);

        // Without the declaration, a launch argument such as a document name stays as given.
        var plain = new CommandLineHostingAdapter(App(Catalog(responseFiles: false, words => received = words), new TestCommandConsole()));
        decision = plain.Classify(new HostedCommandLineLaunchInput(["@missing.rsp"]));
        AssertEx.Equal(HostedCommandLineDecisionKind.Invocation, decision.Kind);
    }

    [Test, DisplayName("response-files/combined-catalogs-read-them-when-one-part-does")]
    public ValueTask Combine()
    {
        var with = new CommandCatalogBuilder().ResponseFiles();
        CatalogTests.ValidCommand(with, "first");
        var without = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(without, "second");
        CommandCatalog withCatalog = with.Build(), withoutCatalog = without.Build();
        AssertEx.True(CommandCatalog.Combine(withoutCatalog, withCatalog).ResponseFiles);
        AssertEx.True(CommandCatalog.Combine(withCatalog, withoutCatalog).ResponseFiles);
        AssertEx.True(!CommandCatalog.Combine(withoutCatalog).ResponseFiles);
        AssertEx.True(new CommandCatalogBuilder().ResponseFiles().ResponseFiles().Build().ResponseFiles);
        return ValueTask.CompletedTask;
    }

    // A reference that names a home or temporary directory is reported by its file name, which output does not redact.
    private static void AssertUsageError(Run run, string code, string kind, int tokenIndex, string argument)
    {
        AssertEx.Equal(CommandExitCodes.Usage, run.ExitCode, run.StandardError);
        AssertEx.True(run.Words is null, "The command ran.");
        Run json = run.Json!;
        using JsonDocument document = CommandTestEnvelope.Parse(json.StandardOutput);
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostics").EnumerateArray().Single();
        AssertEx.Equal(code, diagnostic.GetProperty("code").GetString(), json.StandardOutput);
        AssertEx.Equal(kind, diagnostic.GetProperty("kind").GetString());
        AssertEx.Equal(tokenIndex, diagnostic.GetProperty("tokenIndex").GetInt32());
        // Elsewhere, such as under /var/folders on macOS, the path is reported as given.
        string actual = diagnostic.GetProperty("arguments")[0].GetString()!;
        AssertEx.True(actual == argument || actual.EndsWith(Path.DirectorySeparatorChar + argument, StringComparison.Ordinal), json.StandardOutput);
        AssertEx.True(!diagnostic.GetProperty("message").GetString()!.Contains("redacted", StringComparison.Ordinal), json.StandardOutput);
    }

    private sealed record Run(int ExitCode, string StandardOutput, string StandardError, string[]? Words)
    {
        internal Run? Json { get; init; }
    }

    // Runs the arguments in human mode, and again with RUNIC_COMMANDLINE_OUTPUT=json for the diagnostics.
    private static async ValueTask<Run> RunAsync(string[] args, bool responseFiles = true)
    {
        Run human = await RunOnceAsync(args, responseFiles, ParseSettings.Default);
        Run json = await RunOnceAsync(args, responseFiles, new ParseSettings("json"));
        return human with { Json = json };
    }

    private static async ValueTask<Run> RunOnceAsync(string[] args, bool responseFiles, ParseSettings settings)
    {
        string[]? received = null;
        var console = new TestCommandConsole();
        int exitCode = await new CommandApp(Catalog(responseFiles, words => received = words))
        {
            Name = "fixture", Console = console, ParseSettings = settings, HandleTerminationSignals = false,
        }.RunAsync(args);
        return new Run(exitCode, console.StandardOutput, console.StandardError, received);
    }

    private static CommandApp App(CommandCatalog catalog, ICommandConsole console) =>
        new(catalog) { Name = "fixture", Console = console, ParseSettings = ParseSettings.Default, HandleTerminationSignals = false };

    private static CommandCatalog Catalog(bool responseFiles, Action<string[]> received)
    {
        var builder = new CommandCatalogBuilder();
        if (responseFiles) builder.ResponseFiles();
        builder.Command<TestOptions, TestHandler, TestResult>("run", command => command
            .Argument("words", "words", CommandArity.ZeroOrMore)
            .Option("force", "--force", CommandArity.Zero)
            .BindWith(new TestBinder((invocation, _) =>
            {
                received(invocation.Arguments.SelectMany(argument => argument.Values).ToArray());
                return ValueTask.FromResult(CommandOutcome.Success(new TestOptions()));
            }))
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new TestCodec()));
        return builder.DefaultCommand("run").Build();
    }

    private sealed class KeyResolver(string key, string text) : ICommandTextResolver
    {
        public string? Resolve(string requested, CultureInfo culture, IReadOnlyList<string> arguments) =>
            requested == key ? string.Format(CultureInfo.InvariantCulture, text, [.. arguments]) : null;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "runic-response-files-" + Guid.NewGuid().ToString("N"));

        internal TemporaryDirectory() => Directory.CreateDirectory(Path);

        internal string Write(string relativePath, string content) => Write(relativePath, Utf8.GetBytes(content));

        internal string Write(string relativePath, byte[] content)
        {
            string path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
