using System.Globalization;
using LocalizedCli.Translations;
using Runic.CommandLine;
using Runic.CommandLine.Generated;
using Runic.Translations;

CultureInfo culture = CultureInfo.GetCultureInfo(Environment.GetEnvironmentVariable("RCLI_EXAMPLE_CULTURE") ?? "en");
var provider = CliTextCatalog.CreateProvider();
ITranslationSnapshot snapshot = await provider.GetSnapshotAsync(culture.Name);
return await new CommandApp(GeneratedCommandCatalog.Create())
{
    Name = "localized", Culture = culture, TextResolver = new SnapshotTextResolver(snapshot),
    ScopeFactory = CommandScopes.FromServices(CommandServices.Empty.With(snapshot)),
}.RunAsync(args);

internal static class Commands
{
    [Command("greet", Description = "Greet a person", DescriptionKey = "commands.greet")]
    public static string Greet(
        [Argument(Description = "Name of the person to greet", DescriptionKey = "arguments.name")] string name,
        [FromServices] ITranslationSnapshot snapshot) => snapshot.Format(CliTextKeys.r_617070_r_6772656574696e67, [new TextArgument("name", name)]);
}

internal sealed class SnapshotTextResolver(ITranslationSnapshot snapshot) : ICommandTextResolver
{
    // Explicit generated keys keep catalog ordinals owned by Translations and CLI tokens owned by the CLI.
    private static readonly Dictionary<string, TranslationKey> Keys = new Dictionary<string, TranslationKey>(StringComparer.Ordinal)
    {
        ["commands.greet"] = CliTextKeys.r_636f6d6d616e6473_r_6772656574,
        ["arguments.name"] = CliTextKeys.r_617267756d656e7473_r_6e616d65,
        ["help.options-placeholder"] = CliTextKeys.r_68656c70_r_6f7074696f6e73506c616365686f6c646572,
        ["help.command-placeholder"] = CliTextKeys.r_68656c70_r_636f6d6d616e64506c616365686f6c646572,
        ["help.required"] = CliTextKeys.r_68656c70_r_7265717569726564,
        ["help.completion"] = CliTextKeys.r_68656c70_r_636f6d706c6574696f6e,
        ["help.usage"] = CliTextKeys.r_68656c70_r_7573616765,
        ["help.commands"] = CliTextKeys.r_68656c70_r_636f6d6d616e6473,
        ["help.arguments"] = CliTextKeys.r_68656c70_r_617267756d656e7473,
        ["help.options"] = CliTextKeys.r_68656c70_r_6f7074696f6e73,
        ["help.show-help"] = CliTextKeys.r_68656c70_r_73686f7748656c70,
        ["help.show-version"] = CliTextKeys.r_68656c70_r_73686f7756657273696f6e,
        ["help.output-format"] = CliTextKeys.r_68656c70_r_73656c6563744f7574707574,
        ["diagnostics.unknown-option"] = CliTextKeys.r_646961676e6f7374696373_r_756e6b6e6f776e4f7074696f6e,
    };
    public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments) =>
        Keys.TryGetValue(key, out TranslationKey translationKey) && snapshot.TryGet(translationKey, out _)
            ? snapshot.Format(translationKey, []) : null;
}
