using System.Text.Json;
using Fadenstack.Chat;
using Fadenstack.UI.Parts;

namespace Fadenstack.UI.ViewModels;

/// <summary>
/// Turns a gateway event into parts of the answer. Core renders citations, PII notices and, for an event it
/// does not know, a generic notice (so a UI shows an extension's events sensibly before it knows them). Modules add
/// their own through <see cref="AgentChatOptions.Renderers"/>; the first that handles an event wins.
/// </summary>
public interface IAgentEventRenderer
{
    bool TryRender(FadenstackEventContent gatewayEvent, TurnViewModel turn);
}

internal sealed class CitationsRenderer : IAgentEventRenderer
{
    public bool TryRender(FadenstackEventContent e, TurnViewModel turn)
    {
        if (e.Kind != "faden.citations")
        {
            return false;
        }
        var citations = new List<Citation>();
        if (e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty("sources", out var sources)
            && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sources.EnumerateArray())
            {
                var title = Json.Str(s, "source") ?? Json.Str(s, "document_id") ?? "Source";
                var chunk = s.TryGetProperty("chunk", out var c) && c.ValueKind == JsonValueKind.Number ? $"Part {c.GetInt32()}" : null;
                double? score = s.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : null;
                citations.Add(new Citation(title, chunk, score));
            }
        }
        if (citations.Count == 0)
        {
            return true;
        }
        if (turn.Parts.Count > 0 && turn.Parts[turn.Parts.Count - 1] is CitationsPart last)
        {
            last.Add(citations);
        }
        else
        {
            turn.AddPart(new CitationsPart(citations));
        }
        return true;
    }
}

internal sealed class PiiRenderer : IAgentEventRenderer
{
    public bool TryRender(FadenstackEventContent e, TurnViewModel turn)
    {
        if (e.Kind != "faden.pii")
        {
            return false;
        }
        if (e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty("entities", out var entities)
            && entities.ValueKind == JsonValueKind.Object)
        {
            var found = entities.EnumerateObject()
                .Select(p => (Type: p.Name, Count: p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : 0))
                .Where(p => p.Count > 0).ToList();
            if (found.Count > 0)
            {
                var total = found.Sum(f => f.Count);
                turn.AddPart(new NoticePart(NoticeSeverity.Info, "Personal data protected",
                    $"{total} value{(total == 1 ? "" : "s")} kept from the model: " +
                    string.Join(", ", found.Select(f => $"{f.Count} {Pretty(f.Type)}"))));
            }
        }
        return true;
    }

    private static string Pretty(string type) => type.Replace('_', ' ').ToLowerInvariant();
}

/// <summary>Events the chat shows elsewhere or not at all, and a generic notice for anything else.</summary>
internal sealed class FallbackRenderer : IAgentEventRenderer
{
    private static readonly HashSet<string> Handled = new(StringComparer.Ordinal)
    {
        "TOOL_CALL_START", "TOOL_CALL_END", "faden.gateway_round", "faden.approval_required", "faden.tool_denied",
        "faden.governance", "faden.tool_mode",
    };

    public bool TryRender(FadenstackEventContent e, TurnViewModel turn)
    {
        if (Handled.Contains(e.Kind) || e.Type != "CUSTOM")
        {
            return true;
        }
        var message = e.Value.ValueKind == JsonValueKind.Object && Json.Str(e.Value, "message") is { } m ? m : e.Kind;
        turn.AddPart(new NoticePart(NoticeSeverity.Info, "Notice", message));
        return true;
    }
}
