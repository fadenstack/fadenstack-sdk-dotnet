using System.Text.RegularExpressions;
using Fadenstack.UI.Services;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// sdk/51 F15, the styling pass (Office's live run, 15 Nov): WPF hands a recycled item container's command
/// <c>{DisconnectedItem}</c> as its parameter, and CommunityToolkit's <c>RelayCommand&lt;T&gt;</c> throws on a parameter that is
/// not a <c>T</c>; one throw stopped the wizard's refresh and left "Use this" disabled. Every parameterized command is an
/// item command, which answers false instead: in the view models and in the skins (a skin's own command, such as the chat's
/// "jump to newest", gets its parameter from a binding too).
/// </summary>
public class ItemCommandRuleTests
{
    /// <summary>The view models (<c>src/Fadenstack.UI</c>) and every skin (<c>src/Fadenstack.UI.*</c>).</summary>
    private static IEnumerable<string> Sources
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fadenstack.slnx")))
            {
                root = root.Parent;
            }
            Assert.NotNull(root);
            var folders = Directory.GetDirectories(Path.Combine(root!.FullName, "src"), "Fadenstack.UI*");
            Assert.Contains(folders, f => f.EndsWith(Path.DirectorySeparatorChar + "Fadenstack.UI.Avalonia", StringComparison.Ordinal));
            return folders;
        }
    }

    [Fact]
    public void No_view_model_builds_a_RelayCommand_of_T_outside_the_item_commands()
    {
        var offenders = Sources.SelectMany(s => Directory.GetFiles(s, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !f.EndsWith("ItemCommands.cs", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"new\s+(Async)?RelayCommand<"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "Use ItemCommand<T> / AsyncItemCommand<T> in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void An_item_command_answers_false_for_a_parameter_of_another_type_and_runs_nothing()
    {
        var ran = 0;
        var command = new ItemCommand<string>(_ => ran++, _ => true);

        Assert.False(command.CanExecute(new object()));
        command.Execute(new object());
        Assert.Equal(0, ran);
        Assert.True(command.CanExecute("a"));
    }
}
