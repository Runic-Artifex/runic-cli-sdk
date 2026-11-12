using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

// Usage: TextKeysWriter <Runic.CommandLine.dll> <output.json> <package version> [<key without catalog message>...]
//
// Reads CommandTextKeys.All by reflection, so the writer does not reference the assembly it describes, and writes each
// key with its ordered argument names and the name of its message in the runic-command-line catalog. The keys after
// the version have no message in the built-in catalog on purpose; their entries carry "message": null.
if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: TextKeysWriter <Runic.CommandLine.dll> <output.json> <package version> [<key without catalog message>...]");
    return 64;
}

Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
Type keys = assembly.GetType("Runic.CommandLine.CommandTextKeys", throwOnError: true)!;
var all = (IEnumerable)keys.GetProperty("All", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
var withoutMessage = new HashSet<string>(args[3..], StringComparer.Ordinal);
var seen = new HashSet<string>(StringComparer.Ordinal);

using var buffer = new MemoryStream();
using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
{
    json.WriteStartObject();
    json.WriteNumber("schemaVersion", 1);
    json.WriteString("package", "Runic.CommandLine");
    json.WriteString("version", args[2]);
    json.WriteString("catalog", "runic-command-line");
    // Relative to this file: the catalog project and its sources sit next to it in the package.
    json.WriteString("catalogProject", "runic.json");
    json.WriteStartArray("keys");
    foreach (object item in all)
    {
        Type type = item.GetType();
        string key = (string)type.GetProperty("Key")!.GetValue(item)!;
        var arguments = (IEnumerable<string>)type.GetProperty("Arguments")!.GetValue(item)!;
        if (!seen.Add(key)) throw new InvalidOperationException("CommandTextKeys.All lists " + key + " twice.");
        json.WriteStartObject();
        json.WriteString("key", key);
        json.WriteStartArray("arguments");
        foreach (string argument in arguments) json.WriteStringValue(argument);
        json.WriteEndArray();
        if (withoutMessage.Contains(key)) json.WriteNull("message");
        else json.WriteString("message", MessageName(key));
        json.WriteEndObject();
    }

    json.WriteEndArray();
    json.WriteEndObject();
}

foreach (string key in withoutMessage)
    if (!seen.Contains(key)) throw new InvalidOperationException(key + " is listed without a catalog message, but CommandTextKeys.All does not contain it.");

buffer.WriteByte((byte)'\n');
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllBytes(output, buffer.ToArray());
return 0;

// The catalog message name: each hyphen is removed and the letter after it capitalized
// (help.show-help is help.showHelp), as Runic.Translations.CommandLine looks messages up.
static string MessageName(string key)
{
    var name = new StringBuilder(key.Length);
    bool upper = false;
    foreach (char character in key)
    {
        if (character == '-')
        {
            upper = true;
            continue;
        }

        name.Append(upper && character != '.' ? char.ToUpperInvariant(character) : character);
        upper = false;
    }

    return name.ToString();
}
