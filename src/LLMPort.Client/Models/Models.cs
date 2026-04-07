using System.Text.Json.Serialization;

namespace LLMPort.Client.Models;

public sealed record Session
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("tenant_id")] public required string TenantId { get; init; }
    [JsonPropertyName("user_id")] public required string UserId { get; init; }
    [JsonPropertyName("project_id")] public Guid? ProjectId { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("metadata_json")] public Dictionary<string, object>? MetadataJson { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record Project
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("tenant_id")] public required string TenantId { get; init; }
    [JsonPropertyName("user_id")] public required string UserId { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("system_instructions")] public string? SystemInstructions { get; init; }
    [JsonPropertyName("model_alias")] public string? ModelAlias { get; init; }
    [JsonPropertyName("metadata_json")] public Dictionary<string, object>? MetadataJson { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ChatMessage
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("session_id")] public required Guid SessionId { get; init; }
    [JsonPropertyName("role")] public required string Role { get; init; }
    [JsonPropertyName("content")] public required string Content { get; init; }
    [JsonPropertyName("content_parts")] public List<Dictionary<string, object>>? ContentParts { get; init; }
    [JsonPropertyName("tool_call_json")] public Dictionary<string, object>? ToolCallJson { get; init; }
    [JsonPropertyName("model_alias")] public string? ModelAlias { get; init; }
    [JsonPropertyName("provider_instance_id")] public Guid? ProviderInstanceId { get; init; }
    [JsonPropertyName("token_estimate")] public int? TokenEstimate { get; init; }
    [JsonPropertyName("trace_id")] public string? TraceId { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record Summary
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("session_id")] public required Guid SessionId { get; init; }
    [JsonPropertyName("summary_text")] public required string SummaryText { get; init; }
    [JsonPropertyName("last_message_id")] public required Guid LastMessageId { get; init; }
    [JsonPropertyName("token_estimate")] public required int TokenEstimate { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record MemoryFact
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("tenant_id")] public required string TenantId { get; init; }
    [JsonPropertyName("user_id")] public required string UserId { get; init; }
    [JsonPropertyName("scope")] public required string Scope { get; init; }
    [JsonPropertyName("session_id")] public Guid? SessionId { get; init; }
    [JsonPropertyName("project_id")] public Guid? ProjectId { get; init; }
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("value")] public required string Value { get; init; }
    [JsonPropertyName("confidence")] public required double Confidence { get; init; }
    [JsonPropertyName("source_message_id")] public Guid? SourceMessageId { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ToolAvailability
{
    [JsonPropertyName("tool_id")] public required string ToolId { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("realm")] public required string Realm { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("effective_enabled")] public required bool EffectiveEnabled { get; init; }
    [JsonPropertyName("policy_allowed")] public required bool PolicyAllowed { get; init; }
    [JsonPropertyName("user_enabled")] public required bool UserEnabled { get; init; }
    [JsonPropertyName("available")] public required bool Available { get; init; }
    [JsonPropertyName("availability_reason")] public string? AvailabilityReason { get; init; }
}

public sealed record ToolAvailabilityResponse
{
    [JsonPropertyName("session_id")] public required string SessionId { get; init; }
    [JsonPropertyName("execution_mode")] public required string ExecutionMode { get; init; }
    [JsonPropertyName("effective_catalog_version")] public int EffectiveCatalogVersion { get; init; }
    [JsonPropertyName("tools")] public required List<ToolAvailability> Tools { get; init; }
}

public sealed record SessionToolPolicy
{
    [JsonPropertyName("session_id")] public required string SessionId { get; init; }
    [JsonPropertyName("execution_mode")] public required string ExecutionMode { get; init; }
    [JsonPropertyName("hybrid_preference")] public string? HybridPreference { get; init; }
    [JsonPropertyName("effective_catalog_version")] public int EffectiveCatalogVersion { get; init; }
}

public sealed record Attachment
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("tenant_id")] public required string TenantId { get; init; }
    [JsonPropertyName("user_id")] public required string UserId { get; init; }
    [JsonPropertyName("session_id")] public Guid? SessionId { get; init; }
    [JsonPropertyName("project_id")] public Guid? ProjectId { get; init; }
    [JsonPropertyName("message_id")] public Guid? MessageId { get; init; }
    [JsonPropertyName("filename")] public required string Filename { get; init; }
    [JsonPropertyName("content_type")] public required string ContentType { get; init; }
    [JsonPropertyName("size_bytes")] public required long SizeBytes { get; init; }
    [JsonPropertyName("extraction_status")] public required string ExtractionStatus { get; init; }
    [JsonPropertyName("scope")] public required string Scope { get; init; }
    [JsonPropertyName("page_count")] public int? PageCount { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record AttachmentUploadResponse
{
    [JsonPropertyName("attachment")] public required Attachment Attachment { get; init; }
    [JsonPropertyName("extracted_text_length")] public required int ExtractedTextLength { get; init; }
    [JsonPropertyName("token_estimate")] public required int TokenEstimate { get; init; }
}

public sealed record AttachmentStats
{
    [JsonPropertyName("total_count")] public required int TotalCount { get; init; }
    [JsonPropertyName("total_bytes")] public required long TotalBytes { get; init; }
}

public sealed record ServiceInfo
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("display_name")] public required string DisplayName { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("metadata")] public Dictionary<string, object>? Metadata { get; init; }
}
