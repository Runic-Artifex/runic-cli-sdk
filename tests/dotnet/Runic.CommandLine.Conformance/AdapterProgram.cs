using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Runic.CommandLine.Conformance;

// The adapter loop: one JSON request per line on stdin, one response per line on stdout, the log on stderr
// (rax.conformance-adapter/1). The driver builds every input and compares every result; the operations here only call
// Runic.CommandLine and report what it returned (eng/conformance/README.md, "Command Line").
internal static class AdapterProgram
{
    private const string Protocol = "rax.conformance-adapter/1";

    public static int Main()
    {
        // Only protocol lines go to stdout; anything the runtime or the library prints goes to the log.
        Stream stdout = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var operations = new Operations();

        while (input.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonObject response;
            bool shutdown = false;
            long sequence = 0;
            try
            {
                using JsonDocument request = JsonDocument.Parse(line);
                JsonElement root = request.RootElement;
                sequence = root.GetProperty("seq").GetInt64();
                string operation = root.GetProperty("op").GetString() ?? string.Empty;
                JsonElement arguments = root.TryGetProperty("args", out JsonElement value) ? value : default;
                shutdown = operation == "shutdown";
                response = new JsonObject { ["seq"] = sequence, ["ok"] = Dispatch(operations, operation, arguments) };
            }
            catch (UnsupportedException exception)
            {
                response = Error(sequence, "unsupported", exception.Message);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                response = Error(sequence, "failed", $"{exception.GetType().Name}: {exception.Message}");
            }

            byte[] bytes = Encoding.UTF8.GetBytes(response.ToJsonString() + "\n");
            stdout.Write(bytes);
            stdout.Flush();
            if (shutdown)
            {
                break;
            }
        }

        return 0;
    }

    private static JsonObject Dispatch(Operations operations, string operation, JsonElement arguments) => operation switch
    {
        "describe" => Describe(),
        "shutdown" => new JsonObject(),
        "cl.parse" => operations.Parse(arguments),
        "cl.classify" => operations.Classify(arguments),
        "cl.validate" => operations.Validate(arguments),
        "cl.catalog" => Operations.Catalog(arguments),
        "cl.help" => operations.Help(arguments),
        "cl.human" => Operations.Human(arguments),
        "cl.readEnvelope" => Operations.ReadEnvelope(arguments),
        "cl.write" => Operations.Write(arguments),
        _ => throw new UnsupportedException($"the .NET adapter has no operation {operation}"),
    };

    private static JsonObject Describe()
    {
        Assembly library = typeof(CommandCatalog).Assembly;
        string version = library.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? library.GetName().Version?.ToString()
            ?? "unknown";
        return new JsonObject
        {
            ["protocol"] = Protocol,
            ["implementation"] = new JsonObject
            {
                ["id"] = "dotnet-commandline",
                ["name"] = library.GetName().Name,
                ["version"] = version,
            },
            ["corpora"] = new JsonArray("commandline/1"),
            ["roles"] = new JsonArray("parser", "reader", "writer", "presenter"),
            // Root help lists the built-in completion command (help.md, "Commands"); generated binders convert
            // integers before their bounds (grammar.md, "Typed values"); the catalog may declare the Windows slash
            // dialect (grammar.md, "Windows slash dialect").
            ["features"] = new JsonArray("help:completion-command", "binding:integer-conversion", "dialect:windows-slash"),
            // Runic.CommandLine's writer is the one the `dotnet` escaping profile describes (protocol README).
            ["capabilities"] = new JsonArray("escaping-profile:dotnet"),
        };
    }

    private static JsonObject Error(long sequence, string code, string message) => new()
    {
        ["seq"] = sequence,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}

// The implementation lacks something a case needs: a catalog feature, a payload type or an operation.
internal sealed class UnsupportedException(string message) : Exception(message);
