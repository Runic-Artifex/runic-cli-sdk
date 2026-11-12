using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Runic.Translations;
using Runic.Translations.CommandLine.Translations;
using Runic.Translations.Generated;

namespace Runic.CommandLine.TextCatalog.Tests;

/// <summary>
/// Resolves framework keys from the compiled built-in catalog the way Runic.Translations.CommandLine does: the message
/// named in text-keys.json, its inputs bound to the key's arguments by name. It has no application catalog and no
/// fallback chain, so the tests see exactly the catalog's text.
/// </summary>
internal sealed class CatalogTextResolver : ICommandTextResolver
{
    private readonly ITranslationSnapshot _snapshot;
    private readonly Dictionary<string, TextKeyEntry> _keys;

    private CatalogTextResolver(ITranslationSnapshot snapshot, TextKeysFile keys)
    {
        _snapshot = snapshot;
        _keys = keys.Keys.ToDictionary(static entry => entry.Key, System.StringComparer.Ordinal);
    }

    public static async Task<CatalogTextResolver> CreateAsync(string locale, TextKeysFile keys) =>
        new(await CommandLineTextCatalog.CreateProvider().GetSnapshotAsync(locale), keys);

    public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments)
    {
        if (!_keys.TryGetValue(key, out TextKeyEntry? entry) || entry.Message is null ||
            !_snapshot.TryGetKey(entry.Message, out TranslationKey translationKey) ||
            !_snapshot.TryGetPlaceholders(translationKey, out System.ReadOnlyMemory<TranslationPlaceholderDescriptor> placeholders))
            return null;
        var values = new TextArgument[placeholders.Length];
        for (int index = 0; index < values.Length; index++)
        {
            string name = placeholders.Span[index].Name;
            int position = entry.Arguments.ToList().IndexOf(name);
            // The framework leaves out an argument it does not have, such as the command of an empty command line.
            if (position < 0 || position >= arguments.Count) return null;
            values[index] = GeneratedCatalogSupport.CreateArgument(name, new TextArgument("_", arguments[position]));
        }

        return _snapshot.Format(translationKey, values);
    }
}
