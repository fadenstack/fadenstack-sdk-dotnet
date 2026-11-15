using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Fadenstack.Endpoints;
using Fadenstack.UI.Wizard;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// "Use this" stayed disabled in Word's pane (WPF on .NET Framework): the view model's Refresh() raised CanExecuteChanged for each command in
/// one loop, WPF asked a command of an item button with {DisconnectedItem} (the parameter of a recycled item container,
/// <see cref="BindingOperations.DisconnectedSource"/>), a RelayCommand&lt;T&gt; threw in CanExecute because that is not a T, the throw stopped
/// the loop, and the commands after it (UseEndpointCommand) were never asked again.
/// </summary>
public class WizardCommandTests
{
    [Fact]
    public void A_command_with_a_parameter_answers_false_for_the_item_of_a_recycled_container() => Sta.Run(() =>
    {
        using var host = new WizardHost();
        host.Show(WizardStep.OwnPick);
        var disconnected = BindingOperations.DisconnectedSource;

        Assert.False(host.Wizard.SelectPresetCommand.CanExecute(disconnected));
        Assert.False(host.Wizard.SelectFoundCommand.CanExecute(disconnected));
        Assert.False(host.Wizard.SignInWithPasswordCommand.CanExecute(disconnected));
        Assert.False(host.Wizard.SubmitKeyCommand.CanExecute(disconnected));
        // Asked to run with it, they do nothing.
        host.Wizard.SelectPresetCommand.Execute(disconnected);
        host.Wizard.SelectFoundCommand.Execute(disconnected);
        Assert.Equal(WizardStep.OwnPick, host.Wizard.Step);
    });

    [Fact]
    public void A_command_with_a_parameter_still_runs_for_an_item_of_its_type() => Sta.Run(() =>
    {
        using var host = new WizardHost();
        host.Show(WizardStep.OwnPick);

        var preset = host.Wizard.Presets.First(p => p.BaseUrl is not null);
        Assert.True(host.Wizard.SelectPresetCommand.CanExecute(preset));
        Assert.False(host.Wizard.SelectPresetCommand.CanExecute(null));
        host.Wizard.SelectPresetCommand.Execute(preset);
        Assert.Equal(preset.BaseUrl!.AbsoluteUri, host.Wizard.EndpointAddressText);
    });

    [Fact]
    public void Use_this_is_enabled_at_the_pick_after_the_found_list_changed_and_the_step_changed() => Sta.Run(() =>
    {
        using var host = new WizardHost(o => o with { Current = null });
        host.Set(nameof(ConnectionWizardViewModel.Found), new[] { new LocalEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Preset = EndpointPreset.Ollama } });
        host.Show(WizardStep.OwnPick);
        var recycled = host.Visible().OfType<Button>().Where(b => b.CommandParameter is LocalEndpoint).ToList();
        Assert.NotEmpty(recycled);
        host.Show(WizardStep.OwnAccept);
        Assert.False(UseThis(host).IsEnabled);

        // A second search: the items are new and the old item buttons are recycled. The test keeps them alive, as a recycling panel does, and
        // gives each the parameter WPF on .NET Framework gives a recycled container's binding: {DisconnectedItem} (WPF on .NET 10 gives null).
        host.Set(nameof(ConnectionWizardViewModel.Found), new[]
        {
            new LocalEndpoint { BaseUrl = new Uri("http://localhost:1234/v1"), Preset = EndpointPreset.LMStudio },
            new LocalEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Preset = EndpointPreset.Ollama },
        });
        host.Pump();
        foreach (var button in recycled)
        {
            try
            {
                button.CommandParameter = BindingOperations.DisconnectedSource;
            }
            catch (ArgumentException)
            {
                // Before the fix the button's own CanExecute throws as soon as it is given the parameter; the value is set all the same.
            }
        }

        // The step changes to the pick: every command is asked again.
        Exception? thrown = null;
        try
        {
            host.Show(WizardStep.OwnPick);
        }
        catch (Exception e)
        {
            thrown = e;
        }
        host.Pump();

        Assert.True(host.Wizard.UseEndpointCommand.CanExecute(null));
        Assert.True(UseThis(host).IsEnabled, "Use this is disabled although its command can execute");
        Assert.Null(thrown);
    });

    private static Button UseThis(WizardHost host) => host.Tree().OfType<Button>().First(b => b.Content as string == host.Strings.WizardOwnUse);
}
