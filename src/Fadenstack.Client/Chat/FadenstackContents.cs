using System.Text.Json;
using Fadenstack.Governance;
using Microsoft.Extensions.AI;

namespace Fadenstack.Chat;

/// <summary>
/// One of the gateway's events (AG-UI vocabulary): a gateway tool starting or ending, citations,
/// a PII notice, a gateway round to keep, an approval the gateway waits for, a refused tool. Carried in a
/// response's contents; clients that do not know it ignore it.
/// </summary>
public sealed class FadenstackEventContent : AIContent
{
    public FadenstackEventContent(JsonElement @event)
    {
        Event = @event.Clone();
        Type = Event.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        Name = Event.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        Value = Event.TryGetProperty("value", out var v) ? v.Clone() : default;
    }

    /// <summary>AG-UI type: <c>TOOL_CALL_START</c>, <c>TOOL_CALL_END</c>, <c>CUSTOM</c>.</summary>
    public string Type { get; }

    /// <summary>For <c>CUSTOM</c>: <c>faden.gateway_round</c>, <c>faden.approval_required</c>, ...</summary>
    public string? Name { get; }

    public JsonElement Value { get; }

    /// <summary>The whole event, as the gateway sent it.</summary>
    public JsonElement Event { get; }

    /// <summary><c>Type</c>, or <c>Name</c> for a custom event.</summary>
    public string Kind => Type == "CUSTOM" && Name is not null ? Name : Type;

    public override string ToString() => $"faden event {Kind}";
}

/// <summary>A refusal: the prompt (<c>prompt_blocked</c>) or the answer (<c>response_blocked</c>).</summary>
public sealed class GovernanceNoticeContent : AIContent
{
    public GovernanceNoticeContent(GovernanceNotice notice)
    {
        Notice = notice;
    }

    public GovernanceNotice Notice { get; }

    public override string ToString() => $"{Notice.Outcome}: {Notice.Message}";
}

/// <summary>The keys a request's <see cref="ChatOptions.AdditionalProperties"/> uses for Fadenstack.</summary>
public static class FadenstackChatOptions
{
    /// <summary>Groups usage per conversation without the server keeping it (<c>X-Faden-Conversation-Id</c>).</summary>
    public const string ConversationId = "faden.conversation_id";

    /// <summary><c>X-Faden-Turn-Id</c>.</summary>
    public const string TurnId = "faden.turn_id";

    /// <summary>A <see cref="Tools.ToolMode"/> for this request (<c>X-Faden-Tool-Mode</c>).</summary>
    public const string ToolMode = "faden.tool_mode";

    /// <summary>A server session, for the plain routes (web chat and API sessions); agents have none.</summary>
    public const string SessionId = "faden.session_id";

    /// <summary>
    /// Skills picked by slug for this message: the gateway gives the request what they say (an agent's chat and the
    /// plain routes). Each request names them; a later one without them has none.
    /// </summary>
    public const string Skills = "faden.skills";

    /// <summary>Set on a response the gateway refused: <c>prompt_blocked</c> or <c>response_blocked</c>.</summary>
    public const string Outcome = "faden.outcome";

    /// <summary>Set on a response the gateway refused: the <see cref="GovernanceNotice"/>.</summary>
    public const string Governance = "faden.governance";

    public static ChatOptions WithConversation(this ChatOptions options, string conversationId, string? turnId = null)
    {
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties[ConversationId] = conversationId;
        if (turnId is not null)
        {
            options.AdditionalProperties[TurnId] = turnId;
        }
        return options;
    }

    public static ChatOptions WithToolMode(this ChatOptions options, Tools.ToolMode mode)
    {
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties[ToolMode] = mode;
        return options;
    }

    public static ChatOptions WithSession(this ChatOptions options, string sessionId)
    {
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties[SessionId] = sessionId;
        return options;
    }

    /// <summary>The skills (<see cref="Models.SkillOption.Slug"/>) the user picked for this request; none, nothing is sent.</summary>
    public static ChatOptions WithSkills(this ChatOptions options, IEnumerable<string> slugs)
    {
        var picked = slugs.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToArray();
        if (picked.Length == 0)
        {
            options.AdditionalProperties?.Remove(Skills);
            return options;
        }
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties[Skills] = picked;
        return options;
    }

    /// <summary>The refusal on a response, if the gateway refused it.</summary>
    public static GovernanceNotice? GetGovernanceNotice(this ChatResponse response) =>
        response.AdditionalProperties?.TryGetValue(Governance, out var n) == true ? n as GovernanceNotice
        : response.Messages.SelectMany(m => m.Contents).OfType<GovernanceNoticeContent>().FirstOrDefault()?.Notice;
}
