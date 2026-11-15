using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Fadenstack.Endpoints;
using Fadenstack.UI.Tests;
using Fadenstack.UI.Wizard;
using Xunit;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// The WPF wizard went on off the UI thread after an answer that completed on the thread pool (Word's pane, 15 Nov 2026); Avalonia's dispatcher
/// has a synchronization context of its own too. These tests run the view model on Avalonia's dispatcher, with its view, and a checker that
/// completes on the pool: the wizard goes on on the UI thread, and its buttons follow.
/// </summary>
public class WizardThreadTests
{
    private static readonly Uri Ollama = new("http://localhost:11434/v1");

    [AvaloniaFact]
    public async Task After_a_check_that_completes_on_the_pool_the_wizard_goes_on_on_the_UI_thread()
    {
        using var host = await WizardHost.OpenAsync(o => o with { Current = null });
        var ui = Thread.CurrentThread;
        var offThread = new List<string>();
        host.Wizard.PropertyChanged += (_, e) =>
        {
            if (Thread.CurrentThread != ui)
            {
                offThread.Add(e.PropertyName ?? "?");
            }
        };
        await host.ShowAsync(WizardStep.OwnPick);
        host.Wizard.EndpointAddressText = Ollama.AbsoluteUri;
        var check = host.Checker.Report.Pending();

        Dispatcher.UIThread.Post(() => host.Wizard.UseEndpointCommand.Execute(null)); // as a click: inside a dispatcher job
        await host.PumpUntilAsync(() => host.Wizard.Step == WizardStep.OwnChecking, "the check");
        ThreadPool.QueueUserWorkItem(_ => check.Complete(Reports.Usable(Ollama, "qwen3:14b")));
        await host.PumpUntilAsync(() => host.Wizard.Step == WizardStep.OwnResult, "the result");

        Assert.Empty(offThread);
        Assert.Equal("qwen3:14b", host.Wizard.SelectedModel);
        var next = host.Visible().OfType<Button>().First(b => ReferenceEquals(b.Command, host.Wizard.ContinueCommand));
        Assert.True(next.IsEffectivelyEnabled, "Continue stays disabled after the check");
    }

    [AvaloniaFact]
    public async Task After_a_search_that_completes_on_the_pool_Use_this_is_enabled()
    {
        using var host = await WizardHost.OpenAsync(o => o with { Current = null, CanSearchThisComputer = true });
        var search = host.Checker.Find.Pending();
        await host.ShowAsync(WizardStep.OwnAccept);
        host.Wizard.Accepted = true;
        host.Wizard.SearchConsent = true;

        Dispatcher.UIThread.Post(() => host.Wizard.AcceptCommand.Execute(null));
        await host.PumpUntilAsync(() => host.Wizard.Step == WizardStep.OwnSearching, "the search");
        ThreadPool.QueueUserWorkItem(_ => search.Complete(new[] { new LocalEndpoint { BaseUrl = Ollama, Preset = EndpointPreset.Ollama } }));
        await host.PumpUntilAsync(() => host.Wizard.Step == WizardStep.OwnPick, "the pick");

        Assert.Single(host.Wizard.Found);
        var useThis = host.Visible().OfType<Button>().First(b => ReferenceEquals(b.Command, host.Wizard.UseEndpointCommand));
        Assert.True(useThis.IsEffectivelyEnabled, "Use this stays disabled after the search");
    }

    [AvaloniaFact]
    public async Task A_check_whose_flow_fails_on_the_UI_thread_shows_OwnFailed()
    {
        using var host = await WizardHost.OpenAsync(o => o with { Current = null });
        host.Wizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionWizardViewModel.Report))
            {
                throw new InvalidOperationException("A view that fails.");
            }
        };
        await host.ShowAsync(WizardStep.OwnPick);
        host.Wizard.EndpointAddressText = Ollama.AbsoluteUri;
        var check = host.Checker.Report.Pending();

        Dispatcher.UIThread.Post(() => host.Wizard.UseEndpointCommand.Execute(null));
        await host.PumpUntilAsync(() => host.Wizard.Step == WizardStep.OwnChecking, "the check");
        ThreadPool.QueueUserWorkItem(_ => check.Complete(Reports.Usable(Ollama)));
        await host.PumpUntilAsync(() => host.Wizard.Step == WizardStep.OwnFailed, "the failure");

        Assert.Equal(EndpointProblem.Failed, host.Wizard.Problem);
        var retry = host.Visible().OfType<Button>().First(b => ReferenceEquals(b.Command, host.Wizard.RetryCommand));
        Assert.True(retry.IsEffectivelyEnabled);
    }
}
