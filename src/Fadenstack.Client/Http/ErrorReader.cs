using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Fadenstack.Http;

/// <summary>Turns an error response -- the gateway's OpenAI shape or the backend's <c>detail</c> -- into an exception.</summary>
internal static class ErrorReader
{
    internal sealed record Parsed(string Message, string? Code, string? Type, string? Param, JsonElement? Fadenstack);

    public static Parsed Parse(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            JsonElement? faden = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("faden", out var f)
                ? f.Clone() : null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                return new Parsed(Str(error, "message") ?? fallback, Str(error, "code"), Str(error, "type"),
                    Str(error, "param"), faden);
            }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.Object)
                {
                    return new Parsed(Str(detail, "message") ?? fallback, Str(detail, "code"), null, null, faden);
                }
                if (detail.ValueKind == JsonValueKind.String)
                {
                    return new Parsed(detail.GetString() ?? fallback, null, null, null, faden);
                }
            }
        }
        catch (JsonException)
        {
            // not JSON: the status says it
        }
        return new Parsed(string.IsNullOrWhiteSpace(body) ? fallback : Truncate(body), null, null, null, null);
    }

    public static FadenstackApiException ToException(HttpResponseMessage response, string body)
    {
        var status = response.StatusCode;
        var parsed = Parse(body, $"{(int)status} {response.ReasonPhrase}");
        FadenstackApiException exception = (int)status switch
        {
            401 => new FadenstackAuthenticationException(status, parsed.Message, parsed.Code),
            403 => new FadenstackPermissionException(status, parsed.Message, parsed.Code, parsed.Type),
            429 => new FadenstackRateLimitedException(status, parsed.Message, parsed.Code, RetryAfter(response)),
            _ => new FadenstackApiException(status, parsed.Message, parsed.Code, parsed.Type, parsed.Param),
        };
        exception.Faden = parsed.Fadenstack;
        return exception;
    }

    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s) => s.Length <= 300 ? s : s.Substring(0, 300) + "...";
}
