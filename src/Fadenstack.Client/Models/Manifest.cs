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

    /// <summary>What the app reports about the device it runs on (sdk/51 8.3); null: nothing. The SDK fills it in when it sends the manifest.</summary>
    public ManifestDevice? Device { get; init; }
}

/// <summary>
/// What the app reports about the device (<c>device</c> of <c>PUT /v1/agents/{agent}/manifest</c>): a short description of the
/// operating system, the app's version, its plugins and the local MCP programs it runs. Never a path, a user name, an argument or
/// an environment value. The limits are the server's: see <see cref="DeviceLimits"/>. TS: <c>ManifestDevice</c>.
/// </summary>
public sealed record ManifestDevice
{
    /// <summary>The operating system and architecture, at most <see cref="DeviceLimits.Os"/> characters.</summary>
    public string Os { get; init; } = "";

    /// <summary>The app's own version, at most <see cref="DeviceLimits.AppVersion"/> characters.</summary>
    public string AppVersion { get; init; } = "";

    public IReadOnlyList<ManifestPlugin> Plugins { get; init; } = [];

    public IReadOnlyList<ManifestMcpProgram> McpPrograms { get; init; } = [];
}

/// <summary>A plugin of the app, as the app declares it: its name, version and the SHA-256 of its file (64 lowercase hex). TS: <c>ManifestPlugin</c>.</summary>
public sealed record ManifestPlugin
{
    public string Name { get; init; } = "";

    public string Version { get; init; } = "";

    public string Sha256 { get; init; } = "";
}

/// <summary>
/// A local MCP server's program: the server's label, the program's file name (never its folder, which can hold the user name)
/// and its SHA-256. A server reached over http has none. The browser starts no local program, so the TypeScript SDK never sends one.
/// </summary>
public sealed record ManifestMcpProgram
{
    public string Name { get; init; } = "";

    public string Program { get; init; } = "";

    public string Sha256 { get; init; } = "";
}

/// <summary>The server's limits for the device block (core 0.7.1): the SDK cuts to them before it sends. TS: <c>DEVICE_LIMITS</c>.</summary>
public static class DeviceLimits
{
    public const int Os = 100;
    public const int AppVersion = 50;

    /// <summary>The most plugins, and the most MCP programs.</summary>
    public const int Entries = 100;

    public const int Name = 128;
    public const int Version = 50;
    public const int Program = 255;
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
