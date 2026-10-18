using Fadenstack.Agents;
using Fadenstack.Models;

namespace Fadenstack.UI.ViewModels;

/// <summary>
/// A link the panel shows (the footer's, About's). The skin opens it through its link hook
/// (<see cref="AgentChatViewModel.LinkRequested"/>), so a host can route it through its own browser.
/// </summary>
public sealed class PanelLink
{
    internal PanelLink(string label, Uri url, UiStrings strings)
    {
        Label = label;
        Url = url;
        Name = strings.OpensInBrowser(label);
    }

    /// <summary>What it says ("Help", "Fadenstack").</summary>
    public string Label { get; }

    public Uri Url { get; }

    /// <summary>Its name for screen readers: what it says, and that it opens in the browser.</summary>
    public string Name { get; }

    public override string ToString() => Label;
}

/// <summary>
/// What About shows, in the panel's language: the agent (name, version or draft, model), the app it runs in, the SDK,
/// the server's address (shown, never sent), and the links: the organisation's Help, fadenstack.com and the developer
/// docs. Before the agent started, the agent's lines are empty; the rest is there, which helps when it cannot start.
/// </summary>
public sealed class AboutInfo
{
    /// <summary>The product, as the links and the SDK line name it (a name: never translated).</summary>
    public const string Product = "Fadenstack";

    private AboutInfo(AgentProfile? profile, AppDescription? app, string sdkVersion, Uri? server, string? refSource, UiStrings strings)
    {
        AgentName = profile?.Name?.Trim() ?? "";
        IsDraft = profile?.Draft == true;
        VersionText = profile is null ? ""
            : IsDraft ? strings.AboutDraft
            : profile.Version is { } version ? strings.AboutVersion(version)
            : "";
        Model = string.IsNullOrWhiteSpace(profile?.Model) ? null : profile!.Model!.Trim();
        ModelText = Model is null ? "" : strings.AboutModel(Model);
        AgentDetail = string.Join(" · ", new[] { VersionText, ModelText }.Where(s => s.Length > 0));
        var appName = string.Join(" ", new[] { app?.Name, app?.Version }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        AppText = appName.Length == 0 ? "" : strings.AboutApp(appName);
        SdkVersion = sdkVersion ?? "";
        SdkText = SdkVersion.Length == 0 ? $"{Product} SDK (.NET)" : $"{Product} SDK {SdkVersion} (.NET)";
        Server = AboutLinks.ServerText(server);
        ServerText = Server.Length == 0 ? "" : strings.AboutServer(Server);

        var links = new List<PanelLink>();
        if (AboutLinks.Help(server) is { } help)
        {
            links.Add(new PanelLink(strings.Help, help, strings));
        }
        links.Add(new PanelLink(Product, AboutLinks.AboutWebsite(refSource, SdkVersion), strings));
        links.Add(new PanelLink(strings.DeveloperDocs, AboutLinks.AboutDeveloperDocs(refSource, SdkVersion), strings));
        Links = links;

        var lines = new List<string>();
        if (AgentName.Length > 0)
        {
            lines.Add(strings.AboutAgent(AgentName));
        }
        lines.AddRange(new[] { VersionText, ModelText, AppText, SdkText, ServerText }.Where(s => s.Length > 0));
        Details = string.Join(Environment.NewLine, lines);
    }

    /// <summary>What About shows for an agent, in a language.</summary>
    /// <param name="profile">The agent's profile; null before it started.</param>
    /// <param name="app">The app as the host described it (<see cref="FadenstackAgent.App"/>); null leaves its line out.</param>
    /// <param name="sdkVersion">The SDK's version (<see cref="SdkInfo.Version"/>); the links carry its major.minor.</param>
    /// <param name="server">The agent's server; About shows its address and links to its Help.</param>
    /// <param name="refSource">The host's <c>&lt;app&gt;</c> for the links' <c>ref</c> (<see cref="AgentChatOptions.RefSource"/>).</param>
    /// <param name="strings">The panel's texts.</param>
    public static AboutInfo Create(AgentProfile? profile, AppDescription? app, string sdkVersion, Uri? server, string? refSource, UiStrings strings) =>
        new(profile, app, sdkVersion, server, refSource, strings ?? throw new ArgumentNullException(nameof(strings)));

    /// <summary>The agent's name; empty before it started.</summary>
    public string AgentName { get; }

    public bool HasAgent => AgentName.Length > 0;

    /// <summary>The agent is a maintainer's draft, not its published version.</summary>
    public bool IsDraft { get; }

    /// <summary>"Version 3", or "Draft"; empty when not known.</summary>
    public string VersionText { get; }

    /// <summary>The model the agent uses, when the profile says.</summary>
    public string? Model { get; }

    /// <summary>"Model: qwen3-32b", or empty.</summary>
    public string ModelText { get; }

    /// <summary>The line under the agent's name: "Version 3 · Model: qwen3-32b".</summary>
    public string AgentDetail { get; }

    /// <summary>"Runs in Word add-in 1.2.0", when the host described its app; else empty.</summary>
    public string AppText { get; }

    public bool HasApp => AppText.Length > 0;

    public string SdkVersion { get; }

    /// <summary>"Fadenstack SDK 0.2.1 (.NET)".</summary>
    public string SdkText { get; }

    /// <summary>The server's address, as shown (never sent anywhere).</summary>
    public string Server { get; }

    /// <summary>"Server: https://ai.example.internal", or empty.</summary>
    public string ServerText { get; }

    /// <summary>Help (the organisation's, when the server is known), Fadenstack (the website) and Developer docs.</summary>
    public IReadOnlyList<PanelLink> Links { get; }

    /// <summary>What "Copy details" copies: the agent, its version and model, the app, the SDK and the server, one per line.</summary>
    public string Details { get; }
}
