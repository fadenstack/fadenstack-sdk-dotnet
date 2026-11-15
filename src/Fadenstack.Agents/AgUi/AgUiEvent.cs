using System.Text.Json;

namespace Fadenstack.Agents.AgUi;

/// <summary>
/// One AG-UI event (docs.ag-ui.com, protocol 1.0), as our own small record: the SDK takes no dependency on an AG-UI
/// package. <see cref="AgUiJson.Serialize"/> writes the JSON a third-party AG-UI client reads: camelCase keys and a
/// <c>type</c> in AG-UI's SCREAMING_SNAKE names. Only the events a Fadenstack run produces are here.
/// </summary>
public abstract record AgUiEvent
{
    internal AgUiEvent()
    {
    }

    /// <summary>The AG-UI event type: <c>RUN_STARTED</c>, <c>TEXT_MESSAGE_CONTENT</c>, <c>TOOL_CALL_START</c>, ...</summary>
    public abstract string Type { get; }

    /// <summary>Milliseconds since the Unix epoch, when the event was made. Null leaves the key out.</summary>
    public long? Timestamp { get; init; }

    /// <summary>Extra information on the event (AG-UI's <c>metadata</c>). Null leaves the key out.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    internal abstract void WriteFields(Utf8JsonWriter writer);
}

/// <summary>A run begins (a turn of the agent). A run that resumes after an interrupt is a new run on the same thread.</summary>
public sealed record RunStarted(string ThreadId, string RunId) : AgUiEvent
{
    public override string Type => "RUN_STARTED";

    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("threadId", ThreadId);
        writer.WriteString("runId", RunId);
    }
}

/// <summary>One thing a paused run waits for. For an approval, <see cref="Id"/> and <see cref="ToolCallId"/> are the call's id.</summary>
public sealed record AgUiInterrupt(string Id, string Reason)
{
    public string? Message { get; init; }

    public string? ToolCallId { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>Token counts of the run, as AG-UI's <c>usage</c> entry (counts only, never content).</summary>
public sealed record AgUiTokenUsage
{
    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public long? TotalTokens { get; init; }
}

/// <summary>
/// A run ends. With <see cref="Interrupts"/> it ends with AG-UI's <c>interrupt</c> outcome (the run waits for an
/// answer, and the next run resumes it); without, with the <c>success</c> outcome.
/// </summary>
public sealed record RunFinished(string ThreadId, string RunId) : AgUiEvent
{
    public override string Type => "RUN_FINISHED";

    /// <summary>The run's return value (here: <c>{ "outcome": "completed" }</c> and the like). Null leaves it out.</summary>
    public IReadOnlyDictionary<string, object?>? Result { get; init; }

    public IReadOnlyList<AgUiInterrupt>? Interrupts { get; init; }

    public IReadOnlyList<AgUiTokenUsage>? Usage { get; init; }

    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("threadId", ThreadId);
        writer.WriteString("runId", RunId);
        if (Result is not null)
        {
            writer.WritePropertyName("result");
            AgUiJson.WriteValue(writer, Result);
        }
        writer.WriteStartObject("outcome");
        if (Interrupts is { Count: > 0 })
        {
            writer.WriteString("type", "interrupt");
            writer.WriteStartArray("interrupts");
            foreach (var interrupt in Interrupts)
            {
                writer.WriteStartObject();
                writer.WriteString("id", interrupt.Id);
                writer.WriteString("reason", interrupt.Reason);
                if (interrupt.Message is not null)
                {
                    writer.WriteString("message", interrupt.Message);
                }
                if (interrupt.ToolCallId is not null)
                {
                    writer.WriteString("toolCallId", interrupt.ToolCallId);
                }
                AgUiJson.WriteMetadata(writer, interrupt.Metadata);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        else
        {
            writer.WriteString("type", "success");
        }
        writer.WriteEndObject();
        if (Usage is { Count: > 0 })
        {
            writer.WriteStartArray("usage");
            foreach (var u in Usage)
            {
                writer.WriteStartObject();
                if (u.InputTokens is { } i)
                {
                    writer.WriteNumber("inputTokens", i);
                }
                if (u.OutputTokens is { } o)
                {
                    writer.WriteNumber("outputTokens", o);
                }
                if (u.TotalTokens is { } t)
                {
                    writer.WriteNumber("totalTokens", t);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
    }
}

/// <summary>A run failed. A stopped turn (an exception from the stream) ends this way.</summary>
public sealed record RunError(string Message) : AgUiEvent
{
    public override string Type => "RUN_ERROR";

    public string? Code { get; init; }

    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("message", Message);
        if (Code is not null)
        {
            writer.WriteString("code", Code);
        }
    }
}

public sealed record TextMessageStart(string MessageId) : AgUiEvent
{
    public override string Type => "TEXT_MESSAGE_START";

    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("messageId", MessageId);
        writer.WriteString("role", "assistant");
    }
}

public sealed record TextMessageContent(string MessageId, string Delta) : AgUiEvent
{
    public override string Type => "TEXT_MESSAGE_CONTENT";

    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("messageId", MessageId);
        writer.WriteString("delta", Delta);
    }
}

public sealed record TextMessageEnd(string MessageId) : AgUiEvent
{
    public override string Type => "TEXT_MESSAGE_END";

    internal override void WriteFields(Utf8JsonWriter writer) => writer.WriteString("messageId", MessageId);
}

public sealed record ReasoningStart(string MessageId) : AgUiEvent
{
    public override string Type => "REASONING_START";

    internal override void WriteFields(Utf8JsonWriter writer) => writer.WriteString("messageId", MessageId);
}

public sealed record ReasoningMessageStart(string MessageId) : AgUiEvent
{
    public override string Type => "REASONING_MESSAGE_START";

    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("messageId", MessageId);
        writer.WriteString("role", "reasoning");
    }
}

public sealed record ReasoningMessageContent(string MessageId, string Delta) : AgUiEvent
{
    public override string Type => "REASONING_MESSAGE_CONTENT";

    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("messageId", MessageId);
        writer.WriteString("delta", Delta);
    }
}

public sealed record ReasoningMessageEnd(string MessageId) : AgUiEvent
{
    public override string Type => "REASONING_MESSAGE_END";

    internal override void WriteFields(Utf8JsonWriter writer) => writer.WriteString("messageId", MessageId);
}

public sealed record ReasoningEnd(string MessageId) : AgUiEvent
{
    public override string Type => "REASONING_END";

    internal override void WriteFields(Utf8JsonWriter writer) => writer.WriteString("messageId", MessageId);
}

public sealed record ToolCallStart(string ToolCallId, string ToolCallName) : AgUiEvent
{
    public override string Type => "TOOL_CALL_START";

    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("toolCallId", ToolCallId);
        writer.WriteString("toolCallName", ToolCallName);
    }
}

/// <summary>The call's arguments as one JSON text (<see cref="Delta"/>).</summary>
public sealed record ToolCallArgs(string ToolCallId, string Delta) : AgUiEvent
{
    public override string Type => "TOOL_CALL_ARGS";

    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("toolCallId", ToolCallId);
        writer.WriteString("delta", Delta);
    }
}

public sealed record ToolCallEnd(string ToolCallId) : AgUiEvent
{
    public override string Type => "TOOL_CALL_END";

    internal override void WriteFields(Utf8JsonWriter writer) => writer.WriteString("toolCallId", ToolCallId);
}

public sealed record ToolCallResult(string MessageId, string ToolCallId, string Content) : AgUiEvent
{
    public override string Type => "TOOL_CALL_RESULT";

    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("messageId", MessageId);
        writer.WriteString("toolCallId", ToolCallId);
        writer.WriteString("content", Content);
        writer.WriteString("role", "tool");
    }
}

/// <summary>What AG-UI has no event for (a governance notice, a citation list, a mark): a name with the <c>faden.</c> prefix and a value.</summary>
public sealed record Custom(string Name, object? Value) : AgUiEvent
{
    public override string Type => "CUSTOM";

    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("name", Name);
        writer.WritePropertyName("value");
        AgUiJson.WriteValue(writer, Value);
    }
}
