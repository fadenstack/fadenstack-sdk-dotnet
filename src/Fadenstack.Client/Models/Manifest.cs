using System.Text.Json;
using System.Text.Json.Serialization;
using Fadenstack.Tools;

namespace Fadenstack.Models;

/// <summary>
/// What an app brings to the agent it runs (<c>PUT /v1/agents/{agent}/manifest</c>): its host tools, the context it
/// offers and its default greeting. The server keeps it for its admins to review; nothing in it changes what the
/// agent may do.
/// </summary>
public sealed record AppManifest
{
    public ManifestApp App { get; init; } = new();

    public ManifestSdk? Sdk { get; init; }

    public IReadOnlyList<ManifestTool> Tools { get; init; } = [];

    public IReadOnlyList<ManifestContext> Context { get; init; } = [];

    public ManifestUi? Ui { get; init; }
}

/// <summary>The app: "Word add-in", "2.1.0".</summary>
public sealed record ManifestApp
{
    public string Name { get; init; } = "";

    public string Version { get; init; } = "";
}

/// <summary>The SDK the app is built with.</summary>
public sealed record ManifestSdk
{
    public string Language { get; init; } = "";

    public string Version { get; init; } = "";
}

/// <summary>A host tool as the app's developer describes it: its class is a suggestion the server's admins review.</summary>
public sealed record ManifestTool
{
    public string Name { get; init; } = "";

    public string? Description { get; init; }

    /// <summary>The JSON schema of its arguments.</summary>
    public JsonElement? Parameters { get; init; }

    [JsonPropertyName("class")]
    public ToolClass Class { get; init; } = ToolClass.Unknown;

    public bool? OpenWorld { get; init; }

    /// <summary>The content for people it writes, and what kind of edit it makes; null for a tool that declares none.</summary>
    public IReadOnlyList<ManifestOutput>? Outputs { get; init; }
}

/// <summary>
/// An argument whose content a tool puts where people read it: its media type and its edit kind. On the wire
/// <c>{"argument", "media_type", "edit_kind"}</c> in the tool's <c>outputs</c> list, as the TypeScript SDK sends it.
/// </summary>
public sealed record ManifestOutput
{
    public string Argument { get; init; } = "";

    public string MediaType { get; init; } = "";

    /// <summary>The edit kind when it is fixed; null (left out) when the tool decides it per call.</summary>
    public EditKind? EditKind { get; init; }
}

/// <summary>Something the app offers the agent to read, by its id (<c>doc:selection</c>).</summary>
public sealed record ManifestContext
{
    public string Id { get; init; } = "";

    public string? Title { get; init; }

    public string? Kind { get; init; }
}

/// <summary>The greeting and suggestions the app would show.</summary>
public sealed record ManifestUi
{
    public string? Greeting { get; init; }

    public IReadOnlyList<string>? Suggestions { get; init; }
}

/// <summary>What the server made of a manifest: its hash, and whether it had not seen it before.</summary>
public sealed record ManifestReceipt
{
    public string Sha256 { get; init; } = "";

    [JsonPropertyName("new")]
    public bool IsNew { get; init; }
}
