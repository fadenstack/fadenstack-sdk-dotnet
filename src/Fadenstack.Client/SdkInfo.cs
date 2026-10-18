using System.Reflection;

namespace Fadenstack;

/// <summary>This SDK, as the manifests an app reports name it.</summary>
public static class SdkInfo
{
    public const string Language = "dotnet";

    /// <summary>The SDK's version as released (<c>0.2.1</c>, <c>0.2.0-rc.3</c>): the package's version, without build metadata.</summary>
    public static string Version { get; } = Of(typeof(SdkInfo).Assembly);

    /// <summary>An assembly's informational version (the package's) without its <c>+commit</c>; else its version's first three numbers.</summary>
    internal static string Of(Assembly assembly) =>
        Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, assembly.GetName().Version);

    internal static string Parse(string? informational, Version? fallback)
    {
        var plus = informational?.IndexOf('+') ?? -1;
        var version = (plus >= 0 ? informational!.Substring(0, plus) : informational)?.Trim();
        if (!string.IsNullOrEmpty(version))
        {
            return version!;
        }
        return fallback is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : "";
    }
}
