namespace Fadenstack.Models;

/// <summary>A skill the user can pick for a message (<see cref="Resources.AgentResource.ListSkillsAsync"/>); never its instructions.</summary>
public sealed record SkillOption
{
    public string Id { get; init; } = "";

    /// <summary>What a message names it by (<see cref="Chat.FadenstackChatOptions.Skills"/>).</summary>
    public string Slug { get; init; } = "";

    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary><c>always</c> (in every chat anyway), <c>auto</c> (when the question matches), <c>manual</c> (only when picked).</summary>
    public string Activation { get; init; } = "auto";
}

/// <summary>The skills a user can pick, and whether the server offers skills at all.</summary>
public sealed record SkillList
{
    /// <summary>False on a server without the skills module, or one too old for the route.</summary>
    public bool Available { get; init; }

    public IReadOnlyList<SkillOption> Skills { get; init; } = Array.Empty<SkillOption>();
}

/// <summary><c>GET /v1/agents/{agent}/skills</c> as the gateway sends it: <c>{"object":"list","available":true,"data":[...]}</c>.</summary>
internal sealed record SkillListWire
{
    public bool? Available { get; init; }

    public List<SkillWire>? Data { get; init; }

    public SkillList ToList() => new()
    {
        // Only an explicit false says the server has no skills: an answer that does not say still lists them.
        Available = Available != false,
        Skills = (Data ?? new List<SkillWire>()).Where(s => !string.IsNullOrEmpty(s.Slug)).Select(s => new SkillOption
        {
            Id = s.Id ?? "",
            Slug = s.Slug!,
            Name = string.IsNullOrEmpty(s.Name) ? s.Slug! : s.Name!,
            Description = s.Description ?? "",
            Activation = string.IsNullOrEmpty(s.Activation) ? "auto" : s.Activation!,
        }).ToList(),
    };
}

internal sealed record SkillWire
{
    public string? Id { get; init; }

    public string? Slug { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Activation { get; init; }
}
