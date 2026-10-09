namespace Faden.Models;

/// <summary>
/// What to draw: OpenAI's images request with the settings local image servers take (sdk/11). Pictures come back
/// in the answer, never as links.
/// </summary>
public sealed record ImageRequest
{
    public string Prompt { get; init; } = "";

    /// <summary>"1024x1024", "1280x720"; the server's default when left out.</summary>
    public string? Size { get; init; }

    /// <summary>How many pictures (1 to 4).</summary>
    public int N { get; init; } = 1;

    public string? NegativePrompt { get; init; }

    public int? NumInferenceSteps { get; init; }

    public double? GuidanceScale { get; init; }

    public long? Seed { get; init; }

    /// <summary>The model's alias; the server's image model when left out.</summary>
    public string? Model { get; init; }
}

public sealed record ImagesResponse
{
    public long Created { get; init; }

    public List<ImageData> Data { get; init; } = new();

    /// <summary>The pictures, decoded.</summary>
    public IReadOnlyList<byte[]> Pictures => Data.Where(d => !string.IsNullOrEmpty(d.B64Json)).Select(d => Convert.FromBase64String(d.B64Json!)).ToList();
}

public sealed record ImageData
{
    public string? B64Json { get; init; }

    /// <summary>The prompt as the model used it, when it says.</summary>
    public string? RevisedPrompt { get; init; }
}
