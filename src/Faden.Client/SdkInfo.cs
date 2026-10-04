namespace Faden;

/// <summary>This SDK, as the manifests an app reports name it.</summary>
public static class SdkInfo
{
    public const string Language = "dotnet";

    /// <summary>The SDK's version (<c>0.2.0</c>).</summary>
    public static string Version { get; } = typeof(SdkInfo).Assembly.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}"
        : "";
}
