using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Processes;
using Runic.CommandLine.Testing;
using Runic.Translations;
using Runic.Translations.CommandLine.Translations;
using TUnit.Core;

namespace Runic.CommandLine.TextCatalog.Tests;

// The runic-command-line catalog sources (packages/dotnet/Runic.CommandLine/translations) must translate every framework
// key in English and German, with the key's arguments, and nothing else. A pull request that adds or changes a key in
// CommandTextKeys.All adds its en and de messages in the same change; see docs/guides/command-line/text-keys.md.
internal sealed partial class TextCatalogTests
{
    private static readonly string[] Locales = ["en", "de"];

    // Labels that German writes like English.
    private static readonly string[] SameInGerman = ["help.minimum", "help.maximum"];

    // Invocations that reach help, usage errors, value errors, prompts and faults.
    private static readonly string[][] Invocations =
    [
        ["--help"], ["greet", "--help"], ["pick", "--help"], ["tag", "--help"], ["ask", "--help"],
        ["greet"], ["greet", "Ada", "--bogus"], ["greet", "Ada", "--tmies", "2"], ["greet", "Ada", "--times"],
        ["greet", "Ada", "--times", "9"], ["greet", "Ada", "--times", "0"], ["greet", "Ada", "--times", "x"],
        ["greet", "Ada", "--times", "1", "--times", "2"], ["greet", "Ada", "Bob"], ["greet", "Ada", "-xy"],
        ["frobnicate"], ["gret", "Ada"], ["--help", "extra"], ["greet", "Ada", "--json", "--format", "json"], ["--format", "yaml"],
        ["ask"], ["ask", "--json"], ["fail"], ["tag"], ["tag", "--json"],
        ["pick", "--mode", "medium"], ["pick", "--source", "missing-file.txt"], ["pick", "--target", "missing-directory"],
        ["pick", "--id", "x"], ["pick", "--ratio", "x"], ["pick", "--apply"], ["pick", "--apply", "--confirm", "--dry-run"],
        ["pick", "--confirm=maybe"], ["completion", "tcsh"], ["--format", "human"], ["--format", "json"], ["--json"],
        ["linked"], ["leak"], ["leak", "--json"],
        .. ProcessFaultMessages.ByCode.Keys.Select(static code => new[] { "process", code }),
    ];

    [Test, DisplayName("text-keys/json-lists-every-command-text-key-with-its-arguments")]
    public void TextKeysJson()
    {
        TextKeysFile file = TextKeysFile.Load();
        Assert.Equal(1, file.Root.GetProperty("schemaVersion").GetInt32(), "schemaVersion");
        Assert.Equal("Runic.CommandLine", file.Root.GetProperty("package").GetString(), "package");
        Assert.Equal(typeof(CommandTextKeys).Assembly.GetName().Version!.ToString(3),
            file.Root.GetProperty("version").GetString()!.Split('-', '+')[0], "version");
        Assert.Equal("runic.json", file.Root.GetProperty("catalogProject").GetString(), "catalogProject");
        using (JsonDocument project = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "catalog", "runic.json"))))
            Assert.Equal(project.RootElement.GetProperty("catalog").GetString(), file.Root.GetProperty("catalog").GetString(), "catalog");

        Assert.Equal(string.Join("\n", CommandTextKeys.All.Select(static key => key.Key + "(" + string.Join(",", key.Arguments) + ")")),
            string.Join("\n", file.Keys.Select(static key => key.Key + "(" + string.Join(",", key.Arguments) + ")")),
            "text-keys.json against CommandTextKeys.All (rebuild Runic.CommandLine)");
        foreach (TextKeyEntry entry in file.Keys.Where(static entry => entry.Message is not null))
            Assert.Equal(MessageName(entry.Key), entry.Message, entry.Key + " message name");
        // Only RCLI4001 leaves its text to the framework (its diagnostics.prompt-* message).
        Assert.Equal("faults.RCLI4001", string.Join(",", file.Keys.Where(static entry => entry.Message is null).Select(static entry => entry.Key)),
            "keys without a catalog message");
    }

    [Test, DisplayName("catalog/every-key-has-en-and-de-and-no-message-is-orphaned")]
    public void Coverage()
    {
        IReadOnlyDictionary<string, TextKeyEntry> expected = TextKeysFile.Load().ByMessage;
        var problems = new List<string>();
        foreach (string locale in Locales)
        {
            IReadOnlyDictionary<string, SourceMessage> messages = CatalogSource.Load(locale);
            foreach ((string name, TextKeyEntry entry) in expected)
                if (!messages.ContainsKey(name)) problems.Add($"{locale}.rmf2 has no message {name} for the key {entry.Key}.");
            foreach (SourceMessage message in messages.Values)
                if (!expected.ContainsKey(message.Name))
                    problems.Add($"{locale}.rmf2:{message.Line} has the message {message.Name}, which no key of CommandTextKeys.All names" +
                        " (a removed or renamed key, or a key without a catalog message).");
        }

        // The compiled catalog holds the same messages.
        string[] compiled = CommandLineTextCatalogData.CreateDefinition().Definitions.ToArray().Select(static item => item.Name).ToArray();
        string[] wanted = expected.Keys.Select(static name => name.Replace('.', '_')).ToArray();
        foreach (string name in wanted.Except(compiled, StringComparer.Ordinal)) problems.Add("The compiled catalog has no message " + name + ".");
        foreach (string name in compiled.Except(wanted, StringComparer.Ordinal)) problems.Add("The compiled catalog has the orphan message " + name + ".");
        Assert.Empty(problems, "The runic-command-line catalog does not match CommandTextKeys.All");
    }

    [Test, DisplayName("catalog/message-inputs-are-the-key-arguments")]
    public void Inputs()
    {
        IReadOnlyDictionary<string, TextKeyEntry> keys = TextKeysFile.Load().ByMessage;
        Dictionary<string, IReadOnlyDictionary<string, SourceMessage>> sources = Locales.ToDictionary(static locale => locale, CatalogSource.Load);
        var problems = new List<string>();
        foreach ((string locale, IReadOnlyDictionary<string, SourceMessage> messages) in sources)
            foreach (SourceMessage message in messages.Values)
            {
                if (!keys.TryGetValue(message.Name, out TextKeyEntry? entry)) continue;
                string arguments = entry.Arguments.Count == 0 ? "none" : string.Join(", ", entry.Arguments);
                foreach (string input in message.Inputs.Where(input => !entry.Arguments.Contains(input)))
                    problems.Add($"{locale}.rmf2:{message.Line} {message.Name} declares {{${input}}}, but {entry.Key} has the arguments: {arguments}.");
                foreach (string variable in message.Variables.Where(variable => !message.Inputs.Contains(variable)))
                    problems.Add($"{locale}.rmf2:{message.Line} {message.Name} uses {{${variable}}} without '.input {{${variable} :string}}'.");
                if (message.Inputs.Distinct(StringComparer.Ordinal).Count() != message.Inputs.Count)
                    problems.Add($"{locale}.rmf2:{message.Line} {message.Name} declares an input twice.");
            }

        // A translation shows the same values as the English text.
        foreach (SourceMessage english in sources["en"].Values)
            if (sources["de"].TryGetValue(english.Name, out SourceMessage? german) &&
                !english.Variables.Order(StringComparer.Ordinal).SequenceEqual(german.Variables.Order(StringComparer.Ordinal)))
                problems.Add($"de.rmf2:{german.Line} {german.Name} uses {{{string.Join(", ", german.Variables)}}}; en.rmf2 uses {{{string.Join(", ", english.Variables)}}}.");

        foreach (CompiledTranslationDefinition definition in CommandLineTextCatalogData.CreateDefinition().Definitions.ToArray())
        {
            TextKeyEntry? entry = keys.Values.FirstOrDefault(item => item.Message!.Replace('.', '_') == definition.Name);
            if (entry is null) continue;
            foreach (TranslationPlaceholderDescriptor input in definition.Placeholders.ToArray())
            {
                if (!entry.Arguments.Contains(input.Name)) problems.Add($"The compiled {entry.Message} takes {{${input.Name}}}, which {entry.Key} does not supply.");
                if (input.Type != TextArgumentType.String) problems.Add($"The compiled {entry.Message} takes {{${input.Name}}} as {input.Type}; framework arguments are strings.");
            }
        }

        Assert.Empty(problems, "Catalog message inputs do not match the key arguments");
    }

    [Test, DisplayName("catalog/german-text-is-translated-and-follows-the-conventions")]
    public void GermanText()
    {
        IReadOnlyDictionary<string, SourceMessage> english = CatalogSource.Load("en");
        IReadOnlyDictionary<string, SourceMessage> german = CatalogSource.Load("de");
        IReadOnlyDictionary<string, TextKeyEntry> keys = TextKeysFile.Load().ByMessage;
        var problems = new List<string>();
        foreach (SourceMessage message in german.Values)
        {
            string key = keys.TryGetValue(message.Name, out TextKeyEntry? entry) ? entry.Key : message.Name;
            if (message.Text.Trim().Length == 0) problems.Add($"de.rmf2:{message.Line} {message.Name} is empty.");
            if (english.TryGetValue(message.Name, out SourceMessage? source) && source.Text == message.Text && !SameInGerman.Contains(key))
                problems.Add($"de.rmf2:{message.Line} {message.Name} is the English text.");
            // Terminal text uses ASCII quotes, which every console shows and copies.
            if (message.Text.IndexOfAny(['„', '“', '”', '‚', '‘', '’', '«', '»']) >= 0)
                problems.Add($"de.rmf2:{message.Line} {message.Name} uses typographic quotes; use ' or \".");
            // The command line says Verzeichnis; Ordner is the GUI term.
            if (message.Text.Contains("Ordner", StringComparison.Ordinal)) problems.Add($"de.rmf2:{message.Line} {message.Name} says Ordner; use Verzeichnis.");
            // Formal address: Sie, never du.
            if (Informal().IsMatch(message.Text)) problems.Add($"de.rmf2:{message.Line} {message.Name} uses the informal du; use Sie.");
        }

        Assert.Empty(problems, "German catalog text");
    }

    [Test, DisplayName("catalog/every-process-fault-has-a-key-and-its-english-text")]
    public void ProcessFaults()
    {
        string[] codes = typeof(ProcessFaultCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral).Select(static field => (string)field.GetRawConstantValue()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(11, codes.Length, "ProcessFaultCodes");
        Assert.Equal(string.Join(",", codes.Select(CommandTextKeys.Fault)), string.Join(",", ProcessTextKeys.All.Select(static key => key.Key)),
            "ProcessTextKeys.All against ProcessFaultCodes");
        string[] constants = typeof(ProcessTextKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral).Select(static field => (string)field.GetRawConstantValue()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(string.Join(",", codes.Select(CommandTextKeys.Fault)), string.Join(",", constants), "ProcessTextKeys constants");
        Assert.Equal(string.Join(",", codes), string.Join(",", ProcessFaultMessages.ByCode.Keys), "ProcessFaultMessages");

        IReadOnlyDictionary<string, SourceMessage> english = CatalogSource.Load("en");
        var problems = new List<string>();
        foreach (CommandTextKey key in ProcessTextKeys.All)
        {
            // The same key object as CommandTextKeys.All, so the adapter needs only Runic.CommandLine to translate it.
            if (!ReferenceEquals(key, CommandTextKeys.Find(key.Key))) problems.Add(key.Key + " is not the CommandTextKeys.All entry.");
            if (key.Arguments.Count != 0) problems.Add(key.Key + " has arguments; fault keys have none.");
            string code = key.Key["faults.".Length..];
            if (!english.TryGetValue(MessageName(key.Key), out SourceMessage? message)) problems.Add("en.rmf2 has no message for " + key.Key + ".");
            else if (message.Text != ProcessFaultMessages.ByCode[code])
                problems.Add($"en.rmf2:{message.Line} {message.Name} is '{message.Text}'; the fault message is '{ProcessFaultMessages.ByCode[code]}'.");
        }

        Assert.Empty(problems, "Process fault keys");
    }

    [Test, DisplayName("catalog/english-matches-the-framework-text")]
    public async Task EnglishMatchesFramework()
    {
        CatalogTextResolver english = await CatalogTextResolver.CreateAsync("en", TextKeysFile.Load());
        var problems = new List<string>();
        foreach (string[] invocation in Invocations)
        {
            Result framework = await Run(null, "en", invocation);
            Result catalog = await Run(english, "en", invocation);
            string name = string.Join(' ', invocation);
            if (framework.Exit != catalog.Exit) problems.Add($"{name}: exit {catalog.Exit}, the framework's {framework.Exit}");
            if (framework.Output != catalog.Output) problems.Add($"{name}: standard output\n{catalog.Output}\nthe framework writes\n{framework.Output}");
            if (framework.Error != catalog.Error) problems.Add($"{name}: standard error\n{catalog.Error}\nthe framework writes\n{framework.Error}");
        }

        Assert.Empty(problems, "en.rmf2 differs from the English that Runic.CommandLine writes");
    }

    [Test, DisplayName("catalog/german-output-has-no-english-framework-text")]
    public async Task GermanHasNoEnglish()
    {
        // Every English catalog sentence and label of more than one word, cut at its arguments.
        string[] fragments = CatalogSource.Load("en").Values
            .SelectMany(static message => Variable().Split(message.Text))
            .Select(static fragment => fragment.Trim(' ', '.', '(', ')', '\'', '"', '?', ':', ';', ','))
            .Where(static fragment => fragment.Contains(' ', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(fragments.Length > 40, "Too few English fragments: " + fragments.Length);
        CatalogTextResolver german = await CatalogTextResolver.CreateAsync("de", TextKeysFile.Load());
        var problems = new List<string>();
        foreach (string[] invocation in Invocations)
        {
            Result result = await Run(german, "de", invocation);
            string text = result.Output + result.Error;
            Assert.True(text.Length > 0, string.Join(' ', invocation) + " wrote nothing.");
            foreach (string fragment in fragments.Where(fragment => text.Contains(fragment, StringComparison.Ordinal)))
                problems.Add($"{string.Join(' ', invocation)} shows '{fragment}':\n{text}");
        }

        Assert.Empty(problems, "German output contains English framework text");
        Result unknown = await Run(german, "de", "greet", "Ada", "--bogus");
        Assert.Equal("Die Option '--bogus' ist unbekannt.\nAufruf: greeter greet <name> [Optionen]\nWeitere Informationen erhalten Sie mit \"greeter greet --help\".\n",
            unknown.Error, "German usage error");
    }

    private static async Task<Result> Run(ICommandTextResolver? resolver, string culture, params string[] args)
    {
        var console = new TestCommandConsole();
        int exit = await new CommandApp(GeneratedCommandCatalog.Create(static builder => builder.JsonOutputAlias("--json")))
        {
            Name = "greeter",
            Console = console,
            HandleTerminationSignals = false,
            Culture = CultureInfo.GetCultureInfo(culture),
            TextResolver = resolver,
        }.RunAsync(args);
        // JSON frames carry a fresh request id on every run.
        return new Result(exit, RequestId().Replace(console.StandardOutput, "\"requestId\":\"*\""), console.StandardError);
    }

    // help.show-help is help.showHelp: each hyphen is removed and the letter after it capitalized.
    private static string MessageName(string key) => Hyphen().Replace(key, static match => match.Groups[1].Value.ToUpperInvariant());

    [GeneratedRegex("-([^.])")]
    private static partial Regex Hyphen();

    [GeneratedRegex(@"\{\$[A-Za-z0-9]+\}")]
    private static partial Regex Variable();

    [GeneratedRegex(@"\b(du|dich|dir|dein|deine|deinen|deinem|deiner|deines)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Informal();

    [GeneratedRegex("\"requestId\":\"[0-9a-f]+\"")]
    private static partial Regex RequestId();

    private readonly record struct Result(int Exit, string Output, string Error);
}
