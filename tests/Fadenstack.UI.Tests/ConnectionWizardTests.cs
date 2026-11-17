using System.Collections;
using System.Net;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Endpoints;
using Fadenstack.UI.Wizard;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// sdk/51 I15 §1-§3: each arrow of the wizard's three state diagrams is a test, driven as a skin would drive it (StartAsync
/// and the commands) against fakes; then the policy, the address rule, the app steps, stale answers and the rule that no
/// secret stays in the view model.
/// </summary>
public class ConnectionWizardTests
{
    private static readonly Uri Org = new("https://ai.example.test/");
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.5);
    private const string Address = "https://ai.example.test";

    private static SignedInUser Ana { get; } = new(Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"), "ana@example.test", "Ana");

    private static PairingApproval Approve(params string[] agents) =>
        new(Ana, agents.Select(a => new AgentSignIn(a, AgentAccess.Approved)).ToList());

    private static ConnectionResult ServerConnection { get; } = new()
    {
        Mode = ConnectionMode.Server, Server = Org, Agents = new[] { new AgentSignIn("main-agent", AgentAccess.Approved) },
    };

    private static ConnectionResult EndpointConnection { get; } = new()
    {
        Mode = ConnectionMode.Endpoint,
        Endpoint = new ModelEndpoint { BaseUrl = new Uri("https://api.openai.com/v1"), Model = "gpt-test" },
    };

    private static WizardRig Rig(Func<WizardOptions, WizardOptions>? configure = null) => new(configure);

    private static WizardRig Settings(ConnectionResult? current = null) =>
        Rig(o => o with { Entry = WizardEntry.Settings, Current = current ?? ServerConnection });

    /// <summary>First run, "Connect to my organisation", the address typed and submitted: the code is shown.</summary>
    private static WizardRig PairCode(Func<WizardOptions, WizardOptions>? configure = null, Action<WizardRig>? before = null)
    {
        var rig = Rig(configure);
        before?.Invoke(rig);
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);
        Assert.Equal(WizardStep.PairCode, rig.Wizard.Step);
        return rig;
    }

    /// <summary>First run, "Use my own AI", the terms accepted: the pick (after the search when asked for).</summary>
    private static WizardRig OwnPick(Func<WizardOptions, WizardOptions>? configure = null, Action<WizardRig>? before = null,
        bool search = false)
    {
        var rig = Rig(configure);
        before?.Invoke(rig);
        rig.Start();
        rig.Press(rig.Wizard.ChooseOwnAiCommand);
        rig.Wizard.Accepted = true;
        rig.Wizard.SearchConsent = search;
        rig.Press(rig.Wizard.AcceptCommand);
        return rig;
    }

    private static void Pick(WizardRig rig, EndpointPreset preset) => rig.Press(rig.Wizard.SelectPresetCommand, preset);

    private static void Type(WizardRig rig, string address) => rig.Wizard.EndpointAddressText = address;

    private static void UseEndpoint(WizardRig rig) => rig.Press(rig.Wizard.UseEndpointCommand);

    // ===================================================================================================================
    // §1 Entry
    // ===================================================================================================================

    [Fact]
    public void FirstRun_to_Choose_when_the_app_has_no_steps()
    {
        using var rig = Rig();

        rig.Start();

        Assert.Equal(WizardStep.Choose, rig.Wizard.Step);
        Assert.Equal(WizardOutcome.None, rig.Wizard.Outcome);
        Assert.False(rig.Wizard.IsBusy);
    }

    [Fact]
    public void BeforeChoice_to_Choose_when_the_app_step_says_true()
    {
        var step = new FakeStep(WizardPoint.BeforeChoice);
        var running = step.Script.Pending();
        using var rig = Rig(o => o with { Steps = new[] { step } });

        rig.Start();

        Assert.Equal(WizardStep.BeforeChoice, rig.Wizard.Step);
        Assert.Same(step, rig.Wizard.AppStep);
        Assert.True(rig.Wizard.IsBusy);
        Assert.Equal(WizardPoint.BeforeChoice, Assert.Single(step.Contexts).Point);

        running.Complete(true);

        Assert.Equal(WizardStep.Choose, rig.Wizard.Step);
        Assert.Null(rig.Wizard.AppStep);
        Assert.False(rig.Wizard.AppStepRefused);
    }

    [Fact]
    public void BeforeChoice_to_Finished_Closed_when_the_app_step_says_false()
    {
        var step = new FakeStep(WizardPoint.BeforeChoice);
        step.Script.Returns(false);
        using var rig = Rig(o => o with { Steps = new[] { step } });

        rig.Start();

        Assert.Equal(WizardStep.Finished, rig.Wizard.Step);
        Assert.Equal(WizardOutcome.Closed, rig.Wizard.Outcome);
        Assert.Equal(new[] { WizardOutcome.Closed }, rig.Completed);
        Assert.Empty(rig.Store.Saved);
    }

    [Fact]
    public void BeforeChoice_to_Finished_Closed_when_the_app_step_throws()
    {
        var step = new FakeStep(WizardPoint.BeforeChoice);
        step.Script.Throws(new InvalidOperationException("licence screen failed"));
        using var rig = Rig(o => o with { Steps = new[] { step } });

        rig.Start();

        Assert.Equal(WizardStep.Finished, rig.Wizard.Step);
        Assert.Equal(WizardOutcome.Closed, rig.Wizard.Outcome);
    }

    [Theory]
    [InlineData(WizardEntry.ServerKnown, false)]
    [InlineData(WizardEntry.ReSignIn, true)]
    public void A_known_server_goes_straight_to_PairStarting(WizardEntry entry, bool signInEnded)
    {
        var start = default(PendingAnswer<PairingTicket>);
        using var rig = Rig(o => o with { Entry = entry, Server = Org });
        start = rig.Sign.Start.Pending();

        rig.Start();

        Assert.Equal(WizardStep.PairStarting, rig.Wizard.Step);
        Assert.Equal(signInEnded, rig.Wizard.SignInEnded);
        Assert.Equal(Org, rig.Wizard.ServerUrl);
        Assert.Equal(new[] { Org }, rig.Sign.Started);
        Assert.Empty(rig.Sign.Checked); // a start counts against the server's limit; the wizard never probes with one, and does not check again
        Assert.False(start.IsCompleted);
    }

    [Theory]
    [InlineData(WizardEntry.ServerKnown)]
    [InlineData(WizardEntry.ReSignIn)]
    public void A_known_entry_without_a_server_goes_to_ServerAddress(WizardEntry entry)
    {
        using var rig = Rig(o => o with { Entry = entry });

        rig.Start();

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.Empty(rig.Sign.Started);
        Assert.Equal(entry == WizardEntry.ReSignIn, rig.Wizard.SignInEnded);
    }

    [Fact]
    public void A_policy_server_wins_over_the_apps_server_on_a_known_entry()
    {
        var policy = new MachinePolicy { Server = new Uri("https://ai.corp.test/"), Standalone = PolicyAccess.Forbidden };
        using var rig = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org, Policy = policy });
        rig.Sign.Start.Pending();

        rig.Start();

        Assert.Equal(new[] { new Uri("https://ai.corp.test/") }, rig.Sign.Started);
    }

    [Fact]
    public void Settings_entry_goes_to_Settings()
    {
        using var rig = Settings();

        rig.Start();

        Assert.Equal(WizardStep.Settings, rig.Wizard.Step);
        Assert.Same(ServerConnection, rig.Wizard.Current);
    }

    [Fact]
    public void Settings_to_ServerAddress_on_ConnectOrganisation()
    {
        using var rig = Settings(EndpointConnection);
        rig.Start();

        rig.Press(rig.Wizard.ConnectOrganisationCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
    }

    [Fact]
    public void Settings_to_OwnAccept_on_ChangeEndpoint()
    {
        using var rig = Settings();
        rig.Start();

        rig.Press(rig.Wizard.ChangeEndpointCommand);

        Assert.Equal(WizardStep.OwnAccept, rig.Wizard.Step);
        Assert.False(rig.Wizard.Accepted);
    }

    [Fact]
    public void Settings_to_SignOut_on_SignOut()
    {
        using var rig = Settings();
        rig.Start();

        rig.Press(rig.Wizard.SignOutCommand);

        Assert.Equal(WizardStep.SignOut, rig.Wizard.Step);
        Assert.False(rig.Wizard.DeleteLocalChats);
    }

    [Fact]
    public void The_SignOut_command_is_not_available_without_a_current_connection()
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.Settings, Current = null });
        rig.Start();

        Assert.Equal(WizardStep.Settings, rig.Wizard.Step);
        WizardRig.Unavailable(rig.Wizard.SignOutCommand);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignOut_to_Finished_SignedOut_on_ConfirmSignOut(bool deleteLocalChats)
    {
        using var rig = Settings();
        rig.Start();
        rig.Press(rig.Wizard.SignOutCommand);
        rig.Wizard.DeleteLocalChats = deleteLocalChats;

        rig.Press(rig.Wizard.ConfirmSignOutCommand);

        Assert.Equal(WizardStep.Finished, rig.Wizard.Step);
        Assert.Equal(WizardOutcome.SignedOut, rig.Wizard.Outcome);
        Assert.Equal(new[] { deleteLocalChats }, rig.Store.SignOuts);
        Assert.Empty(rig.Keys.Removed); // a server sign-in has no endpoint key
        Assert.Equal(new[] { WizardOutcome.SignedOut }, rig.Completed);
    }

    [Fact]
    public void Removing_an_endpoint_removes_its_key_from_the_key_store()
    {
        using var rig = Settings(EndpointConnection);
        rig.Start();
        rig.Press(rig.Wizard.SignOutCommand);

        rig.Press(rig.Wizard.ConfirmSignOutCommand);

        Assert.Equal(WizardOutcome.SignedOut, rig.Wizard.Outcome);
        Assert.Equal(new[] { new Uri("https://api.openai.com/v1") }, rig.Keys.Removed);
        Assert.Equal(new[] { false }, rig.Store.SignOuts);
    }

    [Fact]
    public void SignOut_to_Settings_on_Cancel_and_the_chat_box_starts_unticked_again()
    {
        using var rig = Settings();
        rig.Start();
        rig.Press(rig.Wizard.SignOutCommand);
        rig.Wizard.DeleteLocalChats = true;

        rig.Press(rig.Wizard.CancelCommand);
        Assert.Equal(WizardStep.Settings, rig.Wizard.Step);
        Assert.Empty(rig.Store.SignOuts);

        rig.Press(rig.Wizard.SignOutCommand);
        Assert.False(rig.Wizard.DeleteLocalChats);
    }

    // ---- The choice ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Choose_to_ServerAddress()
    {
        using var rig = Rig();
        rig.Start();

        rig.Press(rig.Wizard.ChooseServerCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
    }

    [Fact]
    public void Choose_to_OwnAccept()
    {
        using var rig = Rig();
        rig.Start();

        rig.Press(rig.Wizard.ChooseOwnAiCommand);

        Assert.Equal(WizardStep.OwnAccept, rig.Wizard.Step);
    }

    [Fact]
    public void Choose_to_Finished_Demo()
    {
        using var rig = Rig(o => o with { OffersDemo = true });
        rig.Start();

        rig.Press(rig.Wizard.ChooseDemoCommand);

        Assert.Equal(WizardStep.Finished, rig.Wizard.Step);
        Assert.Equal(WizardOutcome.Demo, rig.Wizard.Outcome);
        Assert.Equal(new[] { WizardOutcome.Demo }, rig.Completed);
        Assert.Empty(rig.Store.Saved);
    }

    [Fact]
    public void Choose_to_Finished_Closed_on_Close_and_nothing_goes_on_after_it()
    {
        using var rig = Rig();
        rig.Start();

        rig.Press(rig.Wizard.CloseCommand);

        Assert.Equal(WizardStep.Finished, rig.Wizard.Step);
        Assert.Equal(WizardOutcome.Closed, rig.Wizard.Outcome);
        WizardRig.Unavailable(rig.Wizard.CloseCommand);
        WizardRig.Unavailable(rig.Wizard.ChooseServerCommand);
    }

    [Fact]
    public void Without_the_demo_the_demo_is_not_offered()
    {
        using var rig = Rig();
        rig.Start();

        WizardRig.Unavailable(rig.Wizard.ChooseDemoCommand);
    }

    [Fact]
    public void Only_the_server_path_offered_goes_straight_to_ServerAddress()
    {
        using var rig = Rig(o => o with { OffersOwnAi = false });

        rig.Start();

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.False(rig.Wizard.CanChooseOwnAi);
        WizardRig.Unavailable(rig.Wizard.BackCommand, "there is no choice to go back to");
    }

    [Fact]
    public void Only_own_AI_offered_goes_straight_to_OwnAccept()
    {
        using var rig = Rig(o => o with { OffersServer = false });

        rig.Start();

        Assert.Equal(WizardStep.OwnAccept, rig.Wizard.Step);
    }

    [Fact]
    public void Own_AI_forbidden_by_the_policy_leaves_one_path_and_goes_straight_to_ServerAddress()
    {
        using var rig = Rig(o => o with { Policy = new MachinePolicy { Standalone = PolicyAccess.Forbidden } });

        rig.Start();

        Assert.False(rig.Wizard.CanChooseOwnAi);
        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
    }

    [Fact]
    public void One_path_and_the_demo_still_shows_the_choice()
    {
        using var rig = Rig(o => o with { OffersOwnAi = false, OffersDemo = true });

        rig.Start();

        Assert.Equal(WizardStep.Choose, rig.Wizard.Step);
    }

    [Fact]
    public void Back_from_the_only_path_in_Settings_returns_to_Settings()
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.Settings, Current = ServerConnection, OffersOwnAi = false });
        rig.Start();
        rig.Press(rig.Wizard.ConnectOrganisationCommand);

        rig.Press(rig.Wizard.BackCommand);

        Assert.Equal(WizardStep.Settings, rig.Wizard.Step);
    }

    [Fact]
    public void Back_from_ServerAddress_returns_to_the_choice()
    {
        using var rig = Rig();
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);

        rig.Press(rig.Wizard.BackCommand);

        Assert.Equal(WizardStep.Choose, rig.Wizard.Step);
    }

    // ---- The machine policy ---------------------------------------------------------------------------------------------

    private static readonly Uri CorpServer = new("https://ai.corp.test/");

    private static MachinePolicy CorpPolicy => new() { Server = CorpServer, Standalone = PolicyAccess.Forbidden };

    [Fact]
    public void A_policy_server_opens_path_A_with_the_address_locked()
    {
        using var rig = Rig(o => o with { Policy = CorpPolicy, OffersDemo = true });
        rig.Sign.Start.Pending();

        rig.Start();

        Assert.Equal(WizardStep.PairStarting, rig.Wizard.Step);
        Assert.Equal(new[] { CorpServer }, rig.Sign.Started);
        Assert.True(rig.Wizard.ServerLocked);
        Assert.False(rig.Wizard.CanChooseDemo);
        Assert.False(rig.Wizard.CanChooseOwnAi);
        rig.Wizard.ServerAddressText = "https://other.example.test";
        Assert.Equal(CorpServer.AbsoluteUri, rig.Wizard.ServerAddressText);
    }

    [Fact]
    public void A_policy_server_cannot_be_changed_from_any_failure_or_the_code()
    {
        using var rig = Rig(o => o with { Policy = CorpPolicy });
        rig.Sign.Start.Returns(Tickets.Make(rig.Clock.Now.AddMinutes(10)));
        rig.Start();
        Assert.Equal(WizardStep.PairCode, rig.Wizard.Step);
        WizardRig.Unavailable(rig.Wizard.ChangeServerCommand);

        rig.Wizard.CancelCommand.Execute(null); // back to the address: it shows the policy's server, locked
        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.Equal(CorpServer.AbsoluteUri, rig.Wizard.ServerAddressText);
        rig.Wizard.ServerAddressText = "https://other.example.test";
        Assert.Equal(CorpServer.AbsoluteUri, rig.Wizard.ServerAddressText);
    }

    [Fact]
    public void A_policy_server_hides_the_change_server_command_at_a_failure()
    {
        using var rig = Rig(o => o with { Policy = CorpPolicy });
        rig.Sign.Start.Throws(new FadenstackUnreachableException("down"));

        rig.Start();

        Assert.Equal(WizardStep.StartFailed, rig.Wizard.Step);
        WizardRig.Unavailable(rig.Wizard.ChangeServerCommand);
        Assert.True(rig.Wizard.RetryCommand.CanExecute(null));
    }

    [Fact]
    public void Standalone_forbidden_hides_own_AI_in_Settings_too()
    {
        using var rig = Rig(o => o with
        {
            Entry = WizardEntry.Settings, Current = ServerConnection, Policy = new MachinePolicy { Standalone = PolicyAccess.Forbidden },
        });
        rig.Start();

        Assert.False(rig.Wizard.CanChooseOwnAi);
        WizardRig.Unavailable(rig.Wizard.ChangeEndpointCommand);
        Assert.True(rig.Wizard.ConnectOrganisationCommand.CanExecute(null));
    }

    [Fact]
    public void The_endpoints_list_narrows_the_presets()
    {
        var policy = new MachinePolicy { Endpoints = new[] { "http://localhost:11434" } };
        using var rig = Rig(o => o with { Policy = policy });

        // A preset with a base of its own stays only when the base is listed; one without (Azure, LiteLLM) has the person's
        // address, which the typed address is checked against.
        Assert.Equal(new[] { "azure-openai", "ollama", "litellm" }, rig.Wizard.Presets.Select(p => p.Id));
    }

    [Fact]
    public void The_endpoints_list_narrows_what_the_search_found()
    {
        var policy = new MachinePolicy { Endpoints = new[] { "http://localhost:11434" } };
        var ollama = new LocalEndpoint { BaseUrl = new Uri("http://localhost:11434/v1"), Preset = EndpointPreset.Ollama };
        var lmStudio = new LocalEndpoint { BaseUrl = new Uri("http://localhost:1234/v1"), Preset = EndpointPreset.LMStudio };
        using var rig = OwnPick(o => o with { Policy = policy }, r => r.Checker.Find.Returns(new[] { ollama, lmStudio }), search: true);

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.Equal(new[] { ollama }, rig.Wizard.Found);
    }

    [Fact]
    public void A_typed_address_the_endpoints_list_does_not_have_is_not_allowed()
    {
        var policy = new MachinePolicy { Endpoints = new[] { "http://localhost:11434" } };
        using var rig = OwnPick(o => o with { Policy = policy });
        Type(rig, "https://api.other.example.test/v1");

        UseEndpoint(rig);

        Assert.Equal(AddressProblem.NotAllowed, rig.Wizard.AddressError);
        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.Empty(rig.Checker.Checked);
    }

    [Fact]
    public void A_typed_address_the_endpoints_list_has_is_checked()
    {
        var policy = new MachinePolicy { Endpoints = new[] { "https://api.example.test/v1" } };
        using var rig = OwnPick(o => o with { Policy = policy });
        Type(rig, "https://api.example.test/v1");

        UseEndpoint(rig);

        Assert.Equal(AddressProblem.None, rig.Wizard.AddressError);
        Assert.Equal(WizardStep.OwnResult, rig.Wizard.Step);
    }

    [Fact]
    public void PlainHttp_forbidden_refuses_http_to_the_local_network()
    {
        using var rig = OwnPick(o => o with { Policy = new MachinePolicy { PlainHttp = PolicyAccess.Forbidden } },
            r => r.Checker.Places["192.168.1.20"] = EndpointPlace.LocalNetwork);
        Type(rig, "http://192.168.1.20:11434/v1");

        UseEndpoint(rig);

        Assert.Equal(AddressProblem.PlainHttpForbidden, rig.Wizard.AddressError);
        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.Empty(rig.Checker.Checked);
    }

    [Fact]
    public void PlainHttp_forbidden_refuses_a_key_over_http_to_this_computer()
    {
        using var rig = OwnPick(o => o with { Policy = new MachinePolicy { PlainHttp = PolicyAccess.Forbidden } });
        Type(rig, "http://127.0.0.1:4000/v1");
        rig.Wizard.NeedsKey = true;

        UseEndpoint(rig);

        Assert.Equal(AddressProblem.PlainHttpForbidden, rig.Wizard.AddressError);
        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
    }

    [Fact]
    public void PlainHttp_forbidden_still_allows_http_without_a_key_to_this_computer()
    {
        using var rig = OwnPick(o => o with { Policy = new MachinePolicy { PlainHttp = PolicyAccess.Forbidden } });
        Pick(rig, EndpointPreset.Ollama);

        UseEndpoint(rig);

        Assert.Equal(WizardStep.OwnResult, rig.Wizard.Step);
        Assert.Single(rig.Checker.Checked);
    }

    // ===================================================================================================================
    // §2 Path A
    // ===================================================================================================================

    [Fact]
    public void ServerAddress_to_AppPrecondition_on_submit()
    {
        var step = new FakeStep(WizardPoint.BeforeServerCheck);
        var running = step.Script.Pending();
        using var rig = Rig(o => o with { Steps = new[] { step } });
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;

        rig.Press(rig.Wizard.SubmitServerCommand);

        Assert.Equal(WizardStep.AppPrecondition, rig.Wizard.Step);
        Assert.Same(step, rig.Wizard.AppStep);
        var context = Assert.Single(step.Contexts);
        Assert.Equal(WizardPoint.BeforeServerCheck, context.Point);
        Assert.Equal(Org, context.Server);
        Assert.Empty(rig.Sign.Checked);
        Assert.False(running.IsCompleted);
    }

    [Fact]
    public void AppPrecondition_to_ServerAddress_when_the_permission_is_refused()
    {
        var step = new FakeStep(WizardPoint.BeforeServerCheck);
        step.Script.Returns(false);
        using var rig = Rig(o => o with { Steps = new[] { step } });
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;

        rig.Press(rig.Wizard.SubmitServerCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.True(rig.Wizard.AppStepRefused);
        Assert.Empty(rig.Sign.Checked);
        Assert.Null(rig.Wizard.AppStep);
    }

    [Fact]
    public void AppPrecondition_to_Checking_when_the_permission_is_granted()
    {
        var step = new FakeStep(WizardPoint.BeforeServerCheck);
        using var rig = Rig(o => o with { Steps = new[] { step } });
        var check = rig.Sign.Check.Pending();
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;

        rig.Press(rig.Wizard.SubmitServerCommand);

        rig.AssertPassed(WizardStep.ServerAddress, WizardStep.AppPrecondition, WizardStep.Checking);
        Assert.Equal(WizardStep.Checking, rig.Wizard.Step);
        Assert.False(rig.Wizard.AppStepRefused);
        Assert.Equal(new[] { Org }, rig.Sign.Checked);
        Assert.False(check.IsCompleted);
    }

    [Fact]
    public void ServerAddress_to_Checking_when_there_is_no_app_step()
    {
        using var rig = Rig();
        var check = rig.Sign.Check.Pending();
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;

        rig.Press(rig.Wizard.SubmitServerCommand);

        Assert.Equal(WizardStep.Checking, rig.Wizard.Step);
        Assert.True(rig.Wizard.IsBusy);
        Assert.DoesNotContain(WizardStep.AppPrecondition, rig.Steps);
    }

    [Fact]
    public void Checking_to_ServerAddress_on_Cancel()
    {
        using var rig = Rig();
        rig.Sign.Check.Pending();
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);

        rig.Press(rig.Wizard.CancelCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.Empty(rig.Store.Servers);
    }

    [Theory]
    [InlineData("unreachable", WizardStep.Unreachable)]
    [InlineData("certificate", WizardStep.CertificateNotTrusted)]
    [InlineData("not_fadenstack", WizardStep.NotFadenstack)]
    [InlineData("redirect", WizardStep.NotFadenstack)]
    [InlineData("other", WizardStep.NotFadenstack)]
    public void Checking_to_the_failure_of_the_matching_typed_exception(string kind, WizardStep expected)
    {
        using var rig = Rig();
        rig.Sign.Check.Throws(kind switch
        {
            "unreachable" => new FadenstackUnreachableException("no answer"),
            "certificate" => new ServerCertificateException("not trusted"),
            "not_fadenstack" => new FadenstackApiException(HttpStatusCode.OK, "a web page", "not_fadenstack"),
            "redirect" => new FadenstackApiException(HttpStatusCode.Found, "moved", "redirect"),
            _ => new InvalidOperationException("odd"),
        });
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;

        rig.Press(rig.Wizard.SubmitServerCommand);

        Assert.Equal(expected, rig.Wizard.Step);
        Assert.Empty(rig.Store.Servers);
        Assert.Empty(rig.Sign.Started);
    }

    [Fact]
    public void Checking_back_to_ServerAddress_when_the_health_check_says_https_is_required()
    {
        using var rig = Rig();
        rig.Sign.Check.Throws(new FadenstackHttpsRequiredException("https needed"));
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;

        rig.Press(rig.Wizard.SubmitServerCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.Equal(AddressProblem.HttpsRequired, rig.Wizard.AddressError);
    }

    [Fact]
    public void Checking_to_ServerAccepted_to_PairStarting_when_the_server_answers()
    {
        using var rig = Rig();
        var start = rig.Sign.Start.Pending();
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;

        rig.Press(rig.Wizard.SubmitServerCommand);

        rig.AssertPassed(WizardStep.Checking, WizardStep.ServerAccepted, WizardStep.PairStarting);
        Assert.Equal(WizardStep.PairStarting, rig.Wizard.Step);
        Assert.Equal(new[] { Org }, rig.Store.Servers); // saved before the pairing, so an app that restarts finds it
        Assert.Equal(new[] { Org }, rig.Sign.Started);
        Assert.False(start.IsCompleted);
    }

    [Fact]
    public void ServerAccepted_to_Finished_RestartOnServer_when_the_app_restarts_onto_the_new_server()
    {
        using var rig = Rig(o => o with { RestartsOnNewServer = true });
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;

        rig.Press(rig.Wizard.SubmitServerCommand);

        rig.AssertPassed(WizardStep.Checking, WizardStep.ServerAccepted, WizardStep.Finished);
        Assert.Equal(WizardOutcome.RestartOnServer, rig.Wizard.Outcome);
        Assert.Equal(new[] { Org }, rig.Store.Servers);
        Assert.Empty(rig.Sign.Started);
        Assert.Empty(rig.Store.Saved);
        Assert.Equal(new[] { WizardOutcome.RestartOnServer }, rig.Completed);
    }

    [Fact]
    public void Unreachable_to_Checking_on_Retry_and_on_to_the_pairing_when_it_answers()
    {
        using var rig = Rig();
        rig.Sign.Check.Throws(new FadenstackUnreachableException("no answer"));
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);
        Assert.Equal(WizardStep.Unreachable, rig.Wizard.Step);
        var again = rig.Sign.Check.Pending();

        rig.Press(rig.Wizard.RetryCommand);

        Assert.Equal(WizardStep.Checking, rig.Wizard.Step);
        Assert.Equal(2, rig.Sign.Checked.Count);
        again.Complete(true);
        Assert.Equal(WizardStep.PairCode, rig.Wizard.Step);
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("certificate")]
    [InlineData("not_fadenstack")]
    public void A_failed_check_to_ServerAddress_on_ChangeServer(string kind)
    {
        using var rig = Rig();
        rig.Sign.Check.Throws(kind switch
        {
            "unreachable" => new FadenstackUnreachableException("no answer"),
            "certificate" => new ServerCertificateException("not trusted"),
            _ => new FadenstackApiException(HttpStatusCode.OK, "a web page", "not_fadenstack"),
        });
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);
        Assert.NotEqual(WizardStep.ServerAddress, rig.Wizard.Step);

        rig.Press(rig.Wizard.ChangeServerCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
    }

    [Fact]
    public void A_certificate_failure_offers_no_retry_with_a_click_through()
    {
        using var rig = Rig();
        rig.Sign.Check.Throws(new ServerCertificateException("not trusted"));
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);

        Assert.Equal(WizardStep.CertificateNotTrusted, rig.Wizard.Step);
        WizardRig.Unavailable(rig.Wizard.RetryCommand, "a CA comes only from the policy or the app's settings");
    }

    [Fact]
    public void PairStarting_to_PairCode_shows_the_code_and_the_page()
    {
        var ticket = default(PairingTicket);
        using var rig = Rig();
        ticket = Tickets.Make(rig.Clock.Now.AddMinutes(10));
        rig.Sign.Start.Returns(ticket);
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;

        rig.Press(rig.Wizard.SubmitServerCommand);

        rig.AssertPassed(WizardStep.PairStarting, WizardStep.PairCode);
        Assert.Equal("BDWP-HTQX", rig.Wizard.UserCode);
        Assert.Equal(ticket.VerificationUriComplete, rig.Wizard.PairingPage);
        Assert.Equal(TimeSpan.FromMinutes(10), rig.Wizard.TimeLeft);
        Assert.Single(rig.Sign.Waited);
        Assert.Same(ticket, rig.Sign.Waited[0].Ticket);
    }

    [Fact]
    public void PairStarting_to_ServerAddress_on_Cancel()
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org });
        rig.Sign.Start.Pending();
        rig.Start();
        Assert.Equal(WizardStep.PairStarting, rig.Wizard.Step);

        rig.Press(rig.Wizard.CancelCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
    }

    [Theory]
    [InlineData("unreachable", StartFailure.Unreachable)]
    [InlineData("certificate", StartFailure.CertificateNotTrusted)]
    [InlineData("rate", StartFailure.RateLimited)]
    [InlineData("elsewhere", StartFailure.PageElsewhere)]
    [InlineData("server", StartFailure.ServerError)]
    [InlineData("other", StartFailure.ServerError)]
    public void PairStarting_to_StartFailed_for_each_first_request_failure(string kind, StartFailure expected)
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org });
        rig.Sign.Start.Throws(kind switch
        {
            "unreachable" => new FadenstackUnreachableException("no answer"),
            "certificate" => new ServerCertificateException("not trusted"),
            "rate" => new FadenstackRateLimitedException(HttpStatusCode.TooManyRequests, "slow", null, null),
            "elsewhere" => new FadenstackPairingPageElsewhereException("page is elsewhere"),
            "server" => new FadenstackApiException(HttpStatusCode.InternalServerError, "oops"),
            _ => new InvalidOperationException("odd"),
        });

        rig.Start();

        Assert.Equal(WizardStep.StartFailed, rig.Wizard.Step);
        Assert.Equal(expected, rig.Wizard.StartFailure);
        Assert.Equal(expected == StartFailure.RateLimited, rig.Wizard.RetryAt is not null);
    }

    [Fact]
    public void A_rate_limit_with_RetryAfter_holds_Retry_back_until_the_clock_passes_it()
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org });
        rig.Sign.Start.Throws(new FadenstackRateLimitedException(HttpStatusCode.TooManyRequests, "slow", null, TimeSpan.FromSeconds(30)));
        var started = rig.Clock.Now;
        rig.Start();
        var changes = 0;
        rig.Wizard.RetryCommand.CanExecuteChanged += (_, _) => changes++;

        Assert.Equal(started.AddSeconds(30), rig.Wizard.RetryAt);
        WizardRig.Unavailable(rig.Wizard.RetryCommand);
        rig.Clock.Advance(TimeSpan.FromSeconds(29));
        WizardRig.Unavailable(rig.Wizard.RetryCommand);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(rig.Wizard.RetryCommand.CanExecute(null));
        Assert.True(changes > 0, "the skin is told when Retry becomes available");
    }

    [Fact]
    public void A_rate_limit_without_RetryAfter_waits_a_minute()
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org });
        rig.Sign.Start.Throws(new FadenstackRateLimitedException(HttpStatusCode.TooManyRequests, "slow", null, null));
        var started = rig.Clock.Now;
        rig.Start();

        Assert.Equal(started.AddSeconds(60), rig.Wizard.RetryAt);
    }

    [Fact]
    public void StartFailed_to_PairStarting_on_Retry()
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org });
        rig.Sign.Start.Throws(new FadenstackUnreachableException("no answer"));
        rig.Start();
        var again = rig.Sign.Start.Pending();

        rig.Press(rig.Wizard.RetryCommand);

        Assert.Equal(WizardStep.PairStarting, rig.Wizard.Step);
        Assert.Null(rig.Wizard.StartFailure);
        Assert.Equal(2, rig.Sign.Started.Count);
        again.Complete(Tickets.Make(rig.Clock.Now.AddMinutes(10)));
        Assert.Equal(WizardStep.PairCode, rig.Wizard.Step);
    }

    [Fact]
    public void StartFailed_to_ServerAddress_on_ChangeServer()
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org });
        rig.Sign.Start.Throws(new FadenstackUnreachableException("no answer"));
        rig.Start();

        rig.Press(rig.Wizard.ChangeServerCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
    }

    [Fact]
    public void PairStarting_to_ServerTooOld_when_the_server_has_no_pairing()
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org });
        rig.Sign.Start.Throws(new FadenstackPairingUnsupportedException("too old"));

        rig.Start();

        Assert.Equal(WizardStep.ServerTooOld, rig.Wizard.Step);
        Assert.Null(rig.Wizard.StartFailure);
    }

    [Fact]
    public void ServerTooOld_to_Password_only_with_the_password_fallback()
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org, PasswordFallback = true });
        rig.Sign.Start.Throws(new FadenstackPairingUnsupportedException("too old"));
        rig.Start();

        rig.Press(rig.Wizard.UsePasswordCommand);

        Assert.Equal(WizardStep.Password, rig.Wizard.Step);
    }

    [Fact]
    public void ServerTooOld_offers_no_password_without_the_fallback()
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org });
        rig.Sign.Start.Throws(new FadenstackPairingUnsupportedException("too old"));
        rig.Start();

        WizardRig.Unavailable(rig.Wizard.UsePasswordCommand);
        Assert.False(rig.Wizard.PasswordFallback);
    }

    [Fact]
    public void ServerTooOld_to_ServerAddress_on_ChangeServer()
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org });
        rig.Sign.Start.Throws(new FadenstackPairingUnsupportedException("too old"));
        rig.Start();

        rig.Press(rig.Wizard.ChangeServerCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
    }

    [Fact]
    public void PairCode_to_Approved_to_Redeeming_to_SignedIn_to_AfterConnect_to_Finished_Connected()
    {
        var wait = default(PendingAnswer<PairingApproval>);
        var step = new FakeStep(WizardPoint.AfterConnect);
        using var rig = PairCode(o => o with { Steps = new[] { step } }, before: r => wait = r.Sign.Wait.Pending());

        wait!.Complete(Approve("main-agent"));

        rig.AssertPassed(WizardStep.PairCode, WizardStep.Approved, WizardStep.Redeeming, WizardStep.SignedIn);
        Assert.Equal(WizardStep.SignedIn, rig.Wizard.Step);
        Assert.Equal(Ana, rig.Wizard.Person);
        Assert.Equal(new[] { new AgentSignIn("main-agent", AgentAccess.Approved) }, rig.Wizard.Agents);
        Assert.Empty(rig.Store.Saved); // "Signed in as ..." shows first
        rig.Clock.Advance(Pause - TimeSpan.FromMilliseconds(1));
        Assert.Equal(WizardStep.SignedIn, rig.Wizard.Step);

        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));

        rig.AssertPassed(WizardStep.SignedIn, WizardStep.AfterConnect, WizardStep.Finished);
        Assert.Equal(WizardOutcome.Connected, rig.Wizard.Outcome);
        var saved = Assert.Single(rig.Store.Saved);
        Assert.Same(saved, rig.Wizard.Result);
        Assert.Equal(ConnectionMode.Server, saved.Mode);
        Assert.Equal(Org, saved.Server);
        Assert.Equal(Ana, saved.Person);
        Assert.Equal(new[] { new AgentSignIn("main-agent", AgentAccess.Approved) }, saved.Agents);
        Assert.Equal(new[] { WizardOutcome.Connected }, rig.Completed);
    }

    [Theory]
    [InlineData("denied", WizardStep.Denied)]
    [InlineData("expired", WizardStep.Expired)]
    [InlineData("failed", WizardStep.PairFailed)]
    public void PairCode_to_Denied_Expired_or_PairFailed_when_the_wait_ends_so(string kind, WizardStep expected)
    {
        var wait = default(PendingAnswer<PairingApproval>);
        using var rig = PairCode(before: r => wait = r.Sign.Wait.Pending());

        wait!.Fail(kind switch
        {
            "denied" => new FadenstackPairingDeniedException("denied"),
            "expired" => new FadenstackPairingExpiredException("expired"),
            _ => new FadenstackApiException(HttpStatusCode.InternalServerError, "oops"),
        });

        Assert.Equal(expected, rig.Wizard.Step);
        Assert.Equal(0, rig.Clock.ActiveTimers); // the countdown stops
        Assert.False(rig.Wizard.IsRetrying);
    }

    [Fact]
    public void PairCode_to_Password_when_the_person_chooses_a_password()
    {
        using var rig = PairCode(o => o with { PasswordFallback = true });

        rig.Press(rig.Wizard.UsePasswordCommand);

        Assert.Equal(WizardStep.Password, rig.Wizard.Step);
        Assert.True(rig.Sign.Waited[0].Token.IsCancellationRequested, "the wait for the approval stops");
        Assert.Null(rig.Wizard.UserCode);
        Assert.Equal(0, rig.Clock.ActiveTimers);
    }

    [Fact]
    public void PairCode_offers_no_password_without_the_fallback()
    {
        using var rig = PairCode();

        WizardRig.Unavailable(rig.Wizard.UsePasswordCommand);
    }

    [Fact]
    public void PairCode_to_ServerAddress_on_Cancel()
    {
        using var rig = PairCode();

        rig.Press(rig.Wizard.CancelCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.True(rig.Sign.Waited[0].Token.IsCancellationRequested);
        Assert.Null(rig.Wizard.UserCode);
        Assert.Equal(0, rig.Clock.ActiveTimers);
    }

    [Fact]
    public void PairCode_to_ServerAddress_on_ChangeServer()
    {
        using var rig = PairCode();

        rig.Press(rig.Wizard.ChangeServerCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
    }

    [Fact]
    public void The_countdown_ticks_each_second_and_at_zero_the_wizard_goes_to_Expired_by_itself()
    {
        using var rig = Rig();
        rig.Sign.Start.Returns(Tickets.Make(rig.Clock.Now.AddSeconds(5)));
        var late = rig.Sign.Wait.Pending(honourCancel: false);
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Wizard.TimeLeft);

        for (var left = 4; left >= 1; left--)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(TimeSpan.FromSeconds(left), rig.Wizard.TimeLeft);
            Assert.Equal(WizardStep.PairCode, rig.Wizard.Step);
        }
        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(WizardStep.Expired, rig.Wizard.Step);
        Assert.True(rig.Sign.Waited[0].Token.IsCancellationRequested, "the wait stops");
        Assert.Null(rig.Wizard.UserCode);
        Assert.Equal(0, rig.Clock.ActiveTimers);

        late.Complete(Approve("main-agent")); // an approval that comes too late changes nothing
        Assert.Equal(WizardStep.Expired, rig.Wizard.Step);
    }

    [Fact]
    public void The_retrying_banner_follows_the_waits_progress()
    {
        using var rig = PairCode();
        var progress = rig.Sign.Waited[0].Progress;

        progress.Report(true);
        Assert.True(rig.Wizard.IsRetrying);
        progress.Report(false);
        Assert.False(rig.Wizard.IsRetrying);
    }

    [Theory]
    [InlineData("denied", WizardStep.Denied)]
    [InlineData("expired", WizardStep.Expired)]
    public void Denied_and_Expired_to_PairStarting_on_NewCode(string kind, WizardStep from)
    {
        var wait = default(PendingAnswer<PairingApproval>);
        using var rig = PairCode(before: r =>
        {
            wait = r.Sign.Wait.Pending();
            r.Sign.Start.Returns(Tickets.Make(r.Clock.Now.AddMinutes(10)));
        });
        wait!.Fail(kind == "denied" ? new FadenstackPairingDeniedException("denied") : new FadenstackPairingExpiredException("expired"));
        Assert.Equal(from, rig.Wizard.Step);
        rig.Sign.Start.Returns(Tickets.Make(rig.Clock.Now.AddMinutes(10), "NEW1-CODE"));

        rig.Press(rig.Wizard.NewCodeCommand);

        rig.AssertPassed(from, WizardStep.PairStarting, WizardStep.PairCode);
        Assert.Equal("NEW1-CODE", rig.Wizard.UserCode);
        Assert.Equal(2, rig.Sign.Started.Count);
    }

    [Fact]
    public void NewCode_is_not_available_at_the_code()
    {
        using var rig = PairCode();

        WizardRig.Unavailable(rig.Wizard.NewCodeCommand);
    }

    [Fact]
    public void PairFailed_to_PairStarting_on_Retry()
    {
        var wait = default(PendingAnswer<PairingApproval>);
        using var rig = PairCode(before: r => wait = r.Sign.Wait.Pending());
        wait!.Fail(new FadenstackApiException(HttpStatusCode.InternalServerError, "oops"));
        Assert.Equal(WizardStep.PairFailed, rig.Wizard.Step);
        rig.Sign.Start.Returns(Tickets.Make(rig.Clock.Now.AddMinutes(10), "NEW1-CODE"));

        rig.Press(rig.Wizard.RetryCommand);

        Assert.Equal(WizardStep.PairCode, rig.Wizard.Step);
        Assert.Equal("NEW1-CODE", rig.Wizard.UserCode);
    }

    [Fact]
    public void PairFailed_to_ServerAddress_on_ChangeServer()
    {
        var wait = default(PendingAnswer<PairingApproval>);
        using var rig = PairCode(before: r => wait = r.Sign.Wait.Pending());
        wait!.Fail(new FadenstackApiException(HttpStatusCode.InternalServerError, "oops"));

        rig.Press(rig.Wizard.ChangeServerCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
    }

    private static readonly string[] Suite = ["word", "excel", "outlook"];

    private static WizardRig SuitePairCode(out PendingAnswer<PairingApproval> wait, string[]? required = null,
        Action<WizardRig>? before = null)
    {
        PendingAnswer<PairingApproval>? pending = null;
        var rig = PairCode(o => o with { Agents = Suite, RequiredAgents = required ?? Array.Empty<string>() }, r =>
        {
            pending = r.Sign.Wait.Pending();
            r.Sign.Start.Returns(Tickets.Make(r.Clock.Now.AddMinutes(10), "BDWP-HTQX", Suite));
            before?.Invoke(r);
        });
        wait = pending!;
        return rig;
    }

    [Fact]
    public void Redeeming_collects_every_agent_of_the_ticket_not_yet_collected()
    {
        using var rig = SuitePairCode(out var wait);

        wait.Complete(Approve("word"));

        Assert.Equal(new[] { "excel", "outlook" }, rig.Sign.Redeemed);
        Assert.Equal(WizardStep.SignedIn, rig.Wizard.Step);
        Assert.Equal(Suite, rig.Wizard.Agents.Select(a => a.Agent));
        Assert.All(rig.Wizard.Agents, a => Assert.Equal(AgentAccess.Approved, a.Access));
    }

    [Fact]
    public void A_refused_agent_that_is_not_required_still_signs_in()
    {
        using var rig = SuitePairCode(out var wait, before: r => r.Sign.Redeem.Returns(new AgentSignIn("excel", AgentAccess.Refused)));

        wait.Complete(Approve("word"));

        Assert.Equal(WizardStep.SignedIn, rig.Wizard.Step);
        Assert.Equal(AgentAccess.Refused, rig.Wizard.Agents.Single(a => a.Agent == "excel").Access);
        Assert.Equal(AgentAccess.Approved, rig.Wizard.Agents.Single(a => a.Agent == "outlook").Access);
    }

    [Fact]
    public void Redeeming_to_NoAccess_when_a_required_agent_is_refused()
    {
        using var rig = SuitePairCode(out var wait, required: ["excel"],
            before: r => r.Sign.Redeem.Returns(new AgentSignIn("excel", AgentAccess.Refused)));

        wait.Complete(Approve("word"));

        rig.AssertPassed(WizardStep.Redeeming, WizardStep.NoAccess);
        Assert.Equal(WizardStep.NoAccess, rig.Wizard.Step);
        Assert.Empty(rig.Store.Saved);
        Assert.Null(rig.Wizard.Result);
    }

    [Fact]
    public void Redeeming_to_NoAccess_when_the_main_agent_is_not_approved()
    {
        using var rig = SuitePairCode(out var wait);

        wait.Complete(new PairingApproval(null, new[]
        {
            new AgentSignIn("word", AgentAccess.Refused), new AgentSignIn("excel", AgentAccess.Approved),
        }));

        Assert.Equal(WizardStep.NoAccess, rig.Wizard.Step);
        Assert.Equal(new[] { "outlook" }, rig.Sign.Redeemed); // the ones the approval did not name are still collected
    }

    [Fact]
    public void Redeeming_to_PairFailed_when_collecting_an_agent_fails()
    {
        using var rig = SuitePairCode(out var wait,
            before: r => r.Sign.Redeem.Throws(new FadenstackUnreachableException("no answer")));

        wait.Complete(Approve("word"));

        Assert.Equal(WizardStep.PairFailed, rig.Wizard.Step);
    }

    [Fact]
    public void NoAccess_to_ServerAddress_on_ChangeServer_and_to_Finished_Closed_on_Close()
    {
        using var rig = SuitePairCode(out var wait);
        wait.Complete(new PairingApproval(null, new[] { new AgentSignIn("word", AgentAccess.Refused) }));
        Assert.Equal(WizardStep.NoAccess, rig.Wizard.Step);

        rig.Press(rig.Wizard.ChangeServerCommand);
        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);

        using var other = SuitePairCode(out var wait2);
        wait2.Complete(new PairingApproval(null, new[] { new AgentSignIn("word", AgentAccess.Refused) }));
        other.Press(other.Wizard.CloseCommand);
        Assert.Equal(WizardStep.Finished, other.Wizard.Step);
        Assert.Equal(WizardOutcome.Closed, other.Wizard.Outcome);
    }

    // ---- The password path ---------------------------------------------------------------------------------------------

    private static WizardRig PasswordStep(Action<WizardRig>? before = null)
    {
        var rig = PairCode(o => o with { PasswordFallback = true }, before);
        rig.Press(rig.Wizard.UsePasswordCommand);
        Assert.Equal(WizardStep.Password, rig.Wizard.Step);
        return rig;
    }

    [Fact]
    public void Password_to_SignedIn_and_on_to_Finished_Connected()
    {
        using var rig = PasswordStep(r => r.Sign.Password.Returns(Approve("main-agent")));
        rig.Wizard.Email = "  ana@example.test ";

        rig.Press(rig.Wizard.SignInWithPasswordCommand, "correct horse");

        Assert.Equal(WizardStep.SignedIn, rig.Wizard.Step);
        Assert.Equal(new PasswordCall(Org, "ana@example.test", "correct horse"), Assert.Single(rig.Sign.PasswordsTried));
        Assert.False(rig.Wizard.IsBusy);
        rig.Clock.Advance(Pause);
        Assert.Equal(WizardOutcome.Connected, rig.Wizard.Outcome);
        Assert.Equal(Org, Assert.Single(rig.Store.Saved).Server);
    }

    [Fact]
    public void Password_to_NoAccess_when_a_required_agent_is_refused()
    {
        using var rig = PasswordStep(r => r.Sign.Password.Returns(new PairingApproval(null,
            new[] { new AgentSignIn("main-agent", AgentAccess.Refused) })));
        rig.Wizard.Email = "ana@example.test";

        rig.Press(rig.Wizard.SignInWithPasswordCommand, "pw");

        Assert.Equal(WizardStep.NoAccess, rig.Wizard.Step);
    }

    [Fact]
    public void Password_stays_when_the_email_or_the_password_is_empty()
    {
        using var rig = PasswordStep();

        rig.Wizard.Email = "";
        rig.Press(rig.Wizard.SignInWithPasswordCommand, "pw");
        rig.Wizard.Email = "ana@example.test";
        rig.Press(rig.Wizard.SignInWithPasswordCommand, "");
        rig.Press(rig.Wizard.SignInWithPasswordCommand, null);

        Assert.Equal(WizardStep.Password, rig.Wizard.Step);
        Assert.Empty(rig.Sign.PasswordsTried);
    }

    [Theory]
    [InlineData("bad", PasswordFailure.BadCredentials)]
    [InlineData("https", PasswordFailure.HttpsRequired)]
    [InlineData("not_fadenstack", PasswordFailure.NotFadenstack)]
    [InlineData("redirect", PasswordFailure.NotFadenstack)]
    [InlineData("unreachable", PasswordFailure.Unreachable)]
    [InlineData("certificate", PasswordFailure.CertificateNotTrusted)]
    [InlineData("other", PasswordFailure.Other)]
    public void Password_to_PasswordError_for_each_typed_error(string kind, PasswordFailure expected)
    {
        using var rig = PasswordStep(r => r.Sign.Password.Throws(kind switch
        {
            "bad" => new FadenstackBadCredentialsException(HttpStatusCode.BadRequest, "wrong"),
            "https" => new FadenstackHttpsRequiredException("https needed"),
            "not_fadenstack" => new FadenstackApiException(HttpStatusCode.OK, "a web page", "not_fadenstack"),
            "redirect" => new FadenstackApiException(HttpStatusCode.Found, "moved", "redirect"),
            "unreachable" => new FadenstackUnreachableException("no answer"),
            "certificate" => new ServerCertificateException("not trusted"),
            _ => new InvalidOperationException("odd"),
        }));
        rig.Wizard.Email = "ana@example.test";

        rig.Press(rig.Wizard.SignInWithPasswordCommand, "pw");

        Assert.Equal(WizardStep.PasswordError, rig.Wizard.Step);
        Assert.Equal(expected, rig.Wizard.PasswordFailure);
        Assert.False(rig.Wizard.IsBusy);
    }

    [Fact]
    public void PasswordError_to_Password_on_Retry()
    {
        using var rig = PasswordStep(r => r.Sign.Password.Throws(new FadenstackBadCredentialsException(HttpStatusCode.BadRequest, "wrong")));
        rig.Wizard.Email = "ana@example.test";
        rig.Press(rig.Wizard.SignInWithPasswordCommand, "pw");

        rig.Press(rig.Wizard.RetryCommand);

        Assert.Equal(WizardStep.Password, rig.Wizard.Step);
        Assert.Null(rig.Wizard.PasswordFailure);
    }

    [Fact]
    public void Password_to_PairStarting_on_BackToCode()
    {
        using var rig = PasswordStep();
        rig.Sign.Start.Returns(Tickets.Make(rig.Clock.Now.AddMinutes(10), "NEW1-CODE"));

        rig.Press(rig.Wizard.BackToCodeCommand);

        rig.AssertPassed(WizardStep.Password, WizardStep.PairStarting, WizardStep.PairCode);
        Assert.Equal("NEW1-CODE", rig.Wizard.UserCode);
    }

    [Fact]
    public void PasswordError_to_PairStarting_on_BackToCode()
    {
        using var rig = PasswordStep(r => r.Sign.Password.Throws(new FadenstackBadCredentialsException(HttpStatusCode.BadRequest, "wrong")));
        rig.Wizard.Email = "ana@example.test";
        rig.Press(rig.Wizard.SignInWithPasswordCommand, "pw");
        rig.Sign.Start.Returns(Tickets.Make(rig.Clock.Now.AddMinutes(10), "NEW1-CODE"));

        rig.Press(rig.Wizard.BackToCodeCommand);

        Assert.Equal(WizardStep.PairCode, rig.Wizard.Step);
    }

    [Fact]
    public void A_password_sign_in_is_busy_while_it_runs_and_Cancel_stops_it()
    {
        PendingAnswer<PairingApproval>? login = null;
        using var rig = PasswordStep(r => login = r.Sign.Password.Pending(honourCancel: false));
        rig.Wizard.Email = "ana@example.test";

        rig.Press(rig.Wizard.SignInWithPasswordCommand, "pw");
        Assert.True(rig.Wizard.IsBusy);
        WizardRig.Unavailable(rig.Wizard.SignInWithPasswordCommand);
        WizardRig.Unavailable(rig.Wizard.BackToCodeCommand);
        rig.Press(rig.Wizard.CancelCommand);

        Assert.False(rig.Wizard.IsBusy);
        Assert.Equal(WizardStep.Password, rig.Wizard.Step);
        login!.Complete(Approve("main-agent")); // the answer comes late: stale
        Assert.Equal(WizardStep.Password, rig.Wizard.Step);
        Assert.Empty(rig.Store.Saved);
    }

    // ---- The pairing page and the clipboard --------------------------------------------------------------------------

    [Fact]
    public void OpenPairingPage_opens_the_page_through_the_link_opener()
    {
        using var rig = PairCode();

        rig.Press(rig.Wizard.OpenPairingPageCommand);

        Assert.Equal(new[] { new Uri("https://ai.example.test/pair?code=BDWP-HTQX") }, rig.Links.Opened);
    }

    [Fact]
    public void CopyCode_puts_the_user_code_on_the_clipboard()
    {
        using var rig = PairCode();

        rig.Press(rig.Wizard.CopyCodeCommand);

        Assert.Equal(new[] { "BDWP-HTQX" }, rig.Clipboard.Texts);
    }

    [Fact]
    public void The_page_and_the_clipboard_are_available_only_at_the_code()
    {
        using var rig = Rig();
        rig.Start();

        WizardRig.Unavailable(rig.Wizard.OpenPairingPageCommand);
        WizardRig.Unavailable(rig.Wizard.CopyCodeCommand);
    }

    // ---- The address rule ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://ai.example.test/faden/", "https://ai.example.test/faden/")]
    [InlineData("https://ai.example.test/faden", "https://ai.example.test/faden")]
    [InlineData("https://ai.example.test/faden/?x=1&y=2", "https://ai.example.test/faden/")]
    [InlineData("https://ai.example.test/faden/#section", "https://ai.example.test/faden/")]
    [InlineData("https://ai.example.test:8443/?token=abc#frag", "https://ai.example.test:8443/")]
    [InlineData("ai.example.test", "https://ai.example.test/")]
    [InlineData("ai.example.test:8443/faden", "https://ai.example.test:8443/faden")]
    [InlineData("  https://ai.example.test  ", "https://ai.example.test/")]
    [InlineData("http://localhost:8000", "http://localhost:8000/")]
    [InlineData("http://127.0.0.1:8000/faden/", "http://127.0.0.1:8000/faden/")]
    [InlineData("http://[::1]:8000", "http://[::1]:8000/")]
    public void The_address_rule_keeps_scheme_host_port_and_path_and_drops_query_and_fragment(string typed, string kept)
    {
        using var rig = Rig();

        var server = rig.Wizard.ParseServer(typed, out var problem);

        Assert.NotNull(server);
        Assert.Equal(kept, server!.AbsoluteUri);
        Assert.Equal(AddressProblem.None, problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://anna:secret@ai.example.test")]
    [InlineData("https://anna@ai.example.test/")]
    [InlineData("ftp://ai.example.test")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]
    [InlineData("not a host name")]
    public void The_address_rule_refuses_user_info_and_other_schemes_and_nonsense(string typed)
    {
        using var rig = Rig();

        var server = rig.Wizard.ParseServer(typed, out var problem);

        Assert.Null(server);
        Assert.Equal(AddressProblem.Invalid, problem);
    }

    [Theory]
    [InlineData("http://ai.example.test")]
    [InlineData("http://192.168.1.20:8000")]
    [InlineData("http://ai.example.test/faden/")]
    public void The_address_rule_refuses_plain_http_to_another_machine(string typed)
    {
        using var rig = Rig();

        var server = rig.Wizard.ParseServer(typed, out var problem);

        Assert.Null(server);
        Assert.Equal(AddressProblem.HttpsRequired, problem);
    }

    [Theory]
    [InlineData("http://corp.internal:8080/faden", true)]
    [InlineData("http://corp.internal:8080/faden/", true)]
    [InlineData("HTTP://CORP.internal:8080/faden", true)]
    [InlineData("http://corp.internal:8080/other", false)]
    [InlineData("http://other.internal:8080/faden", false)]
    public void The_address_rule_takes_plain_http_only_to_the_http_server_a_machine_policy_names(string typed, bool taken)
    {
        var policy = new MachinePolicy { Server = new Uri("http://corp.internal:8080/faden"), Standalone = PolicyAccess.Forbidden };
        using var rig = Rig(o => o with { Policy = policy });

        var server = rig.Wizard.ParseServer(typed, out var problem);

        Assert.Equal(taken, server is not null);
        Assert.Equal(taken ? AddressProblem.None : AddressProblem.HttpsRequired, problem);
    }

    [Fact]
    public void Submitting_a_bad_address_says_why_and_nothing_runs()
    {
        var step = new FakeStep(WizardPoint.BeforeServerCheck);
        using var rig = Rig(o => o with { Steps = new[] { step } });
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);

        rig.Wizard.ServerAddressText = "http://ai.example.test";
        rig.Press(rig.Wizard.SubmitServerCommand);
        Assert.Equal(AddressProblem.HttpsRequired, rig.Wizard.AddressError);
        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);

        rig.Wizard.ServerAddressText = "ftp://x.test"; // typing again clears the old message
        Assert.Equal(AddressProblem.None, rig.Wizard.AddressError);
        rig.Press(rig.Wizard.SubmitServerCommand);
        Assert.Equal(AddressProblem.Invalid, rig.Wizard.AddressError);
        Assert.Empty(step.Contexts);
        Assert.Empty(rig.Sign.Checked);
    }

    // ===================================================================================================================
    // §3 Path B
    // ===================================================================================================================

    [Fact]
    public void OwnAccept_offers_Accept_only_once_the_box_is_ticked_and_the_box_starts_unticked()
    {
        using var rig = Rig();
        rig.Start();
        rig.Press(rig.Wizard.ChooseOwnAiCommand);

        Assert.False(rig.Wizard.Accepted);
        WizardRig.Unavailable(rig.Wizard.AcceptCommand);
        rig.Wizard.Accepted = true;
        Assert.True(rig.Wizard.AcceptCommand.CanExecute(null));
        rig.Wizard.Accepted = false;
        WizardRig.Unavailable(rig.Wizard.AcceptCommand);
        Assert.Empty(rig.Store.Acceptances);
    }

    [Fact]
    public void OwnAccept_to_OwnPick_keeps_the_acceptance_record_and_never_searches_without_consent()
    {
        using var rig = Rig(o => o with { TermsVersion = "2026-11-14" });
        rig.Start();
        rig.Press(rig.Wizard.ChooseOwnAiCommand);
        rig.Wizard.Accepted = true;
        var accepted = rig.Clock.Now;

        rig.Press(rig.Wizard.AcceptCommand);

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        var record = Assert.Single(rig.Store.Acceptances);
        Assert.Equal(new AcceptanceRecord("2026-11-14", accepted, rig.Strings.Language, "standalone"), record);
        Assert.Equal("de", record.Language);
        Assert.Equal(0, rig.Checker.Find.Calls);
    }

    [Fact]
    public void OwnAccept_to_OwnSearching_to_OwnPick_with_consent_to_search()
    {
        var ollama = new LocalEndpoint { BaseUrl = new Uri("http://127.0.0.1:11434/v1"), Preset = EndpointPreset.Ollama, Models = new[] { "m1" } };
        PendingAnswer<IReadOnlyList<LocalEndpoint>>? search = null;
        using var rig = OwnPick(before: r => search = r.Checker.Find.Pending(), search: true);

        Assert.Equal(WizardStep.OwnSearching, rig.Wizard.Step);
        Assert.True(rig.Wizard.IsBusy);
        Assert.Single(rig.Store.Acceptances);

        search!.Complete(new[] { ollama });

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.Equal(new[] { ollama }, rig.Wizard.Found);
        Assert.Equal(1, rig.Checker.Find.Calls);
    }

    [Fact]
    public void A_search_that_fails_finds_nothing_and_goes_on_to_OwnPick()
    {
        using var rig = OwnPick(before: r => r.Checker.Find.Throws(new InvalidOperationException("no sockets")), search: true);

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.Empty(rig.Wizard.Found);
    }

    [Fact]
    public void The_search_never_runs_when_the_app_cannot_search_this_computer()
    {
        using var rig = OwnPick(o => o with { CanSearchThisComputer = false }, search: true);

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.False(rig.Wizard.CanSearchThisComputer);
        Assert.Equal(0, rig.Checker.Find.Calls);
    }

    [Fact]
    public void OwnSearching_to_OwnPick_on_Cancel_and_the_late_answer_changes_nothing()
    {
        PendingAnswer<IReadOnlyList<LocalEndpoint>>? search = null;
        using var rig = OwnPick(before: r => search = r.Checker.Find.Pending(honourCancel: false), search: true);

        rig.Press(rig.Wizard.CancelCommand);
        search!.Complete(new[] { new LocalEndpoint { BaseUrl = new Uri("http://127.0.0.1:11434/v1") } });

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.Empty(rig.Wizard.Found);
    }

    [Fact]
    public void OwnAccept_to_ServerAddress_on_ServerModeInstead()
    {
        using var rig = Rig();
        rig.Start();
        rig.Press(rig.Wizard.ChooseOwnAiCommand);

        rig.Press(rig.Wizard.ServerModeInsteadCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.Empty(rig.Store.Acceptances);
    }

    [Fact]
    public void OwnAccept_to_Choose_on_Back_and_the_boxes_start_unticked_again()
    {
        using var rig = Rig();
        rig.Start();
        rig.Press(rig.Wizard.ChooseOwnAiCommand);
        rig.Wizard.Accepted = true;
        rig.Wizard.SearchConsent = true;

        rig.Press(rig.Wizard.BackCommand);
        Assert.Equal(WizardStep.Choose, rig.Wizard.Step);
        rig.Press(rig.Wizard.ChooseOwnAiCommand);

        Assert.False(rig.Wizard.Accepted);
        Assert.False(rig.Wizard.SearchConsent);
    }

    [Fact]
    public void OwnPick_offers_what_was_found_and_the_presets_and_a_pick_fills_the_address()
    {
        var found = new LocalEndpoint { BaseUrl = new Uri("http://127.0.0.1:1234/v1"), Preset = EndpointPreset.LMStudio };
        using var rig = OwnPick(before: r => r.Checker.Find.Returns(new[] { found }), search: true);

        rig.Press(rig.Wizard.SelectFoundCommand, found);
        Assert.Equal("http://127.0.0.1:1234/v1", rig.Wizard.EndpointAddressText);
        Assert.Same(EndpointPreset.LMStudio, rig.Wizard.SelectedPreset);
        Assert.False(rig.Wizard.NeedsKey);

        Pick(rig, EndpointPreset.OpenAI);
        Assert.Equal("https://api.openai.com/v1", rig.Wizard.EndpointAddressText);
        Assert.True(rig.Wizard.NeedsKey);
        Assert.Equal(EndpointPreset.All.Count, rig.Wizard.Presets.Count);
    }

    [Fact]
    public void OwnPick_to_OwnPlainHttp_for_http_to_a_local_network_address_and_ConfirmPlainHttp_checks_with_no_key()
    {
        using var rig = OwnPick(before: r => r.Checker.Places["192.168.1.20"] = EndpointPlace.LocalNetwork);
        Type(rig, "http://192.168.1.20:11434/v1");

        UseEndpoint(rig);

        Assert.Equal(WizardStep.OwnPlainHttp, rig.Wizard.Step);
        Assert.Empty(rig.Checker.Checked);
        rig.Press(rig.Wizard.ConfirmPlainHttpCommand);

        rig.AssertPassed(WizardStep.OwnPlainHttp, WizardStep.OwnChecking, WizardStep.OwnResult); // BeforeCheck shows only when an app step runs
        var endpoint = Assert.Single(rig.Checker.Checked);
        Assert.True(endpoint.AllowPlainHttpInLan);
        Assert.Null(endpoint.Key);
        Assert.Empty(rig.Keys.Saved);
        Assert.False(rig.Wizard.KeySet);
    }

    [Fact]
    public void OwnPlainHttp_to_OwnPick_on_Back()
    {
        using var rig = OwnPick(before: r => r.Checker.Places["192.168.1.20"] = EndpointPlace.LocalNetwork);
        Type(rig, "http://192.168.1.20:11434/v1");
        UseEndpoint(rig);

        rig.Press(rig.Wizard.BackCommand);

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
    }

    [Fact]
    public void OwnPick_to_OwnKey_when_a_key_is_needed()
    {
        using var rig = OwnPick();
        Pick(rig, EndpointPreset.OpenAI);

        UseEndpoint(rig);

        Assert.Equal(WizardStep.OwnKey, rig.Wizard.Step);
        Assert.Empty(rig.Checker.Checked);
        Assert.Empty(rig.Keys.Saved);
        Assert.Empty(rig.Checker.PlaceAsked); // https: no need to decide where it points
    }

    [Fact]
    public void OwnPick_to_BeforeCheck_to_OwnChecking_with_no_key()
    {
        var step = new FakeStep(WizardPoint.BeforeEndpointCheck);
        var running = step.Script.Pending();
        PendingAnswer<EndpointReport>? check = null;
        using var rig = OwnPick(o => o with { Steps = new[] { step } }, r => check = r.Checker.Report.Pending());
        Pick(rig, EndpointPreset.Ollama);

        UseEndpoint(rig);

        Assert.Equal(WizardStep.BeforeCheck, rig.Wizard.Step);
        Assert.Same(step, rig.Wizard.AppStep);
        var context = Assert.Single(step.Contexts);
        Assert.Equal(WizardPoint.BeforeEndpointCheck, context.Point);
        Assert.Equal(new Uri("http://localhost:11434/v1"), context.Endpoint!.BaseUrl);
        Assert.Null(context.Endpoint.Key);
        Assert.Empty(rig.Checker.Checked);

        running.Complete(true);

        Assert.Equal(WizardStep.OwnChecking, rig.Wizard.Step);
        var endpoint = Assert.Single(rig.Checker.Checked);
        Assert.Null(endpoint.Key);
        Assert.False(endpoint.AllowPlainHttpInLan);
        Assert.Equal("", endpoint.Model); // the check names the model; the person picks it afterwards
        Assert.Same(EndpointPreset.Ollama.Dialect, endpoint.Dialect);
        Assert.False(check!.IsCompleted);
    }

    [Fact]
    public void A_key_over_plain_http_to_this_computer_is_allowed()
    {
        using var rig = OwnPick();
        Type(rig, "http://127.0.0.1:4000/v1");
        rig.Wizard.NeedsKey = true;

        UseEndpoint(rig);

        Assert.Equal(WizardStep.OwnKey, rig.Wizard.Step);
        Assert.Equal(AddressProblem.None, rig.Wizard.AddressError);
        rig.Press(rig.Wizard.SubmitKeyCommand, "sk-local-1");
        Assert.Equal(WizardStep.OwnResult, rig.Wizard.Step);
        Assert.False(Assert.Single(rig.Checker.Checked).AllowPlainHttpInLan);
    }

    [Fact]
    public void Plain_http_to_the_internet_is_refused_as_HttpsRequired()
    {
        using var rig = OwnPick();
        Type(rig, "http://8.8.8.8:11434/v1");

        UseEndpoint(rig);

        Assert.Equal(AddressProblem.HttpsRequired, rig.Wizard.AddressError);
        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.Empty(rig.Checker.Checked);
    }

    [Fact]
    public void A_key_over_plain_http_to_the_local_network_is_refused_as_KeyNeedsHttps()
    {
        using var rig = OwnPick(before: r => r.Checker.Places["192.168.1.20"] = EndpointPlace.LocalNetwork);
        Type(rig, "http://192.168.1.20:4000/v1");
        rig.Wizard.NeedsKey = true;

        UseEndpoint(rig);

        Assert.Equal(AddressProblem.KeyNeedsHttps, rig.Wizard.AddressError);
        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.Empty(rig.Keys.Saved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:11434")]
    [InlineData("ftp://x.test/v1")]
    [InlineData("https://anna:secret@api.example.test/v1")]
    public void A_typed_endpoint_address_that_is_no_http_address_is_Invalid(string typed)
    {
        using var rig = OwnPick();
        Type(rig, typed);

        UseEndpoint(rig);

        Assert.Equal(AddressProblem.Invalid, rig.Wizard.AddressError);
        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
    }

    [Fact]
    public void A_typed_endpoint_address_drops_query_and_fragment()
    {
        using var rig = OwnPick();
        Type(rig, "https://api.example.test/v1?api-version=1#x");

        UseEndpoint(rig);

        Assert.Equal(new Uri("https://api.example.test/v1"), Assert.Single(rig.Checker.Checked).BaseUrl);
    }

    [Fact]
    public async Task OwnKey_to_BeforeCheck_hands_the_key_to_the_key_store_and_checks_with_its_provider()
    {
        var step = new FakeStep(WizardPoint.BeforeEndpointCheck);
        using var rig = OwnPick(o => o with { Steps = new[] { step } });
        Pick(rig, EndpointPreset.OpenAI);
        UseEndpoint(rig);

        rig.Press(rig.Wizard.SubmitKeyCommand, "  sk-test-1234  ");

        rig.AssertPassed(WizardStep.OwnKey, WizardStep.BeforeCheck, WizardStep.OwnChecking, WizardStep.OwnResult);
        Assert.Equal((new Uri("https://api.openai.com/v1"), "sk-test-1234"), Assert.Single(rig.Keys.Saved));
        Assert.True(rig.Wizard.KeySet);
        var provider = Assert.Single(rig.Keys.Providers);
        Assert.Same(provider, Assert.Single(rig.Checker.Checked).Key);
        Assert.Same(provider, Assert.Single(step.Contexts).Endpoint!.Key);
        Assert.Equal("sk-test-1234", await provider.GetAccessTokenAsync());
    }

    [Fact]
    public void OwnKey_ignores_an_empty_key()
    {
        using var rig = OwnPick();
        Pick(rig, EndpointPreset.OpenAI);
        UseEndpoint(rig);

        rig.Press(rig.Wizard.SubmitKeyCommand, "   ");
        rig.Press(rig.Wizard.SubmitKeyCommand, null);

        Assert.Equal(WizardStep.OwnKey, rig.Wizard.Step);
        Assert.Empty(rig.Keys.Saved);
    }

    [Fact]
    public void OwnKey_to_OwnPick_on_Back()
    {
        using var rig = OwnPick();
        Pick(rig, EndpointPreset.OpenAI);
        UseEndpoint(rig);

        rig.Press(rig.Wizard.BackCommand);

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("throws")]
    public void BeforeCheck_to_OwnPick_when_the_apps_step_refuses(string how)
    {
        var step = new FakeStep(WizardPoint.BeforeEndpointCheck);
        if (how == "refused")
        {
            step.Script.Returns(false);
        }
        else
        {
            step.Script.Throws(new InvalidOperationException("permission dialog failed"));
        }
        using var rig = OwnPick(o => o with { Steps = new[] { step } });
        Pick(rig, EndpointPreset.Ollama);

        UseEndpoint(rig);

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.True(rig.Wizard.AppStepRefused);
        Assert.Empty(rig.Checker.Checked);
    }

    [Fact]
    public void OwnChecking_to_OwnPick_on_Cancel_and_the_late_report_changes_nothing()
    {
        PendingAnswer<EndpointReport>? check = null;
        using var rig = OwnPick(before: r => check = r.Checker.Report.Pending(honourCancel: false));
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);
        Assert.Equal(WizardStep.OwnChecking, rig.Wizard.Step);

        rig.Press(rig.Wizard.CancelCommand);
        check!.Complete(Reports.Usable(new Uri("http://localhost:11434/v1")));

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.Null(rig.Wizard.Report);
        Assert.Empty(rig.Wizard.Models);
        Assert.Null(rig.Wizard.Result);
    }

    [Theory]
    [InlineData(EndpointProblem.KeyRejected)]
    [InlineData(EndpointProblem.OriginRefused)]
    [InlineData(EndpointProblem.Unreachable)]
    [InlineData(EndpointProblem.CertificateNotTrusted)]
    [InlineData(EndpointProblem.NotCompatible)]
    [InlineData(EndpointProblem.PlainHttpRefused)]
    [InlineData(EndpointProblem.RateLimited)]
    [InlineData(EndpointProblem.ModelNotFound)]
    public void OwnChecking_to_OwnFailed_for_each_blocking_problem(EndpointProblem problem)
    {
        var url = new Uri("http://localhost:11434/v1");
        using var rig = OwnPick(before: r => r.Checker.Report.Returns(Reports.Failing(url, problem)));
        Pick(rig, EndpointPreset.Ollama);

        UseEndpoint(rig);

        Assert.Equal(WizardStep.OwnFailed, rig.Wizard.Step);
        Assert.Equal(problem, rig.Wizard.Problem);
        Assert.NotNull(rig.Wizard.Report);
        Assert.Null(rig.Wizard.Result);
    }

    [Fact]
    public void OwnFailed_names_the_first_blocking_problem_the_key_before_the_unreachable()
    {
        var url = new Uri("http://localhost:11434/v1");
        using var rig = OwnPick(before: r => r.Checker.Report.Returns(
            Reports.Failing(url, EndpointProblem.NotCompatible, EndpointProblem.Unreachable, EndpointProblem.KeyRejected)));
        Pick(rig, EndpointPreset.Ollama);

        UseEndpoint(rig);

        Assert.Equal(EndpointProblem.KeyRejected, rig.Wizard.Problem);
    }

    [Fact]
    public void OwnFailed_with_only_non_blocking_problems_is_the_general_failure()
    {
        var url = new Uri("http://localhost:11434/v1");
        var report = Reports.Usable(url) with { Chat = false, Problems = new[] { EndpointProblem.NoToolCalls } };
        using var rig = OwnPick(before: r => r.Checker.Report.Returns(report));
        Pick(rig, EndpointPreset.Ollama);

        UseEndpoint(rig);

        Assert.Equal(WizardStep.OwnFailed, rig.Wizard.Step);
        Assert.Equal(EndpointProblem.Failed, rig.Wizard.Problem);
    }

    [Fact]
    public void OwnChecking_to_OwnFailed_when_the_check_itself_throws()
    {
        using var rig = OwnPick(before: r => r.Checker.Report.Throws(new InvalidOperationException("boom")));
        Pick(rig, EndpointPreset.Ollama);

        UseEndpoint(rig);

        Assert.Equal(WizardStep.OwnFailed, rig.Wizard.Step);
        Assert.Equal(EndpointProblem.Failed, rig.Wizard.Problem);
    }

    [Fact]
    public void OwnFailed_to_OwnKey_on_EnterKey_when_the_key_was_rejected_and_the_new_key_is_checked()
    {
        using var rig = OwnPick(before: r => r.Checker.Report.Returns(
            Reports.Failing(new Uri("https://api.openai.com/v1"), EndpointProblem.KeyRejected)));
        Pick(rig, EndpointPreset.OpenAI);
        UseEndpoint(rig);
        rig.Press(rig.Wizard.SubmitKeyCommand, "sk-wrong");
        Assert.Equal(WizardStep.OwnFailed, rig.Wizard.Step);

        rig.Press(rig.Wizard.EnterKeyCommand);

        Assert.Equal(WizardStep.OwnKey, rig.Wizard.Step);
        rig.Press(rig.Wizard.SubmitKeyCommand, "sk-right");
        Assert.Equal(WizardStep.OwnResult, rig.Wizard.Step);
        Assert.Equal(new[] { "sk-wrong", "sk-right" }, rig.Keys.Saved.Select(s => s.Key));
        Assert.Same(rig.Keys.Providers[1], rig.Checker.Checked[1].Key);
    }

    [Fact]
    public void EnterKey_is_not_offered_for_plain_http_to_the_local_network_or_for_other_problems()
    {
        var lan = new Uri("http://192.168.1.20:11434/v1");
        using var rig = OwnPick(before: r =>
        {
            r.Checker.Places["192.168.1.20"] = EndpointPlace.LocalNetwork;
            r.Checker.Report.Returns(Reports.Failing(lan, EndpointProblem.KeyRejected));
        });
        Type(rig, lan.AbsoluteUri);
        UseEndpoint(rig);
        rig.Press(rig.Wizard.ConfirmPlainHttpCommand);
        Assert.Equal(WizardStep.OwnFailed, rig.Wizard.Step);
        Assert.Equal(EndpointProblem.KeyRejected, rig.Wizard.Problem);

        WizardRig.Unavailable(rig.Wizard.EnterKeyCommand, "never a key over plain http to the network (D3)");

        using var other = OwnPick(before: r => r.Checker.Report.Returns(
            Reports.Failing(new Uri("http://localhost:11434/v1"), EndpointProblem.Unreachable)));
        Pick(other, EndpointPreset.Ollama);
        UseEndpoint(other);
        WizardRig.Unavailable(other.Wizard.EnterKeyCommand);
        WizardRig.Unavailable(other.Wizard.ShowHelpCommand);
    }

    [Fact]
    public void OwnFailed_to_OwnHelp_on_ShowHelp_when_the_origin_is_refused_and_OwnHelp_to_OwnChecking_on_Retry()
    {
        var url = new Uri("http://localhost:11434/v1");
        using var rig = OwnPick(before: r => r.Checker.Report.Returns(Reports.Failing(url, EndpointProblem.OriginRefused)));
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);
        WizardRig.Unavailable(rig.Wizard.EnterKeyCommand);

        rig.Press(rig.Wizard.ShowHelpCommand);
        Assert.Equal(WizardStep.OwnHelp, rig.Wizard.Step);

        rig.Press(rig.Wizard.RetryCommand);

        Assert.Equal(WizardStep.OwnResult, rig.Wizard.Step); // the second check passes
        Assert.Equal(2, rig.Checker.Checked.Count);
        rig.AssertPassed(WizardStep.OwnHelp, WizardStep.OwnChecking, WizardStep.OwnResult);
    }

    [Fact]
    public void OwnFailed_to_OwnChecking_on_Retry()
    {
        var url = new Uri("http://localhost:11434/v1");
        using var rig = OwnPick(before: r => r.Checker.Report.Returns(Reports.Failing(url, EndpointProblem.Unreachable)));
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);
        var again = rig.Checker.Report.Pending();

        rig.Press(rig.Wizard.RetryCommand);

        Assert.Equal(WizardStep.OwnChecking, rig.Wizard.Step);
        Assert.Equal(2, rig.Checker.Checked.Count);
        Assert.False(again.IsCompleted);
    }

    [Fact]
    public void OwnFailed_to_OwnPick_on_Back()
    {
        var url = new Uri("http://localhost:11434/v1");
        using var rig = OwnPick(before: r => r.Checker.Report.Returns(Reports.Failing(url, EndpointProblem.Unreachable)));
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);

        rig.Press(rig.Wizard.BackCommand);

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
    }

    [Fact]
    public void OwnChecking_to_OwnResult_shows_what_the_check_found()
    {
        var url = new Uri("http://localhost:11434/v1");
        var report = Reports.Usable(url, "m1", "m1", "m2");
        using var rig = OwnPick(before: r => r.Checker.Report.Returns(report));
        Pick(rig, EndpointPreset.Ollama);

        UseEndpoint(rig);

        Assert.Equal(WizardStep.OwnResult, rig.Wizard.Step);
        Assert.Same(report, rig.Wizard.Report);
        Assert.Equal(new[] { "m1", "m2" }, rig.Wizard.Models);
        Assert.Equal("m1", rig.Wizard.SelectedModel);
        Assert.Null(rig.Wizard.Problem);
    }

    [Fact]
    public void OwnResult_to_OwnPick_on_Back()
    {
        using var rig = OwnPick();
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);

        rig.Press(rig.Wizard.BackCommand);

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
    }

    [Fact]
    public void OwnResult_to_OwnModel_when_the_server_lists_several_models_also_chat_only()
    {
        var url = new Uri("http://localhost:11434/v1");
        var chatOnly = Reports.Usable(url, "m1", "m1", "m2") with { ToolCalls = false, Problems = new[] { EndpointProblem.NoToolCalls } };
        using var rig = OwnPick(before: r => r.Checker.Report.Returns(chatOnly));
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);
        Assert.True(rig.Wizard.Report!.ChatOnly);

        rig.Press(rig.Wizard.ContinueCommand);

        Assert.Equal(WizardStep.OwnModel, rig.Wizard.Step);
        Assert.Empty(rig.Store.Saved);
    }

    [Fact]
    public void OwnResult_to_OwnDone_to_Finished_Connected_when_there_is_one_model()
    {
        var step = new FakeStep(WizardPoint.AfterConnect);
        using var rig = OwnPick(o => o with { Steps = new[] { step } });
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);

        rig.Press(rig.Wizard.ContinueCommand);

        rig.AssertPassed(WizardStep.OwnResult, WizardStep.OwnDone, WizardStep.AfterConnect, WizardStep.Finished);
        Assert.Equal(WizardOutcome.Connected, rig.Wizard.Outcome);
        var saved = Assert.Single(rig.Store.Saved);
        Assert.Same(saved, rig.Wizard.Result);
        Assert.Equal(ConnectionMode.Endpoint, saved.Mode);
        Assert.Equal("m1", saved.Endpoint!.Model);
        Assert.Equal(new Uri("http://localhost:11434/v1"), saved.Endpoint.BaseUrl);
        Assert.Equal("ollama", saved.PresetId);
        Assert.Same(rig.Wizard.Report, saved.Report);
        Assert.Null(saved.Endpoint.Key);
        Assert.Same(saved, Assert.Single(step.Contexts).Result);
        Assert.Equal(new[] { WizardOutcome.Connected }, rig.Completed);
    }

    [Fact]
    public void The_checked_endpoint_and_the_result_carry_the_apps_CA_and_the_wizard_offers_no_CA_choice()
    {
        var ca = new byte[] { 0x30, 0x82, 0x01, 0x0A, 0x42 };
        using var rig = OwnPick(o => o with { TrustedCaCertificate = ca });
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);

        rig.Press(rig.Wizard.ContinueCommand);

        Assert.All(rig.Checker.Checked, e => Assert.Same(ca, e.TrustedCaCertificate));
        Assert.NotEmpty(rig.Checker.Checked);
        Assert.Same(ca, Assert.Single(rig.Store.Saved).Endpoint!.TrustedCaCertificate);
        // No property of the view model takes or shows a certificate: the CA comes from the app only.
        Assert.DoesNotContain(typeof(ConnectionWizardViewModel).GetProperties(),
            p => p.Name.Contains("Certificate", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Trust", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Without_an_app_CA_the_endpoint_carries_none()
    {
        using var rig = OwnPick();
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);
        rig.Press(rig.Wizard.ContinueCommand);

        Assert.Null(Assert.Single(rig.Store.Saved).Endpoint!.TrustedCaCertificate);
    }

    private static WizardRig ModelStep(out EndpointReport first)
    {
        var url = new Uri("https://api.openai.com/v1");
        first = Reports.Usable(url, "m1", "m1", "m2");
        var report = first;
        var rig = OwnPick(before: r => r.Checker.Report.Returns(report));
        Pick(rig, EndpointPreset.OpenAI);
        UseEndpoint(rig);
        rig.Press(rig.Wizard.SubmitKeyCommand, "sk-test-1234");
        rig.Press(rig.Wizard.ContinueCommand);
        Assert.Equal(WizardStep.OwnModel, rig.Wizard.Step);
        return rig;
    }

    [Fact]
    public void OwnModel_with_the_model_that_was_checked_finishes_without_a_second_check()
    {
        using var rig = ModelStep(out var first);
        Assert.Equal("m1", rig.Wizard.SelectedModel);

        rig.Press(rig.Wizard.ContinueCommand);

        Assert.Equal(WizardOutcome.Connected, rig.Wizard.Outcome);
        Assert.Single(rig.Checker.Checked);
        var saved = Assert.Single(rig.Store.Saved);
        Assert.Equal("m1", saved.Endpoint!.Model);
        Assert.Same(first, saved.Report);
        Assert.Same(rig.Keys.Providers[0], saved.Endpoint.Key);
        Assert.Equal("openai", saved.PresetId);
    }

    [Fact]
    public void OwnModel_checks_another_model_again_before_it_is_taken()
    {
        using var rig = ModelStep(out _);
        var second = Reports.Usable(new Uri("https://api.openai.com/v1"), "m2", "m1", "m2");
        rig.Checker.Report.Returns(second);

        rig.Wizard.SelectedModel = "m2";
        rig.Press(rig.Wizard.ContinueCommand);

        Assert.Equal(WizardStep.OwnResult, rig.Wizard.Step);
        Assert.Equal(new[] { "", "m2" }, rig.Checker.Checked.Select(e => e.Model));
        Assert.Same(rig.Keys.Providers[0], rig.Checker.Checked[1].Key);
        Assert.Same(second, rig.Wizard.Report);
        Assert.Empty(rig.Store.Saved); // not taken yet

        rig.Press(rig.Wizard.ContinueCommand); // the choice is made: the result finishes

        Assert.Equal(WizardOutcome.Connected, rig.Wizard.Outcome);
        var saved = Assert.Single(rig.Store.Saved);
        Assert.Equal("m2", saved.Endpoint!.Model);
        Assert.Same(second, saved.Report);
        Assert.Equal(2, rig.Checker.Checked.Count);
    }

    [Fact]
    public void OwnModel_to_OwnFailed_when_the_other_model_fails_its_check()
    {
        using var rig = ModelStep(out _);
        rig.Checker.Report.Returns(Reports.Failing(new Uri("https://api.openai.com/v1"), EndpointProblem.ModelNotFound));

        rig.Wizard.SelectedModel = "m2";
        rig.Press(rig.Wizard.ContinueCommand);

        Assert.Equal(WizardStep.OwnFailed, rig.Wizard.Step);
        Assert.Equal(EndpointProblem.ModelNotFound, rig.Wizard.Problem);
        Assert.Empty(rig.Store.Saved);
    }

    [Fact]
    public void OwnModel_offers_Continue_only_with_a_model_picked_and_goes_back_to_OwnResult()
    {
        using var rig = ModelStep(out _);

        rig.Wizard.SelectedModel = null;
        WizardRig.Unavailable(rig.Wizard.ContinueCommand);
        rig.Wizard.SelectedModel = "m2";
        Assert.True(rig.Wizard.ContinueCommand.CanExecute(null));
        rig.Press(rig.Wizard.BackCommand);

        Assert.Equal(WizardStep.OwnResult, rig.Wizard.Step);
    }

    // ===================================================================================================================
    // App steps
    // ===================================================================================================================

    [Fact]
    public void App_steps_run_at_their_points_in_order_and_each_sees_its_context()
    {
        var log = new List<string>();
        var beforeServer1 = new FakeStep(WizardPoint.BeforeServerCheck, "check-1", log);
        var beforeServer2 = new FakeStep(WizardPoint.BeforeServerCheck, "check-2", log);
        var after = new FakeStep(WizardPoint.AfterConnect, "after", log);
        var beforeChoice = new FakeStep(WizardPoint.BeforeChoice, "choice", log);
        var beforeEndpoint = new FakeStep(WizardPoint.BeforeEndpointCheck, "endpoint", log);
        using var rig = Rig(o => o with { Steps = new IWizardStep[] { beforeServer1, after, beforeChoice, beforeServer2, beforeEndpoint } });
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        var wait = rig.Sign.Wait.Pending();
        rig.Press(rig.Wizard.SubmitServerCommand);

        wait.Complete(Approve("main-agent"));
        rig.Clock.Advance(Pause);

        Assert.Equal(new[] { "choice", "check-1", "check-2", "after" }, log); // the endpoint step belongs to path B
        Assert.Equal(Org, beforeServer1.Contexts.Single().Server);
        Assert.Equal(Org, beforeServer2.Contexts.Single().Server);
        var finished = after.Contexts.Single();
        Assert.Equal(WizardPoint.AfterConnect, finished.Point);
        Assert.Equal(Org, finished.Server);
        Assert.Same(rig.Wizard.Result, finished.Result);
        Assert.Null(finished.Endpoint);
        Assert.Empty(beforeEndpoint.Contexts);
    }

    [Fact]
    public void A_step_that_says_false_stops_the_ones_after_it_at_that_point()
    {
        var log = new List<string>();
        var first = new FakeStep(WizardPoint.BeforeServerCheck, "first", log);
        first.Script.Returns(false);
        var second = new FakeStep(WizardPoint.BeforeServerCheck, "second", log);
        using var rig = Rig(o => o with { Steps = new[] { first, second } });
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;

        rig.Press(rig.Wizard.SubmitServerCommand);

        Assert.Equal(new[] { "first" }, log);
        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.True(rig.Wizard.AppStepRefused);
    }

    [Fact]
    public void A_step_that_throws_stops_the_flow_like_one_that_says_false()
    {
        var step = new FakeStep(WizardPoint.BeforeServerCheck);
        step.Script.Throws(new InvalidOperationException("boom"));
        using var rig = Rig(o => o with { Steps = new[] { step } });
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;

        rig.Press(rig.Wizard.SubmitServerCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.True(rig.Wizard.AppStepRefused);
        Assert.Empty(rig.Sign.Checked);
    }

    [Fact]
    public void AppStepRefused_clears_when_the_person_tries_again()
    {
        var step = new FakeStep(WizardPoint.BeforeServerCheck);
        step.Script.Returns(false).Returns(true);
        using var rig = Rig(o => o with { Steps = new[] { step } });
        rig.Sign.Check.Pending();
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);
        Assert.True(rig.Wizard.AppStepRefused);

        rig.Press(rig.Wizard.SubmitServerCommand);

        Assert.Equal(WizardStep.Checking, rig.Wizard.Step);
        Assert.False(rig.Wizard.AppStepRefused);
    }

    [Fact]
    public void AfterConnect_false_saves_nothing_and_goes_back_to_the_choice_on_a_first_run()
    {
        var step = new FakeStep(WizardPoint.AfterConnect);
        step.Script.Returns(false);
        using var rig = Rig(o => o with { Steps = new[] { step } });
        var wait = rig.Sign.Wait.Pending();
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);
        wait.Complete(Approve("main-agent"));

        rig.Clock.Advance(Pause);

        Assert.Equal(WizardStep.Choose, rig.Wizard.Step);
        Assert.Empty(rig.Store.Saved);
        Assert.Null(rig.Wizard.Result);
        Assert.Equal(WizardOutcome.None, rig.Wizard.Outcome);
        Assert.Empty(rig.Completed);
    }

    [Fact]
    public void AfterConnect_false_on_path_B_goes_back_to_Settings_when_it_began_there()
    {
        var step = new FakeStep(WizardPoint.AfterConnect);
        step.Script.Throws(new InvalidOperationException("the app refused the connection"));
        using var rig = Rig(o => o with { Entry = WizardEntry.Settings, Current = ServerConnection, Steps = new[] { step } });
        rig.Start();
        rig.Press(rig.Wizard.ChangeEndpointCommand);
        rig.Wizard.Accepted = true;
        rig.Press(rig.Wizard.AcceptCommand);
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);

        rig.Press(rig.Wizard.ContinueCommand);

        Assert.Equal(WizardStep.Settings, rig.Wizard.Step);
        Assert.Empty(rig.Store.Saved);
        Assert.Null(rig.Wizard.Result);
    }

    // ===================================================================================================================
    // Stale answers
    // ===================================================================================================================

    [Fact]
    public void A_health_check_answer_after_Cancel_changes_nothing()
    {
        using var rig = Rig();
        var check = rig.Sign.Check.Pending(honourCancel: false);
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);
        rig.Press(rig.Wizard.CancelCommand);

        check.Complete(true);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.Empty(rig.Store.Servers);
        Assert.Empty(rig.Sign.Started);
    }

    [Fact]
    public void A_health_check_failure_after_Cancel_changes_nothing()
    {
        using var rig = Rig();
        var check = rig.Sign.Check.Pending(honourCancel: false);
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);
        rig.Press(rig.Wizard.CancelCommand);

        check.Fail(new FadenstackUnreachableException("no answer"));

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
    }

    [Fact]
    public void A_ticket_after_Cancel_shows_no_code()
    {
        using var rig = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org });
        var start = rig.Sign.Start.Pending(honourCancel: false);
        rig.Start();
        rig.Press(rig.Wizard.CancelCommand);

        start.Complete(Tickets.Make(rig.Clock.Now.AddMinutes(10)));

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.Null(rig.Wizard.UserCode);
        Assert.Empty(rig.Sign.Waited);
        Assert.Equal(0, rig.Clock.ActiveTimers);
    }

    [Fact]
    public void An_approval_after_Cancel_signs_nobody_in()
    {
        var wait = default(PendingAnswer<PairingApproval>);
        using var rig = PairCode(before: r => wait = r.Sign.Wait.Pending(honourCancel: false));
        rig.Press(rig.Wizard.CancelCommand);
        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.True(rig.Sign.Waited[0].Token.IsCancellationRequested);

        wait!.Complete(Approve("main-agent"));

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.Null(rig.Wizard.Person);
        Assert.Empty(rig.Wizard.Agents);
        Assert.Null(rig.Wizard.Result);
        Assert.Empty(rig.Store.Saved);
        Assert.Equal(0, rig.Clock.ActiveTimers);
    }

    [Fact]
    public void A_collected_agent_after_Cancel_changes_nothing()
    {
        PendingAnswer<AgentSignIn>? redeem = null;
        using var rig = SuitePairCode(out var wait, before: r => redeem = r.Sign.Redeem.Pending(honourCancel: false));
        wait.Complete(Approve("word"));
        Assert.Equal(WizardStep.Redeeming, rig.Wizard.Step);

        rig.Press(rig.Wizard.CancelCommand);
        redeem!.Complete(new AgentSignIn("excel", AgentAccess.Approved));

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.Empty(rig.Store.Saved);
        Assert.Null(rig.Wizard.Result);
    }

    [Fact]
    public void An_old_check_answer_does_not_overwrite_a_newer_one()
    {
        using var rig = Rig();
        var old = rig.Sign.Check.Pending(honourCancel: false);
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);
        rig.Press(rig.Wizard.CancelCommand);
        rig.Sign.Check.Throws(new ServerCertificateException("not trusted"));
        rig.Press(rig.Wizard.SubmitServerCommand);
        Assert.Equal(WizardStep.CertificateNotTrusted, rig.Wizard.Step);

        old.Complete(true); // the first request answers last, and fine

        Assert.Equal(WizardStep.CertificateNotTrusted, rig.Wizard.Step);
        Assert.Empty(rig.Store.Servers);
    }

    [Fact]
    public void Dispose_makes_every_answer_stale()
    {
        var rig = Rig();
        var check = rig.Sign.Check.Pending(honourCancel: false);
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);

        rig.Dispose();
        check.Complete(true);

        Assert.Equal(WizardStep.Checking, rig.Wizard.Step);
        Assert.Empty(rig.Store.Servers);
    }

    // ===================================================================================================================
    // Life cycle
    // ===================================================================================================================

    [Fact]
    public void StartAsync_twice_throws()
    {
        using var rig = Rig();
        rig.Start();

        var again = Record.Exception(() => { _ = rig.Wizard.StartAsync(); });

        Assert.IsType<InvalidOperationException>(again);
    }

    [Fact]
    public void Dispose_stops_the_countdown_and_the_wait_and_the_rate_limit_timer()
    {
        using var rig = PairCode();
        Assert.Equal(1, rig.Clock.ActiveTimers);

        rig.Dispose();

        Assert.Equal(0, rig.Clock.ActiveTimers);
        Assert.True(rig.Sign.Waited[0].Token.IsCancellationRequested);
        rig.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(WizardStep.PairCode, rig.Wizard.Step);

        using var limited = Rig(o => o with { Entry = WizardEntry.ServerKnown, Server = Org });
        limited.Sign.Start.Throws(new FadenstackRateLimitedException(HttpStatusCode.TooManyRequests, "slow", null, TimeSpan.FromSeconds(30)));
        limited.Start();
        Assert.Equal(1, limited.Clock.ActiveTimers);
        limited.Dispose();
        Assert.Equal(0, limited.Clock.ActiveTimers);
    }

    [Fact]
    public void Dispose_stops_the_signed_in_pause()
    {
        var wait = default(PendingAnswer<PairingApproval>);
        using var rig = PairCode(before: r => wait = r.Sign.Wait.Pending());
        wait!.Complete(Approve("main-agent"));
        Assert.Equal(1, rig.Clock.ActiveTimers);

        rig.Dispose();
        rig.Clock.Advance(Pause);

        Assert.Equal(WizardStep.SignedIn, rig.Wizard.Step);
        Assert.Empty(rig.Store.Saved);
    }

    [Fact]
    public void Finishing_stops_the_timers()
    {
        var wait = default(PendingAnswer<PairingApproval>);
        using var rig = PairCode(before: r => wait = r.Sign.Wait.Pending());
        rig.Wizard.CloseCommand.Execute(null);

        Assert.Equal(WizardStep.Finished, rig.Wizard.Step);
        Assert.Equal(0, rig.Clock.ActiveTimers);
        Assert.True(rig.Sign.Waited[0].Token.IsCancellationRequested);
        Assert.Equal(new[] { WizardOutcome.Closed }, rig.Completed);
        Assert.NotNull(wait);
    }

    [Fact]
    public void The_constructor_throws_when_the_server_path_is_offered_without_a_sign_in()
    {
        var e = Assert.Throws<ArgumentException>(() =>
            new ConnectionWizardViewModel(WizardRig.DefaultOptions, new FakeConnectionStore(), server: null, keys: new FakeKeyStore()));

        Assert.Equal("server", e.ParamName);
    }

    [Fact]
    public void The_constructor_throws_when_own_AI_is_offered_without_a_key_store()
    {
        var e = Assert.Throws<ArgumentException>(() =>
            new ConnectionWizardViewModel(WizardRig.DefaultOptions, new FakeConnectionStore(),
                new FakeServerSignIn(() => DateTimeOffset.UtcNow), keys: null));

        Assert.Equal("keys", e.ParamName);
    }

    [Fact]
    public void The_constructor_does_not_need_what_a_path_that_is_not_offered_would_use()
    {
        using var serverOnly = new ConnectionWizardViewModel(WizardRig.DefaultOptions with { OffersOwnAi = false },
            new FakeConnectionStore(), new FakeServerSignIn(() => DateTimeOffset.UtcNow));
        using var ownOnly = new ConnectionWizardViewModel(WizardRig.DefaultOptions with { OffersServer = false },
            new FakeConnectionStore(), keys: new FakeKeyStore());

        Assert.NotNull(serverOnly);
        Assert.NotNull(ownOnly);
        Assert.Throws<ArgumentNullException>(() => new ConnectionWizardViewModel(null!, new FakeConnectionStore()));
        Assert.Throws<ArgumentNullException>(() => new ConnectionWizardViewModel(WizardRig.DefaultOptions, null!));
    }

    // ===================================================================================================================
    // The UI thread
    // ===================================================================================================================

    private static void OnAnotherThread(Action action)
    {
        var worker = new Thread(() => action());
        worker.Start();
        worker.Join();
    }

    [Fact]
    public void State_changes_after_an_await_happen_only_inside_posted_actions()
    {
        var ui = new QueuedDispatcher();
        using var rig = new WizardRig(dispatcher: ui);
        var seen = new List<(string? Property, bool OnUi)>();
        rig.Wizard.PropertyChanged += (_, e) => seen.Add((e.PropertyName, ui.CheckAccess()));
        var check = rig.Sign.Check.Pending();
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);
        Assert.Equal(WizardStep.Checking, rig.Wizard.Step);
        var before = seen.Count;

        // The answer arrives on another thread: nothing may change there.
        OnAnotherThread(() => check.Complete(true));

        Assert.Equal(WizardStep.Checking, rig.Wizard.Step);
        Assert.Equal(before, seen.Count);
        Assert.Empty(rig.Store.Servers);
        Assert.Equal(1, ui.Queued);

        Assert.Equal(1, ui.Pump());

        Assert.Equal(WizardStep.PairCode, rig.Wizard.Step);
        Assert.NotEmpty(rig.Store.Servers);
        Assert.True(seen.Count > before);
        Assert.All(seen, s => Assert.True(s.OnUi, s.Property + " changed off the UI thread"));
    }

    [Fact]
    public void The_retrying_banner_changes_only_inside_a_posted_action()
    {
        var ui = new QueuedDispatcher();
        using var rig = new WizardRig(dispatcher: ui);
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Press(rig.Wizard.SubmitServerCommand);
        Assert.Equal(WizardStep.PairCode, rig.Wizard.Step);
        var progress = rig.Sign.Waited[0].Progress;

        OnAnotherThread(() => progress.Report(true));

        Assert.False(rig.Wizard.IsRetrying);
        ui.Pump();
        Assert.True(rig.Wizard.IsRetrying);
    }

    [Fact]
    public void A_command_notification_from_another_thread_is_posted_to_the_UI_thread()
    {
        var ui = new QueuedDispatcher();
        using var rig = new WizardRig(dispatcher: ui);
        var told = new List<bool>();
        rig.Wizard.ContinueCommand.CanExecuteChanged += (_, _) => told.Add(ui.CheckAccess());
        rig.Wizard.AcceptCommand.CanExecuteChanged += (_, _) => told.Add(ui.CheckAccess());

        // A binding may set these from anywhere; a button's handler runs only on the UI thread (WPF's and Avalonia's throw elsewhere).
        OnAnotherThread(() => rig.Wizard.SelectedModel = "m2");
        OnAnotherThread(() => rig.Wizard.Accepted = true);

        Assert.Empty(told);
        ui.Pump();
        Assert.Equal(new[] { true, true }, told);
    }

    [Fact]
    public void A_check_that_fails_unexpectedly_shows_OwnFailed_instead_of_hanging()
    {
        var rig = OwnPick();
        Type(rig, "http://localhost:11434/v1");
        // A view that throws when the report changes (as WPF's button did off its thread, Word, 15 Nov): the flow fails after the check.
        rig.Wizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionWizardViewModel.Report))
            {
                throw new InvalidOperationException("The calling thread cannot access this object.");
            }
        };

        UseEndpoint(rig);

        Assert.Equal(WizardStep.OwnFailed, rig.Wizard.Step);
        Assert.Equal(EndpointProblem.Failed, rig.Wizard.Problem);
        Assert.False(rig.Wizard.IsBusy);
        Assert.True(rig.Wizard.RetryCommand.CanExecute(null));
    }

    [Fact]
    public void A_pairing_start_that_fails_unexpectedly_shows_its_failure_instead_of_hanging()
    {
        var rig = Rig();
        rig.Wizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionWizardViewModel.UserCode))
            {
                throw new InvalidOperationException("A view that fails.");
            }
        };
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;

        rig.Press(rig.Wizard.SubmitServerCommand);

        Assert.Equal(WizardStep.StartFailed, rig.Wizard.Step);
        Assert.Equal(StartFailure.ServerError, rig.Wizard.StartFailure);
        Assert.True(rig.Wizard.RetryCommand.CanExecute(null));
    }

    // ===================================================================================================================
    // No secret in the view model (F15 "Rules", ASVS 5.0 V13)
    // ===================================================================================================================

    private static void AssertNoSecret(object root, string where, params string[] secrets)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void Walk(object? value, string path, int depth)
        {
            if (value is null || depth > 6)
            {
                return;
            }
            var text = value.ToString() ?? "";
            foreach (var secret in secrets)
            {
                Assert.False(text.Contains(secret), $"{where}: {path} has a secret in its text");
            }
            if (value is string || value.GetType().IsPrimitive || value is Enum || !seen.Add(value))
            {
                return;
            }
            if (value is IEnumerable items)
            {
                foreach (var item in items)
                {
                    Walk(item, path + "[]", depth + 1);
                }
                return;
            }
            if (value.GetType().Namespace?.StartsWith("Fadenstack", StringComparison.Ordinal) != true)
            {
                return; // the toolkit's commands, the clock's types: the text above is all that is read of them
            }
            foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
            {
                Walk(property.GetValue(value), path + "." + property.Name, depth + 1);
            }
        }

        Walk(root, "wizard", 0);
    }

    [Fact]
    public void After_a_key_is_submitted_no_public_property_of_the_view_model_has_it()
    {
        const string key = "sk-live-SECRET-9f3a7c1d";
        using var rig = OwnPick();
        Pick(rig, EndpointPreset.OpenAI);
        UseEndpoint(rig);
        AssertNoSecret(rig.Wizard, "at the key step", key);

        rig.Press(rig.Wizard.SubmitKeyCommand, key);

        Assert.Equal(WizardStep.OwnResult, rig.Wizard.Step);
        Assert.True(rig.Wizard.KeySet);
        AssertNoSecret(rig.Wizard, "at the result", key);
        rig.Press(rig.Wizard.ContinueCommand);
        Assert.Equal(WizardOutcome.Connected, rig.Wizard.Outcome);
        AssertNoSecret(rig.Wizard, "when finished", key);
        AssertNoSecret(rig.Wizard.Result!, "the result", key);
        AssertNoSecret(rig.Store.Saved, "the saved results", key);
        AssertNoSecret(rig.Store.Acceptances, "the acceptance records", key);
        AssertNoSecret(rig.Store.Servers, "the saved servers", key);
    }

    [Fact]
    public void After_a_wrong_key_and_a_failed_check_the_key_is_still_in_no_property()
    {
        const string key = "sk-wrong-SECRET-1b2c3d";
        using var rig = OwnPick(before: r => r.Checker.Report.Returns(
            Reports.Failing(new Uri("https://api.openai.com/v1"), EndpointProblem.KeyRejected)));
        Pick(rig, EndpointPreset.OpenAI);
        UseEndpoint(rig);

        rig.Press(rig.Wizard.SubmitKeyCommand, key);

        Assert.Equal(WizardStep.OwnFailed, rig.Wizard.Step);
        AssertNoSecret(rig.Wizard, "after the rejected key", key);
    }

    [Fact]
    public void After_a_password_sign_in_no_public_property_of_the_view_model_has_the_password()
    {
        const string password = "hunter2-SECRET-passw0rd";
        using var rig = PasswordStep(r => r.Sign.Password.Returns(Approve("main-agent")));
        rig.Wizard.Email = "ana@example.test";

        rig.Press(rig.Wizard.SignInWithPasswordCommand, password);

        Assert.Equal(WizardStep.SignedIn, rig.Wizard.Step);
        AssertNoSecret(rig.Wizard, "at SignedIn", password);
        rig.Clock.Advance(Pause);
        Assert.Equal(WizardOutcome.Connected, rig.Wizard.Outcome);
        AssertNoSecret(rig.Wizard, "when finished", password);
        AssertNoSecret(rig.Wizard.Result!, "the result", password);
        AssertNoSecret(rig.Store.Saved, "the saved results", password);
    }

    [Fact]
    public void After_a_failed_password_sign_in_the_password_is_in_no_property()
    {
        const string password = "hunter2-SECRET-passw0rd";
        using var rig = PasswordStep(r => r.Sign.Password.Throws(new FadenstackBadCredentialsException(HttpStatusCode.BadRequest, "wrong")));
        rig.Wizard.Email = "ana@example.test";

        rig.Press(rig.Wizard.SignInWithPasswordCommand, password);

        Assert.Equal(WizardStep.PasswordError, rig.Wizard.Step);
        AssertNoSecret(rig.Wizard, "after the wrong password", password);
    }

    [Fact]
    public void The_secret_walk_does_find_a_secret_that_is_there()
    {
        var result = new ConnectionResult
        {
            Mode = ConnectionMode.Endpoint, Endpoint = new ModelEndpoint { BaseUrl = new Uri("https://x.test/v1"), Model = "sk-leaked" },
        };

        Assert.ThrowsAny<Exception>(() => AssertNoSecret(result, "a leak", "sk-leaked"));
    }

    // ===================================================================================================================
    // Store failures, a cancelled collection, an address that cannot be placed (review of the first pass)
    // ===================================================================================================================

    [Fact]
    public void Saving_the_server_fails_back_at_the_address_with_StoreFailed_and_no_pairing_starts()
    {
        using var rig = Rig();
        rig.Start();
        rig.Press(rig.Wizard.ChooseServerCommand);
        rig.Wizard.ServerAddressText = Address;
        rig.Store.Failure = new IOException("The settings file is read-only.");

        rig.Press(rig.Wizard.SubmitServerCommand);

        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
        Assert.True(rig.Wizard.StoreFailed);
        Assert.Empty(rig.Sign.Started);

        rig.Store.Failure = null;
        rig.Press(rig.Wizard.SubmitServerCommand);

        Assert.False(rig.Wizard.StoreFailed);
        Assert.Equal(WizardStep.PairCode, rig.Wizard.Step);
    }

    [Fact]
    public void Saving_the_acceptance_fails_and_the_person_stays_at_the_acceptance()
    {
        using var rig = Rig();
        rig.Start();
        rig.Press(rig.Wizard.ChooseOwnAiCommand);
        rig.Wizard.Accepted = true;
        rig.Wizard.SearchConsent = true;
        rig.Store.Failure = new IOException("disk full");

        rig.Press(rig.Wizard.AcceptCommand);

        Assert.Equal(WizardStep.OwnAccept, rig.Wizard.Step);
        Assert.True(rig.Wizard.StoreFailed);
        Assert.Empty(rig.Store.Acceptances);
        Assert.Equal(0, rig.Checker.Find.Calls);
    }

    [Fact]
    public void Saving_the_connection_fails_back_at_the_start_and_nothing_completes()
    {
        PendingAnswer<PairingApproval>? wait = null;
        using var rig = PairCode(before: r => wait = r.Sign.Wait.Pending());
        rig.Store.Failure = new IOException("disk full");

        wait!.Complete(Approve("main-agent"));
        rig.Clock.Advance(Pause);

        Assert.Equal(WizardStep.Choose, rig.Wizard.Step);
        Assert.True(rig.Wizard.StoreFailed);
        Assert.Null(rig.Wizard.Result);
        Assert.Empty(rig.Completed);
    }

    [Fact]
    public void Signing_out_fails_stays_at_the_sign_out_and_works_on_the_next_try()
    {
        using var rig = Settings();
        rig.Start();
        rig.Press(rig.Wizard.SignOutCommand);
        rig.Store.Failure = new IOException("locked");

        rig.Press(rig.Wizard.ConfirmSignOutCommand);

        Assert.Equal(WizardStep.SignOut, rig.Wizard.Step);
        Assert.True(rig.Wizard.StoreFailed);
        Assert.Empty(rig.Completed);

        rig.Store.Failure = null;
        rig.Press(rig.Wizard.ConfirmSignOutCommand);

        Assert.Equal(WizardOutcome.SignedOut, rig.Wizard.Outcome);
        Assert.False(rig.Wizard.StoreFailed);
    }

    [Fact]
    public void Cancel_while_collecting_the_agents_sends_no_more_requests()
    {
        PendingAnswer<AgentSignIn>? redeem = null;
        using var rig = SuitePairCode(out var wait, before: r => redeem = r.Sign.Redeem.Pending(honourCancel: false));
        wait.Complete(Approve("word"));
        Assert.Equal(WizardStep.Redeeming, rig.Wizard.Step);

        rig.Press(rig.Wizard.CancelCommand);
        redeem!.Complete(new AgentSignIn("excel", AgentAccess.Approved));

        Assert.Equal(new[] { "excel" }, rig.Sign.Redeemed);
        Assert.Equal(WizardStep.ServerAddress, rig.Wizard.Step);
    }

    [Fact]
    public void An_address_whose_place_cannot_be_told_is_refused_for_plain_http()
    {
        using var rig = OwnPick();
        rig.Checker.PlaceFailure = new InvalidOperationException("resolver broke");
        Type(rig, "http://nas.example.lan:11434/v1");

        UseEndpoint(rig);

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.Equal(AddressProblem.HttpsRequired, rig.Wizard.AddressError);
        Assert.Empty(rig.Checker.Checked);
    }

    // ===================================================================================================================
    // The texts (I15 step 3): the computed ones follow the state, in the rig's language (German)
    // ===================================================================================================================

    [Fact]
    public void The_waiting_line_counts_down_in_minutes_and_seconds_and_says_when_it_retries()
    {
        using var rig = PairCode();

        Assert.Equal("Warten auf Ihre Bestätigung… Der Code läuft in 10:00 ab.", rig.Wizard.WaitingText);

        rig.Clock.Advance(TimeSpan.FromSeconds(19));

        Assert.Equal("Warten auf Ihre Bestätigung… Der Code läuft in 9:41 ab.", rig.Wizard.WaitingText);
    }

    [Fact]
    public void An_address_error_reads_as_the_path_it_belongs_to()
    {
        using var server = Rig();
        server.Start();
        server.Press(server.Wizard.ChooseServerCommand);
        server.Wizard.ServerAddressText = "http://ai.example.test";
        server.Press(server.Wizard.SubmitServerCommand);

        using var own = OwnPick();
        Type(own, "http://8.8.8.8/v1");
        UseEndpoint(own);

        Assert.Equal(own.Strings.WizardServerHttpsRequired, server.Wizard.AddressErrorText);
        Assert.Equal(own.Strings.WizardEndpointHttpsRequired, own.Wizard.AddressErrorText);
        Assert.NotEqual(server.Wizard.AddressErrorText, own.Wizard.AddressErrorText);
    }

    [Fact]
    public void The_result_says_in_plain_words_what_limits_the_connection_and_whether_it_watermarks_text()
    {
        var url = new Uri("http://localhost:11434/v1");
        var report = Reports.Usable(url) with
        {
            ToolCalls = false,
            Problems = new[] { EndpointProblem.NoToolCalls, EndpointProblem.OllamaDefaultContext },
            Watermark = new EndpointWatermarkStatus { Status = TextWatermark.Never },
        };
        using var rig = OwnPick(before: r => r.Checker.Report.Returns(report));
        Type(rig, url.AbsoluteUri);

        UseEndpoint(rig);

        Assert.Equal(WizardStep.OwnResult, rig.Wizard.Step);
        Assert.Equal(new[]
        {
            "Die Verbindung funktioniert.",
            "Dieses Modell kann nur chatten: Es kann keine Werkzeuge nutzen.",
            "Ollama betreibt dieses Modell mit seinem kurzen Standardkontext, der lange Eingaben abschneidet. Erhöhen Sie OLLAMA_CONTEXT_LENGTH.",
            "Diese Verbindung kennzeichnet Texte nicht mit einem Wasserzeichen.",
        }, rig.Wizard.ResultLines);
    }

    [Fact]
    public void A_failed_check_names_its_problem_and_the_signed_in_line_names_the_person()
    {
        var url = new Uri("https://api.example.test/v1");
        using var own = OwnPick(before: r => r.Checker.Report.Returns(Reports.Failing(url, EndpointProblem.KeyRejected)));
        Type(own, url.AbsoluteUri);
        UseEndpoint(own);

        Assert.Equal("Der Server hat den Schlüssel abgelehnt.", own.Wizard.ProblemText);

        PendingAnswer<PairingApproval>? wait = null;
        using var server = PairCode(before: r => wait = r.Sign.Wait.Pending());
        wait!.Complete(Approve("main-agent"));

        Assert.Equal("Angemeldet als Ana.", server.Wizard.SignedInText);
    }

    // ===================================================================================================================
    // The key store failing, and the arrows the first pass left out (found by the TypeScript port)
    // ===================================================================================================================

    [Fact]
    public void A_key_store_that_fails_keeps_the_person_at_the_key_with_StoreFailed_and_checks_nothing()
    {
        using var rig = OwnPick();
        Pick(rig, EndpointPreset.OpenAI);
        UseEndpoint(rig);
        Assert.Equal(WizardStep.OwnKey, rig.Wizard.Step);
        rig.Keys.Failure = new InvalidOperationException("The key store is locked.");

        rig.Press(rig.Wizard.SubmitKeyCommand, "sk-test");

        Assert.Equal(WizardStep.OwnKey, rig.Wizard.Step);
        Assert.True(rig.Wizard.StoreFailed);
        Assert.False(rig.Wizard.KeySet);
        Assert.Empty(rig.Checker.Checked);
    }

    [Fact]
    public void OwnPick_to_OwnAccept_on_Back_and_OwnHelp_to_OwnPick_on_Back()
    {
        var url = new Uri("http://localhost:11434/v1");
        using var rig = OwnPick(before: r => r.Checker.Report.Returns(Reports.Failing(url, EndpointProblem.OriginRefused)));
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);
        rig.Press(rig.Wizard.ShowHelpCommand);
        Assert.Equal(WizardStep.OwnHelp, rig.Wizard.Step);

        rig.Press(rig.Wizard.BackCommand);
        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);

        rig.Press(rig.Wizard.BackCommand);
        Assert.Equal(WizardStep.OwnAccept, rig.Wizard.Step);
        Assert.False(rig.Wizard.Accepted); // accepted again, from an unticked box
    }

    [Fact]
    public void Cancel_while_the_apps_step_before_the_check_runs_goes_back_to_the_pick_and_checks_nothing()
    {
        var step = new FakeStep(WizardPoint.BeforeEndpointCheck);
        var asked = step.Script.Pending();
        using var rig = OwnPick(o => o with { Steps = new[] { step } });
        Type(rig, "https://api.example.test/v1");
        UseEndpoint(rig);
        Assert.Equal(WizardStep.BeforeCheck, rig.Wizard.Step);
        Assert.Same(step, rig.Wizard.AppStep);

        rig.Press(rig.Wizard.CancelCommand);

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
        Assert.Null(rig.Wizard.AppStep);
        Assert.Empty(rig.Checker.Checked);
    }

    // ===================================================================================================================
    // Retext: the language changes while the wizard stands somewhere
    // ===================================================================================================================

    private static readonly System.Globalization.CultureInfo English = System.Globalization.CultureInfo.GetCultureInfo("en");
    private static readonly System.Globalization.CultureInfo German = System.Globalization.CultureInfo.GetCultureInfo("de");

    /// <summary>First run, "Use my own AI", an endpoint checked with this report: in English, or in the rig's German.</summary>
    private static WizardRig Checked(EndpointReport report, bool english)
    {
        var rig = Rig();
        if (english)
        {
            rig.Strings = UiStrings.For(English);
        }
        rig.Checker.Report.Returns(report);
        rig.Start();
        rig.Press(rig.Wizard.ChooseOwnAiCommand);
        rig.Wizard.Accepted = true;
        rig.Press(rig.Wizard.AcceptCommand);
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);
        return rig;
    }

    /// <summary>Every text a skin binds, said by the wizard now.</summary>
    private static object?[] Texts(ConnectionWizardViewModel w) => new object?[]
    {
        w.Strings.Language, w.Strings.WizardServerTitle, w.Strings.WizardOwnWorks, w.AddressErrorText, w.StartFailureText,
        w.PasswordFailureText, w.ProblemText, w.WaitingText, w.SignedInText, string.Join("|", w.ResultLines), w.ConnectedText,
    };

    private static object?[] State(ConnectionWizardViewModel w) => new object?[]
    {
        w.Step, w.Outcome, w.Report, w.Problem, string.Join("|", w.Models), w.SelectedModel, w.EndpointAddressText, w.Accepted,
        w.SelectedPreset, w.IsBusy, w.AddressError, w.NeedsKey,
    };

    [Fact]
    public void Retext_a_check_result_shown_in_English_is_German_and_nothing_else_changes()
    {
        var report = Reports.Usable(new Uri("http://localhost:11434/v1")) with
        {
            ToolCalls = false,
            Problems = new[] { EndpointProblem.NoToolCalls, EndpointProblem.OllamaDefaultContext },
            Watermark = new EndpointWatermarkStatus { Status = TextWatermark.Never },
        };
        using var english = Checked(report, english: true);
        using var german = Checked(report, english: false);
        Assert.Equal(WizardStep.OwnResult, english.Wizard.Step);
        Assert.Equal("en", english.Wizard.Strings.Language);
        Assert.NotEqual(german.Wizard.ResultLines, english.Wizard.ResultLines);
        var before = State(english.Wizard);
        var changed = new List<string?>();
        english.Wizard.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        english.Wizard.Retext(German);

        Assert.Equal(Texts(german.Wizard), Texts(english.Wizard));
        Assert.Equal("Die Verbindung funktioniert.", english.Wizard.ResultLines[0]);
        Assert.Equal(before, State(english.Wizard));
        Assert.Contains(nameof(ConnectionWizardViewModel.Strings), changed);
        Assert.Contains(nameof(ConnectionWizardViewModel.ResultLines), changed);
        Assert.Contains(nameof(ConnectionWizardViewModel.ProblemText), changed);
        Assert.Contains(nameof(ConnectionWizardViewModel.AddressErrorText), changed);
        Assert.Contains(nameof(ConnectionWizardViewModel.ConnectedText), changed);

        english.Wizard.Retext(UiStrings.For(English)); // and back
        Assert.Equal("The connection works.", english.Wizard.ResultLines[0]);
    }

    [Fact]
    public void Retext_a_problem_shown_in_English_is_German()
    {
        var report = Reports.Failing(new Uri("http://localhost:11434/v1"), EndpointProblem.KeyRejected);
        using var english = Checked(report, english: true);
        using var german = Checked(report, english: false);
        Assert.Equal(WizardStep.OwnFailed, english.Wizard.Step);
        Assert.NotEqual(german.Wizard.ProblemText, english.Wizard.ProblemText);
        var before = State(english.Wizard);

        english.Wizard.Retext(German);

        Assert.Equal("Der Server hat den Schlüssel abgelehnt.", english.Wizard.ProblemText);
        Assert.Equal(Texts(german.Wizard), Texts(english.Wizard));
        Assert.Equal(before, State(english.Wizard));
    }

    [Fact]
    public void Retext_an_address_error_and_the_waiting_line_are_German_with_the_typed_text_kept()
    {
        using var own = Rig();
        own.Strings = UiStrings.For(English);
        own.Start();
        own.Press(own.Wizard.ChooseOwnAiCommand);
        own.Wizard.Accepted = true;
        own.Press(own.Wizard.AcceptCommand);
        Type(own, "http://8.8.8.8/v1");
        UseEndpoint(own);
        Assert.Equal(own.Strings.WizardEndpointHttpsRequired, own.Wizard.AddressErrorText);

        own.Wizard.Retext(German);

        Assert.Equal(UiStrings.For(German).WizardEndpointHttpsRequired, own.Wizard.AddressErrorText);
        Assert.Equal("http://8.8.8.8/v1", own.Wizard.EndpointAddressText);
        Assert.Equal(WizardStep.OwnPick, own.Wizard.Step);

        using var code = PairCode(before: r => r.Strings = UiStrings.For(English));
        Assert.StartsWith("Waiting", code.Wizard.WaitingText);
        var userCode = code.Wizard.UserCode;

        code.Wizard.Retext(German);

        Assert.Equal("Warten auf Ihre Bestätigung… Der Code läuft in 10:00 ab.", code.Wizard.WaitingText);
        Assert.Equal(userCode, code.Wizard.UserCode);
        Assert.Equal(WizardStep.PairCode, code.Wizard.Step);
    }

    [Fact]
    public void Retext_the_settings_line_names_the_connection_in_the_new_language()
    {
        using var rig = Settings();
        rig.Strings = UiStrings.For(English);
        rig.Start();
        var english = rig.Wizard.ConnectedText;

        rig.Wizard.Retext(German);

        Assert.NotNull(english);
        Assert.Equal(UiStrings.For(German).WizardConnectedServer(Org.AbsoluteUri), rig.Wizard.ConnectedText);
        Assert.NotEqual(english, rig.Wizard.ConnectedText);
    }

    // ===================================================================================================================
    // The check before the pick sends nothing to a model (F3), and the check after it looks at pictures (F2)
    // ===================================================================================================================

    private static bool IsChat(Fadenstack.Client.Tests.FakeRequest r) =>
        r.Url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal);

    /// <summary>The wizard over the real check, against a scripted Ollama that lists <paramref name="models"/>.</summary>
    private static (WizardRig Rig, Fadenstack.Client.Tests.OpenAiFake Fake) OverRealCheck(params string[] models)
    {
        var fake = new Fadenstack.Client.Tests.OpenAiFake { Models = models.ToList() };
        var rig = OwnPick();
        rig.Checker.Override = (endpoint, token, options) => new EndpointCheck(endpoint with { Handler = fake }).RunAsync(options, token);
        Pick(rig, EndpointPreset.Ollama);
        return (rig, fake);
    }

    private static void Script(Fadenstack.Client.Tests.OpenAiFake fake, string vision)
    {
        fake.Answers.Enqueue(Fadenstack.Client.Tests.OpenAiFake.Text("OK"));
        fake.Answers.Enqueue(Fadenstack.Client.Tests.OpenAiFake.Calls(("call-1", "report_ready", "{}")));
        fake.Answers.Enqueue(Fadenstack.Client.Tests.OpenAiFake.Text(vision));
    }

    private static Task PressAsync(IRelayCommand command) => ((IAsyncRelayCommand)command).ExecuteAsync(null);

    [Fact]
    public async Task With_several_models_the_check_before_the_pick_sends_exactly_one_request_and_nothing_to_chat_completions()
    {
        var (rig, fake) = OverRealCheck("m1", "m2");
        using var _ = rig;

        await PressAsync(rig.Wizard.UseEndpointCommand);

        Assert.Equal(WizardStep.OwnModel, rig.Wizard.Step);
        Assert.Equal(new[] { "m1", "m2" }, rig.Wizard.Models);
        Assert.Null(rig.Wizard.SelectedModel);
        var only = Assert.Single(fake.Requests);
        Assert.Equal("/v1/models", only.Url.AbsolutePath);
        Assert.DoesNotContain(fake.Requests, IsChat);
    }

    [Fact]
    public async Task After_the_pick_the_probes_go_to_the_picked_model_only_and_vision_is_probed()
    {
        var (rig, fake) = OverRealCheck("m1", "m2");
        using var _ = rig;
        await PressAsync(rig.Wizard.UseEndpointCommand);
        Script(fake, "Green");

        rig.Wizard.SelectedModel = "m2";
        await PressAsync(rig.Wizard.ContinueCommand);

        Assert.Equal(WizardStep.OwnResult, rig.Wizard.Step);
        var chats = fake.Requests.Where(IsChat).ToList();
        Assert.Equal(3, chats.Count); // one chat probe, one tool probe, one vision probe
        Assert.All(chats, r => Assert.Equal("m2", r.Json.GetProperty("model").GetString()));
        Assert.Contains(chats, r => r.Body.Contains("image_url"));
        Assert.Equal("m2", rig.Wizard.Report!.Model);
        Assert.True(rig.Wizard.Report.Vision);
        await PressAsync(rig.Wizard.ContinueCommand);
        var saved = Assert.Single(rig.Store.Saved);
        Assert.Equal("m2", saved.Endpoint!.Model);
        Assert.True(saved.Report!.Vision);
    }

    [Fact]
    public async Task With_one_model_it_is_taken_and_checked_at_once()
    {
        var (rig, fake) = OverRealCheck("only");
        using var _ = rig;
        Script(fake, "Green");

        await PressAsync(rig.Wizard.UseEndpointCommand);

        Assert.Equal(WizardStep.OwnResult, rig.Wizard.Step);
        Assert.Equal("only", rig.Wizard.SelectedModel);
        Assert.All(fake.Requests.Where(IsChat), r => Assert.Equal("only", r.Json.GetProperty("model").GetString()));
        Assert.True(rig.Wizard.Report!.Chat);
        Assert.True(rig.Wizard.Report.Vision);
    }

    [Fact]
    public async Task A_listing_that_says_the_model_takes_images_decides_without_a_picture_request()
    {
        var (rig, fake) = OverRealCheck("only");
        using var _ = rig;
        fake.ModelFields["only"] = new() { ["architecture"] = new { input_modalities = new[] { "text", "image" } } };
        fake.Answers.Enqueue(Fadenstack.Client.Tests.OpenAiFake.Text("OK"));
        fake.Answers.Enqueue(Fadenstack.Client.Tests.OpenAiFake.Calls(("call-1", "report_ready", "{}")));

        await PressAsync(rig.Wizard.UseEndpointCommand);

        Assert.True(rig.Wizard.Report!.Vision);
        Assert.DoesNotContain(fake.Requests, r => r.Body.Contains("image_url"));
    }

    [Fact]
    public void Every_check_the_wizard_runs_asks_for_the_picture_probe()
    {
        using var rig = OwnPick();
        Pick(rig, EndpointPreset.Ollama);
        UseEndpoint(rig);

        Assert.True(Assert.Single(rig.Checker.CheckOptions)!.ProbeVision);
    }

    [Fact]
    public async Task Back_from_the_pick_goes_to_the_address_step_when_no_model_was_checked_yet()
    {
        var (rig, _) = OverRealCheck("m1", "m2");
        using var _ = rig;
        await PressAsync(rig.Wizard.UseEndpointCommand);

        rig.Press(rig.Wizard.BackCommand);

        Assert.Equal(WizardStep.OwnPick, rig.Wizard.Step);
    }

    [Fact]
    public void Retext_needs_a_language()
    {
        using var rig = Rig();
        Assert.Throws<ArgumentNullException>(() => rig.Wizard.Retext((System.Globalization.CultureInfo)null!));
        Assert.Throws<ArgumentNullException>(() => rig.Wizard.Retext((UiStrings)null!));
    }
}
