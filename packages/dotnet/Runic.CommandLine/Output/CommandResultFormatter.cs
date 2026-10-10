using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Formats typed results using their registered JSON metadata, without reflection or record debug strings.</summary>
public static class CommandResultFormatter
{
    private const int IndentWidth = 2;

    /// <summary>Writes object fields as labeled rows and scalar results as text.</summary>
    /// <remarks>
    /// Rows use the JSON property names. Booleans are <c>true</c>/<c>false</c>, numbers keep their JSON
    /// form and null is empty. A list of scalars is joined with commas; nested objects and lists of objects
    /// are indented below their label, one <c>- </c> entry per item. A top-level list writes one line per
    /// scalar or one entry per object.
    /// </remarks>
    public static ValueTask WriteHumanAsync<T>(T value, JsonTypeInfo<T> typeInfo, ICommandConsole console, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(console);
        JsonElement element = JsonSerializer.SerializeToElement(value, typeInfo);
        var text = new StringBuilder();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                AppendMembers(text, element, 0);
                break;
            case JsonValueKind.Array when IsScalarList(element):
                foreach (JsonElement item in element.EnumerateArray()) AppendLine(text, 0, Scalar(item));
                break;
            case JsonValueKind.Array:
                AppendItems(text, element, 0);
                break;
            default:
                AppendLine(text, 0, Scalar(element));
                break;
        }

        return console.WriteOutAsync(text.ToString().AsMemory(), cancellationToken);
    }

    private static void AppendMembers(StringBuilder text, JsonElement value, int indent)
    {
        foreach (JsonProperty property in value.EnumerateObject())
        {
            JsonElement member = property.Value;
            if (member.ValueKind == JsonValueKind.Object && HasMembers(member))
            {
                AppendLine(text, indent, property.Name + ":");
                AppendMembers(text, member, indent + IndentWidth);
            }
            else if (member.ValueKind == JsonValueKind.Array && !IsScalarList(member))
            {
                AppendLine(text, indent, property.Name + ":");
                AppendItems(text, member, indent + IndentWidth);
            }
            else
            {
                string scalar = member.ValueKind switch
                {
                    JsonValueKind.Array => JoinScalars(member),
                    JsonValueKind.Object => "",
                    _ => Scalar(member),
                };
                AppendLine(text, indent, scalar.Length == 0 ? property.Name + ":" : property.Name + ": " + scalar);
            }
        }
    }

    // Each item starts with "- "; an object's remaining rows align under its first row.
    private static void AppendItems(StringBuilder text, JsonElement list, int indent)
    {
        foreach (JsonElement item in list.EnumerateArray())
        {
            var entry = new StringBuilder();
            if (item.ValueKind == JsonValueKind.Object && HasMembers(item)) AppendMembers(entry, item, indent + IndentWidth);
            else if (item.ValueKind == JsonValueKind.Array && !IsScalarList(item)) AppendItems(entry, item, indent + IndentWidth);
            else AppendLine(entry, indent + IndentWidth, item.ValueKind == JsonValueKind.Array ? JoinScalars(item) : item.ValueKind == JsonValueKind.Object ? "" : Scalar(item));
            string rendered = entry.ToString(), padding = new(' ', indent + IndentWidth);
            text.Append(new string(' ', indent)).Append(rendered.StartsWith(padding, StringComparison.Ordinal) ? "- " + rendered[padding.Length..] : "-" + rendered);
        }
    }

    private static void AppendLine(StringBuilder text, int indent, string line)
    {
        string padding = new(' ', indent);
        // Continuation lines of multi-line text stay inside their row.
        text.Append((padding + line.Replace("\n", "\n" + padding + new string(' ', IndentWidth), StringComparison.Ordinal)).TrimEnd(' ')).Append('\n');
    }

    private static bool HasMembers(JsonElement value)
    {
        using JsonElement.ObjectEnumerator members = value.EnumerateObject();
        return members.MoveNext();
    }

    private static bool IsScalarList(JsonElement list)
    {
        foreach (JsonElement item in list.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return false;
        }

        return true;
    }

    private static string JoinScalars(JsonElement list)
    {
        var text = new StringBuilder();
        foreach (JsonElement item in list.EnumerateArray())
        {
            if (text.Length > 0) text.Append(", ");
            text.Append(Scalar(item));
        }

        return text.ToString();
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };
}
