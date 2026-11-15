using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.LocalTools;

namespace Fadenstack.UI.Tools;

/// <summary>A cloud tool: its name, its description and what this assistant may do with it.</summary>
public sealed class CloudToolViewModel
{
    internal CloudToolViewModel(UiStrings strings, CloudToolInfo tool)
    {
        Name = tool.Name;
        Description = tool.Description;
        IsOff = !tool.Enabled;
        IsForThis = tool.Enabled && tool.Allowed;
        IsNotFor = tool.Enabled && !tool.Allowed;
        StateText = IsOff ? strings.ToolsCloudOffByAdmin : IsForThis ? strings.ToolsCloudForThis : strings.ToolsCloudNotFor;
    }

    public string Name { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public string StateText { get; }

    /// <summary>For this assistant (a success chip).</summary>
    public bool IsForThis { get; }

    /// <summary>Not for this assistant (an outlined chip, the name muted).</summary>
    public bool IsNotFor { get; }

    /// <summary>Switched off by an administrator (a muted chip with a power icon, the name struck through).</summary>
    public bool IsOff { get; }

    public bool IsMuted => !IsForThis;
}

/// <summary>The cloud tools of one MCP server of the organisation, as a card with a collapsible list.</summary>
public sealed partial class CloudGroupViewModel : ObservableObject, IToolsTexts
{
    private readonly ToolsViewModel _page;

    internal CloudGroupViewModel(ToolsViewModel page, string server, IEnumerable<CloudToolInfo> tools, string filter = "")
    {
        _page = page;
        var s = page.Strings;
        Server = server;
        var all = tools.ToList();
        var serverMatches = ToolsViewModel.Contains(server, filter);
        Tools = all.Where(t => serverMatches || ToolsViewModel.Contains(t.Name, filter)).Select(t => new CloudToolViewModel(s, t)).ToList();
        var parts = new List<string>
        {
            all.Count == 1 ? s.ToolsCountOne : s.ToolsCountMany(all.Count),
            s.ToolsForThisAssistant(all.Count(t => t.Enabled && t.Allowed)),
        };
        var off = all.Count(t => !t.Enabled);
        if (off > 0)
        {
            parts.Add(s.ToolsSwitchedOffCount(off));
        }
        Summary = string.Join(" · ", parts);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        CollapseName = s.ToolsCollapse(server);
        ExpandName = s.ToolsExpand(server);
    }

    public UiStrings Strings => _page.Strings;

    public string Server { get; }

    /// <summary>"6 tools · 4 for this assistant · 1 switched off".</summary>
    public string Summary { get; }

    public IReadOnlyList<CloudToolViewModel> Tools { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollapsed))]
    private bool _isExpanded = true;

    public bool IsCollapsed => !IsExpanded;

    public string CollapseName { get; }

    public string ExpandName { get; }

    public IRelayCommand ToggleCommand { get; }
}
