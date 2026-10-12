using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fadenstack.Http;

/// <summary>The server's JSON: snake_case, and nulls left out of requests.</summary>
public static class FadenstackJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
