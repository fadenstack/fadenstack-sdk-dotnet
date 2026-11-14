using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Fadenstack.Endpoints;
using Fadenstack.UI.Wizard;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// The wizard's screen-reader contract: in every step and both languages, every button, check box, text box, combo box and
/// scrolling text that is on screen has an explicit name from the view model's texts; the errors and the waiting line are
/// live regions.
/// </summary>
public class WizardAccessibilityTests
{
    private static string? NameOf(Control control) =>
        AutomationProperties.GetName(control) is { Length: > 0 } name ? name : null;

    private static IEnumerable<Control> Named(WizardHost host) => host.Visible().Where(c =>
        c is Button or ToggleButton or TextBox or CheckBox or ComboBox || c is ScrollViewer { Focusable: true });

    [AvaloniaTheory]
    [MemberData(nameof(WizardViewTests.StepsInBothLanguages), MemberType = typeof(WizardViewTests))]
    public async Task Every_input_and_button_on_screen_has_a_name_in_every_step(WizardStep step, string language)
    {
        using var host = await WizardHost.OpenAsync(o => o with { Current = new ConnectionResult
        {
            Mode = ConnectionMode.Endpoint, Endpoint = new ModelEndpoint { BaseUrl = new Uri("https://api.openai.com/v1"), Model = "m" },
        } }, language);
        host.Set(nameof(ConnectionWizardViewModel.Found), new[] { new LocalEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Preset = EndpointPreset.Ollama } });
        host.Set(nameof(ConnectionWizardViewModel.Models), new[] { "m1" });
        await host.ShowAsync(step);

        var failures = new List<string>();
        foreach (var control in Named(host))
        {
            var what = $"{step}/{language}: {control.GetType().Name} '{string.Join(' ', control.Classes)}' {control.Name}";
            if (string.IsNullOrWhiteSpace(NameOf(control)))
            {
                failures.Add(what + " has no automation name");
            }
            else if (control is Button { Content: string shown } && NameOf(control) != shown)
            {
                failures.Add(what + $" is named '{NameOf(control)}' but says '{shown}'");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [AvaloniaFact]
    public async Task The_steps_between_them_check_a_good_number_of_controls()
    {
        var counted = 0;
        foreach (var step in Enum.GetValues<WizardStep>())
        {
            using var host = await WizardHost.OpenAsync(o => o with { Current = new ConnectionResult { Mode = ConnectionMode.Server, Server = new Uri("https://ai.example.test/") } });
            await host.ShowAsync(step);
            counted += Named(host).Count();
        }
        Assert.True(counted > 40, $"only {counted} controls were checked");
    }

    [AvaloniaFact]
    public async Task The_banners_the_waiting_line_and_the_progress_are_live_or_named()
    {
        using var host = await WizardHost.OpenAsync();
        await host.ShowAsync(WizardStep.Unreachable);
        var live = host.Visible().Where(c => AutomationProperties.GetLiveSetting(c) == AutomationLiveSetting.Assertive).ToList();
        Assert.NotEmpty(live);

        host.Set(nameof(ConnectionWizardViewModel.UserCode), "BDWP-HTQX");
        await host.ShowAsync(WizardStep.PairCode);
        var waiting = host.Visible().OfType<TextBlock>().First(t => t.Text == host.Wizard.WaitingText);
        // The countdown changes every second: not a live region, or a screen reader would read it out each time.
        Assert.NotEqual(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(waiting));
        Assert.NotEqual(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(waiting));
        var code = host.Find<SelectableTextBlock>("CodeText");
        Assert.Equal(host.Strings.WizardCodeName, AutomationProperties.GetName(code));

        await host.ShowAsync(WizardStep.Checking);
        Assert.All(host.Visible().OfType<ProgressBar>(), bar => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(bar))));
    }
}
