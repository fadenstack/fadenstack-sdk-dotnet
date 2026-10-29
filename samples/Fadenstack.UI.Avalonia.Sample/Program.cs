using Avalonia;

namespace Fadenstack.UI.Avalonia.Sample;

/// <summary>
///   Fadenstack.UI.Avalonia.Sample.exe --turns 1000                  open the chat on a synthetic session of 1,000 turns
///   Fadenstack.UI.Avalonia.Sample.exe --turns 10000 --bench [out.md]  run the long-session harness on it, print the numbers, exit
///   ... --only stream                                              just the streaming scenario
/// With neither, the chat opens empty on the demo gateway (try "stream long answer").
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Options = Options.Parse(args);
        return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }

    public static Options Options { get; private set; } = new();
}

internal sealed record Options(int Turns = 0, bool Bench = false, string? Output = null, string? Only = null)
{
    public static Options Parse(string[] args)
    {
        var turns = Array.IndexOf(args, "--turns") is var t and >= 0 && t + 1 < args.Length ? int.Parse(args[t + 1]) : 0;
        var bench = Array.IndexOf(args, "--bench");
        var only = Array.IndexOf(args, "--only") is var o and >= 0 && o + 1 < args.Length ? args[o + 1] : null;
        return new Options(turns, bench >= 0, bench >= 0 && bench + 1 < args.Length && !args[bench + 1].StartsWith("--") ? args[bench + 1] : null, only);
    }
}
