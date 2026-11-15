using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Fadenstack.Agents.AgUi;

/// <summary>The AG-UI JSON of an event: compact, camelCase keys, <c>type</c> first, <c>metadata</c> last.</summary>
public static class AgUiJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions ValueOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The event as one line of JSON (no line break), as an SSE <c>data:</c> field or a JSON Lines record carries it.</summary>
    public static string Serialize(AgUiEvent value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("type", value.Type);
            if (value.Timestamp is { } timestamp)
            {
                writer.WriteNumber("timestamp", timestamp);
            }
            value.WriteFields(writer);
            WriteMetadata(writer, value.Metadata);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Any value as JSON text, the way the arguments of a tool call go into <c>TOOL_CALL_ARGS</c>.</summary>
    public static string SerializeValue(object? value) => JsonSerializer.Serialize<object?>(value, ValueOptions);

    internal static void WriteMetadata(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null)
        {
            return;
        }
        writer.WritePropertyName("metadata");
        WriteValue(writer, metadata);
    }

    internal static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        if (value is null || value is JsonElement { ValueKind: JsonValueKind.Undefined })
        {
            writer.WriteNullValue();
            return;
        }
        JsonSerializer.Serialize<object?>(writer, value, ValueOptions);
    }
}
