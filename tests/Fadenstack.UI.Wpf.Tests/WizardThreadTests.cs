using System.Windows.Controls;
using Fadenstack.Endpoints;
using Fadenstack.UI.Tests;
using Fadenstack.UI.Wizard;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// "Use this" disabled and "Checking the connection…" for good in Word's pane (15 Nov 2026): after an answer that completed on the thread pool,
/// the wizard's way back to the UI thread completed a task inside a dispatcher callback, where WPF's synchronization context is current, so the
/// await's continuation ran on the pool. Every button's CanExecuteChanged handler then threw, and one throw that escaped (a setter) left the flow
/// faulted at a busy step. These tests run the view model on a real Dispatcher, with its view, and a checker that completes on the pool.
/// </summary>
public class WizardThreadTests
{
    private static readonly Uri Ollama = new("http://localhost:11434/v1");

    [Fact]
    public void After_a_check_that_completes_on_the_pool_the_wizard_goes_on_on_the_UI_thread() => Sta.Run(() =>
    {
        using var host = new WizardHost(o => o with { Current = null });
        var ui = Thread.CurrentThread;
        var offThread = new List<string>();
        host.Wizard.PropertyChanged += (_, e) =>
        {
            if (Thread.CurrentThread != ui)
            {
                offThread.Add(e.PropertyName ?? "?");
            }
        };
        host.Show(WizardStep.OwnPick);
        host.Wizard.EndpointAddressText = Ollama.AbsoluteUri;
        var check = host.Checker.Report.Pending();

        // As a click: inside a dispatcher callback, with WPF's synchronization context current.
        host.Window.Dispatcher.BeginInvoke(new Action(() => host.Wizard.UseEndpointCommand.Execute(null)));
        host.PumpUntil(() => host.Wizard.Step == WizardStep.OwnChecking, "the check");
        ThreadPool.QueueUserWorkItem(_ => check.Complete(Reports.Usable(Ollama, "qwen3:14b")));
        host.PumpUntil(() => host.Wizard.Step == WizardStep.OwnResult, "the result");

        Assert.Empty(offThread);
        Assert.Equal("qwen3:14b", host.Wizard.SelectedModel);
        var next = host.Visible().OfType<Button>().First(b => ReferenceEquals(b.Command, host.Wizard.ContinueCommand));
        Assert.True(next.IsEnabled, "Continue stays disabled after the check");
    });

    [Fact]
    public void After_a_search_that_completes_on_the_pool_Use_this_is_enabled() => Sta.Run(() =>
    {
        using var host = new WizardHost(o => o with { Current = null, CanSearchThisComputer = true });
        var search = host.Checker.Find.Pending();
        host.Show(WizardStep.OwnAccept);
        host.Wizard.Accepted = true;
        host.Wizard.SearchConsent = true;

        host.Window.Dispatcher.BeginInvoke(new Action(() => host.Wizard.AcceptCommand.Execute(null)));
        host.PumpUntil(() => host.Wizard.Step == WizardStep.OwnSearching, "the search");
        ThreadPool.QueueUserWorkItem(_ => search.Complete(new[] { new LocalEndpoint { BaseUrl = Ollama, Preset = EndpointPreset.Ollama } }));
        host.PumpUntil(() => host.Wizard.Step == WizardStep.OwnPick, "the pick");

        Assert.Single(host.Wizard.Found);
        var useThis = host.Visible().OfType<Button>().First(b => ReferenceEquals(b.Command, host.Wizard.UseEndpointCommand));
        Assert.True(useThis.IsEnabled, "Use this stays disabled after the search");
    });

    [Fact]
    public void A_check_whose_flow_fails_on_the_UI_thread_shows_OwnFailed() => Sta.Run(() =>
    {
        using var host = new WizardHost(o => o with { Current = null });
        host.Wizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionWizardViewModel.Report))
            {
                throw new InvalidOperationException("A view that fails.");
            }
        };
        host.Show(WizardStep.OwnPick);
        host.Wizard.EndpointAddressText = Ollama.AbsoluteUri;
        var check = host.Checker.Report.Pending();

        host.Window.Dispatcher.BeginInvoke(new Action(() => host.Wizard.UseEndpointCommand.Execute(null)));
        host.PumpUntil(() => host.Wizard.Step == WizardStep.OwnChecking, "the check");
        ThreadPool.QueueUserWorkItem(_ => check.Complete(Reports.Usable(Ollama)));
        host.PumpUntil(() => host.Wizard.Step == WizardStep.OwnFailed, "the failure");

        Assert.Equal(EndpointProblem.Failed, host.Wizard.Problem);
        var retry = host.Visible().OfType<Button>().First(b => ReferenceEquals(b.Command, host.Wizard.RetryCommand));
        Assert.True(retry.IsEnabled);
    });
}
