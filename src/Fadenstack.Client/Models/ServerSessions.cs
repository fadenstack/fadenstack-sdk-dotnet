namespace Fadenstack.Models;

// The server's own sessions: the web chat and plain API use. An agent's sessions live on the device.
// JSON names are the server's snake_case (FadenstackJson).

public sealed record Session
{
    public Guid Id { get; init; }
    public string TenantId { get; init; } = "";
    public string UserId { get; init; } = "";
    public Guid? ProjectId { get; init; }
    public string? Title { get; init; }
    public string Status { get; init; } = "";
    public Dictionary<string, object?>? MetadataJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record Project
{
    public Guid Id { get; init; }
    public string TenantId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? SystemInstructions { get; init; }
    public string? ModelAlias { get; init; }
    public Dictionary<string, object?>? MetadataJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A message of a server session, as stored.</summary>
public sealed record SessionMessage
{
    public Guid Id { get; init; }
    public Guid SessionId { get; init; }
    public string Role { get; init; } = "";

    /// <summary>Empty for a refused message: show <see cref="Notice"/>.</summary>
    public string Content { get; init; } = "";

    public List<Dictionary<string, object?>>? ContentParts { get; init; }
    public Dictionary<string, object?>? ToolCallJson { get; init; }
    public string? ModelAlias { get; init; }
    public Guid? ProviderInstanceId { get; init; }
    public int? TokenEstimate { get; init; }
    public string? TraceId { get; init; }

    /// <summary><c>ok</c>, <c>blocked_input</c>, <c>blocked_output</c> or <c>error</c>.</summary>
    public string Status { get; init; } = "ok";

    public Guid? DecisionId { get; init; }

    /// <summary>What the user was told about a refused message.</summary>
    public string? Notice { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record Summary
{
    public Guid Id { get; init; }
    public Guid SessionId { get; init; }
    public string SummaryText { get; init; } = "";
    public Guid LastMessageId { get; init; }
    public int TokenEstimate { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record MemoryFact
{
    public Guid Id { get; init; }
    public string TenantId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string Scope { get; init; } = "";
    public Guid? SessionId { get; init; }
    public Guid? ProjectId { get; init; }
    public string Key { get; init; } = "";
    public string Value { get; init; } = "";
    public double Confidence { get; init; }
    public Guid? SourceMessageId { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A tool a chat may use, with its class and what the chat's tool mode does with it.</summary>
public sealed record ToolAvailability
{
    public string ToolId { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string Realm { get; init; } = "";
    public string Source { get; init; } = "";
    public bool EffectiveEnabled { get; init; }
    public bool PolicyAllowed { get; init; }
    public bool UserEnabled { get; init; }
    public bool Available { get; init; }
    public string? AvailabilityReason { get; init; }

    /// <summary><c>read</c>, <c>write</c>, <c>destructive</c> or <c>unknown</c>.</summary>
    public string ToolClass { get; init; } = "unknown";

    public string ClassSource { get; init; } = "none";
    public bool? OpenWorld { get; init; }

    /// <summary><c>run</c>, <c>ask</c> or <c>hidden</c>.</summary>
    public string ModeDecision { get; init; } = "ask";

    public string? ModeReason { get; init; }
}

public sealed record ToolAvailabilityResponse
{
    public string? SessionId { get; init; }
    public string ExecutionMode { get; init; } = "";
    public int EffectiveCatalogVersion { get; init; }
    public List<ToolAvailability> Tools { get; init; } = new();
    public string ToolMode { get; init; } = "ask";
    public string EffectiveToolMode { get; init; } = "ask";
    public string? ToolModeCeiling { get; init; }
}

public sealed record SessionToolPolicy
{
    public string SessionId { get; init; } = "";
    public string ExecutionMode { get; init; } = "";
    public string? HybridPreference { get; init; }
    public int EffectiveCatalogVersion { get; init; }

    /// <summary>What the user picked (<c>ask</c> until they pick).</summary>
    public string ToolMode { get; init; } = "ask";

    /// <summary>The pick, narrowed by any ceiling the server sets.</summary>
    public string EffectiveToolMode { get; init; } = "ask";

    public string? ToolModeCeiling { get; init; }
}

public sealed record Attachment
{
    public Guid Id { get; init; }
    public string TenantId { get; init; } = "";
    public string UserId { get; init; } = "";
    public Guid? SessionId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? MessageId { get; init; }
    public string Filename { get; init; } = "";
    public string ContentType { get; init; } = "";
    public long SizeBytes { get; init; }
    public string ExtractionStatus { get; init; } = "";
    public string Scope { get; init; } = "";
    public int? PageCount { get; init; }
    public bool Truncated { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record AttachmentUploadResponse
{
    public Attachment Attachment { get; init; } = new();
    public int ExtractedTextLength { get; init; }
    public int TokenEstimate { get; init; }
}

public sealed record AttachmentStats
{
    public int TotalCount { get; init; }
    public long TotalBytes { get; init; }
}

public sealed record ServiceInfo
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Enabled { get; init; }
    public string? Url { get; init; }
    public string Status { get; init; } = "";
    public string? Version { get; init; }
    public Dictionary<string, object?>? Metadata { get; init; }
}

internal sealed record DataWrapper<T>
{
    public List<T>? Data { get; init; }
}
