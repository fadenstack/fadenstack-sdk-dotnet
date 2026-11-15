using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Endpoints;
using Fadenstack.UI.Tests;
using Fadenstack.UI.Wizard;
using Xunit;

namespace Fadenstack.UI.Wpf.Tests;

/// <summary>
/// The connection wizard's WPF view (sdk/51 F15): every step of the view model draws its panel in English and German without a
/// binding error, every input and button on screen has a name for screen readers, Enter and Esc and the focus work as the view
/// model says, and a password or a key goes to its command as the parameter and is gone from the box right after, without a
/// binding on the box.
/// </summary>
public class WizardViewTests
{
    private static readonly ConnectionResult EndpointConnection = new()
    {
        Mode = ConnectionMode.Endpoint,
        Endpoint = new ModelEndpoint { BaseUrl = new Uri("https://api.openai.com/v1"), Model = "gpt-test" },
    };

    private static readonly ConnectionResult ServerConnection = new()
    {
        Mode = ConnectionMode.Server, Server = new Uri("https://ai.example.test/"), Agents = new[] { new AgentSignIn("main-agent", AgentAccess.Approved) },
    };

    public static IEnumerable<object[]> StepsInBothLanguages() =>
        Enum.GetValues(typeof(WizardStep)).Cast<WizardStep>().SelectMany(step => new[] { "en", "de" }.Select(language => new object[] { step, language }));

    /// <summary>A real view model, with every offer on, shown at <paramref name="step"/> with the state that step reads.</summary>
    internal static WizardHost At(WizardStep step, string language)
    {
        var host = new WizardHost(o => o with { Current = EndpointConnection }, language);
        var endpoint = new Uri("http://localhost:11434/v1");
        switch (step)
        {
            case WizardStep.StartFailed:
                host.Set(nameof(ConnectionWizardViewModel.StartFailure), StartFailure.Unreachable);
                break;
            case WizardStep.PasswordError:
                host.Set(nameof(ConnectionWizardViewModel.PasswordFailure), PasswordFailure.BadCredentials);
                break;
            case WizardStep.OwnFailed:
                host.Set(nameof(ConnectionWizardViewModel.Problem), EndpointProblem.Unreachable);
                break;
            case WizardStep.OwnPick:
                host.Set(nameof(ConnectionWizardViewModel.Found), new[] { new LocalEndpoint { BaseUrl = endpoint, Preset = EndpointPreset.Ollama } });
                break;
            case WizardStep.OwnResult:
                host.Set(nameof(ConnectionWizardViewModel.Report), Reports.Usable(endpoint));
                break;
            case WizardStep.OwnModel:
                host.Set(nameof(ConnectionWizardViewModel.Models), new[] { "m1", "m2" });
                break;
            case WizardStep.PairCode:
                host.Set(nameof(ConnectionWizardViewModel.UserCode), "BDWP-HTQX");
                host.Set(nameof(ConnectionWizardViewModel.ServerUrl), new Uri("https://ai.example.test/"));
                break;
        }
        host.Show(step);
        return host;
    }

    /// <summary>What the step says first, in the language: a headline or its message (null where the step only shows progress or nothing).</summary>
    private static string? Said(WizardHost host, WizardStep step)
    {
        var s = host.Strings;
        var wizard = host.Wizard;
        switch (step)
        {
            case WizardStep.Choose: return s.WizardChooseServer;
            case WizardStep.ServerAddress: return s.WizardServerTitle;
            case WizardStep.Checking: case WizardStep.AppPrecondition: case WizardStep.ServerAccepted: return s.WizardChecking;
            case WizardStep.PairStarting: return s.WizardStarting;
            case WizardStep.Approved: case WizardStep.Redeeming: case WizardStep.AfterConnect: return s.WizardRedeeming;
            case WizardStep.OwnSearching: return s.WizardOwnSearching;
            case WizardStep.OwnChecking: case WizardStep.BeforeCheck: return s.WizardOwnChecking;
            case WizardStep.OwnDone: return s.WizardOwnDone;
            case WizardStep.Unreachable: return s.WizardUnreachable;
            case WizardStep.CertificateNotTrusted: return s.WizardCertificateNotTrusted;
            case WizardStep.NotFadenstack: return s.WizardNotFadenstack;
            case WizardStep.StartFailed: return wizard.StartFailureText;
            case WizardStep.ServerTooOld: return s.WizardTooOld;
            case WizardStep.Denied: return s.WizardDenied;
            case WizardStep.Expired: return s.WizardExpired;
            case WizardStep.PairFailed: return s.WizardPairFailed;
            case WizardStep.NoAccess: return s.WizardNoAccess;
            case WizardStep.PasswordError: return wizard.PasswordFailureText;
            case WizardStep.OwnFailed: return wizard.ProblemText;
            case WizardStep.PairCode: return s.WizardCodeIntro;
            case WizardStep.Password: return s.WizardEmail;
            case WizardStep.SignedIn: return wizard.SignedInText;
            case WizardStep.OwnAccept: return s.WizardOwnDataFlow;
            case WizardStep.OwnPick: return s.WizardOwnPickTitle;
            case WizardStep.OwnPlainHttp: return s.WizardPlainHttpWarning;
            case WizardStep.OwnKey: return s.WizardKeyLabel;
            case WizardStep.OwnHelp: return s.WizardOwnHelp;
            case WizardStep.OwnResult: return s.WizardOwnWorks;
            case WizardStep.OwnModel: return s.WizardModelLabel;
            case WizardStep.Settings: return wizard.ConnectedText;
            case WizardStep.SignOut: return s.WizardRemoveTitle;
            default: return null;
        }
    }

    private static string? SaidInOther(UiStrings other, WizardStep step)
    {
        switch (step)
        {
            case WizardStep.Choose: return other.WizardChooseServer;
            case WizardStep.ServerAddress: return other.WizardServerTitle;
            case WizardStep.Unreachable: return other.WizardUnreachable;
            case WizardStep.Denied: return other.WizardDenied;
            case WizardStep.Expired: return other.WizardExpired;
            case WizardStep.PairCode: return other.WizardCodeIntro;
            case WizardStep.Password: return other.WizardEmail;
            case WizardStep.OwnAccept: return other.WizardOwnDataFlow;
            case WizardStep.OwnKey: return other.WizardKeyLabel;
            case WizardStep.OwnModel: return other.WizardModelLabel;
            default: return null;
        }
    }

    [Theory]
    [MemberData(nameof(StepsInBothLanguages))]
    public void Every_step_draws_its_panel_in_both_languages_without_a_binding_error(WizardStep step, string language) => Sta.Run(() =>
    {
        using var host = At(step, language);

        Assert.Empty(host.BindingErrors);
        var texts = host.Texts();
        var said = Said(host, step);
        if (said is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(said), $"{step}: the view model has no text for it in {language}");
            Assert.Contains(said, texts);
        }
        else if (step == WizardStep.Finished)
        {
            Assert.DoesNotContain(host.Visible(), e => e is Button);
            Assert.Empty(texts);
        }
        else
        {
            Assert.Contains(host.Visible(), e => e is ProgressBar); // BeforeChoice: the app's own step runs, with nothing to say
        }
        // Not the other language's text.
        var other = UiStrings.For(CultureInfo.GetCultureInfo(language == "en" ? "de" : "en"));
        if (SaidInOther(other, step) is { } wrong)
        {
            Assert.DoesNotContain(wrong, texts);
        }
    });

    [Theory]
    [MemberData(nameof(StepsInBothLanguages))]
    public void Every_input_and_button_on_screen_has_a_name_in_every_step(WizardStep step, string language) => Sta.Run(() =>
    {
        using var host = At(step, language);

        var failures = new List<string>();
        foreach (var element in host.Controls())
        {
            var what = $"{step}/{language}: {element.GetType().Name} {element.Name}";
            var name = AutomationProperties.GetName(element);
            if (string.IsNullOrWhiteSpace(name))
            {
                failures.Add(what + " has no automation name");
            }
            else if (element is Button { Content: string shown } && name != shown)
            {
                failures.Add(what + $" is named '{name}' but says '{shown}'");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    });

    [Fact]
    public void The_steps_between_them_check_a_good_number_of_controls() => Sta.Run(() =>
    {
        var counted = 0;
        foreach (var step in Enum.GetValues(typeof(WizardStep)).Cast<WizardStep>())
        {
            using var host = new WizardHost(o => o with { Current = ServerConnection });
            host.Show(step);
            counted += host.Controls().Count();
        }
        Assert.True(counted > 40, $"only {counted} controls were checked");
    });

    [Fact]
    public void The_banners_the_waiting_line_and_the_progress_are_live_or_named() => Sta.Run(() =>
    {
        using var host = new WizardHost();
        host.Show(WizardStep.Unreachable);
        Assert.Contains(host.Visible(), e => AutomationProperties.GetLiveSetting(e) == AutomationLiveSetting.Assertive);

        host.Set(nameof(ConnectionWizardViewModel.UserCode), "BDWP-HTQX");
        host.Show(WizardStep.PairCode);
        var waiting = host.Visible().OfType<TextBlock>().First(t => t.Text == host.Wizard.WaitingText);
        // The countdown changes every second: not a live region, or a screen reader would read it out each time.
        Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(waiting));
        Assert.Equal(host.Strings.WizardCodeName, AutomationProperties.GetName(host.Find<TextBox>("CodeText")));

        host.Show(WizardStep.Checking);
        Assert.All(host.Visible().OfType<ProgressBar>(), bar => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(bar))));
    });

    [Fact]
    public void The_binding_error_capture_sees_a_broken_binding() => Sta.Run(() =>
    {
        using var host = new WizardHost();
        var broken = new TextBlock { DataContext = host.Wizard };
        broken.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NoSuchProperty"));
        host.Window.Content = broken;
        host.Pump();

        Assert.NotEmpty(host.BindingErrors);
    });

    [Fact]
    public void The_choice_shows_what_is_offered_and_the_titles_take_the_apps_name() => Sta.Run(() =>
    {
        using var host = new WizardHost(o => o with { OffersDemo = false });
        host.Show(WizardStep.Choose);

        Assert.Equal(host.Strings.WizardChooseTitle("Test app"), host.Find<TextBlock>("ChooseTitle").Text);
        Assert.Contains(host.Strings.WizardChooseOwnAi, host.Texts());
        Assert.DoesNotContain(host.Strings.WizardChooseDemo, host.Texts());
        Assert.DoesNotContain(host.Strings.WizardNothingAllowed, host.Texts());

        host.View.AppName = "Faden Writer";
        host.Pump();
        Assert.Equal(host.Strings.WizardChooseTitle("Faden Writer"), host.Find<TextBlock>("ChooseTitle").Text);
    });

    [Fact]
    public void When_the_settings_of_the_computer_allow_nothing_the_choice_says_so() => Sta.Run(() =>
    {
        using var host = new WizardHost(o => o with { OffersServer = false, OffersOwnAi = false, OffersDemo = false });
        host.Show(WizardStep.Choose);

        Assert.Contains(host.Strings.WizardNothingAllowed, host.Texts());
        Assert.DoesNotContain(host.Visible(), e => e is Button);
    });

    [Fact]
    public void The_terms_are_unticked_and_the_binding_version_is_said_in_every_language_but_german() => Sta.Run(() =>
    {
        using var english = new WizardHost(language: "en");
        english.Show(WizardStep.OwnAccept);
        Assert.Contains(english.Strings.WizardOwnBindingVersion, english.Texts());
        Assert.Contains(english.Strings.WizardOwnNotice, english.Texts());
        Assert.Contains(english.Strings.WizardOwnLiability, english.Texts());
        var boxes = english.Visible().OfType<CheckBox>().ToList();
        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, box => Assert.False(box.IsChecked));
        Assert.False(english.Wizard.AcceptCommand.CanExecute(null));

        boxes[0].IsChecked = true;
        Assert.True(english.Wizard.Accepted);
        Assert.True(english.Wizard.AcceptCommand.CanExecute(null));

        using var german = new WizardHost(language: "de");
        german.Show(WizardStep.OwnAccept);
        Assert.DoesNotContain(german.Strings.WizardOwnBindingVersion, german.Texts());
    });

    [Fact]
    public void The_search_consent_shows_only_where_the_app_can_search_this_computer_and_the_full_terms_only_with_a_link() => Sta.Run(() =>
    {
        using var host = new WizardHost(o => o with { CanSearchThisComputer = false });
        host.Show(WizardStep.OwnAccept);
        Assert.Single(host.Visible().OfType<CheckBox>());
        var terms = host.Find<Button>("FullTermsButton");
        Assert.False(terms.IsVisible);

        Uri? opened = null;
        host.View.LinkOpener = link => opened = link;
        host.View.FullTermsLink = new Uri("https://fadenstack.com/terms");
        host.Pump();
        Assert.True(terms.IsVisible);
        Invoke(host, terms);
        Assert.Equal(new Uri("https://fadenstack.com/terms"), opened);
    });

    [Fact]
    public void Sign_out_says_remove_for_an_endpoint_and_sign_out_for_a_server() => Sta.Run(() =>
    {
        using (var endpoint = new WizardHost(o => o with { Current = EndpointConnection }))
        {
            endpoint.Show(WizardStep.SignOut);
            Assert.Contains(endpoint.Strings.WizardRemoveTitle, endpoint.Texts());
            Assert.DoesNotContain(endpoint.Strings.WizardSignOutTitle, endpoint.Texts());
        }
        using var server = new WizardHost(o => o with { Current = ServerConnection });
        server.Show(WizardStep.SignOut);
        Assert.Contains(server.Strings.WizardSignOutTitle, server.Texts());
        Assert.Contains(server.Strings.WizardSignOutBody, server.Texts());
        Assert.DoesNotContain(server.Strings.WizardRemoveTitle, server.Texts());
        Assert.False(server.Wizard.DeleteLocalChats);
        server.Visible().OfType<CheckBox>().Single().IsChecked = true;
        Assert.True(server.Wizard.DeleteLocalChats);
    });

    [Fact]
    public void An_app_step_is_drawn_by_the_apps_own_template_while_it_runs() => Sta.Run(() =>
    {
        var step = new FakeStep(WizardPoint.BeforeChoice);
        var running = step.Script.Pending();
        using var host = new WizardHost(o => o with { Steps = new IWizardStep[] { step } });
        var template = new DataTemplate(typeof(FakeStep)) { VisualTree = new FrameworkElementFactory(typeof(TextBlock)) };
        template.VisualTree.SetValue(TextBlock.TextProperty, "The licence");
        host.View.Resources.Add(new DataTemplateKey(typeof(FakeStep)), template);
        _ = host.Wizard.StartAsync();
        host.Pump();

        Assert.Equal(WizardStep.BeforeChoice, host.Wizard.Step);
        Assert.NotNull(host.Wizard.AppStep);
        Assert.Contains("The licence", host.Texts());
        running.Complete(true);
        host.Pump();
        Assert.Equal(WizardStep.Choose, host.Wizard.Step);
        Assert.DoesNotContain("The licence", host.Texts());
    });

    // ---- The keyboard -----------------------------------------------------------------------------------------

    [Fact]
    public void Enter_in_the_address_box_runs_the_submit_command() => Sta.Run(() =>
    {
        using var host = new WizardHost(o => o with { OffersOwnAi = false, OffersDemo = false }, start: true);
        Assert.Equal(WizardStep.ServerAddress, host.Wizard.Step);
        var box = host.Find<TextBox>("ServerBox");
        box.Text = "https://ai.example.test"; // bound as it is typed, so Enter finds it in the view model
        box.Focus();
        host.Pump();

        host.Press(box, Key.Enter);

        Assert.Single(host.Sign.Checked);
        Assert.Equal("https://ai.example.test/", host.Sign.Checked[0].AbsoluteUri);
        Assert.Equal(WizardStep.PairCode, host.Wizard.Step);
    });

    [Fact]
    public void Esc_on_a_busy_step_runs_cancel_and_on_a_step_with_a_back_goes_back() => Sta.Run(() =>
    {
        using var host = new WizardHost(start: true);
        Assert.Equal(WizardStep.Choose, host.Wizard.Step);
        host.Wizard.ChooseServerCommand.Execute(null);
        host.Pump();
        Assert.Equal(WizardStep.ServerAddress, host.Wizard.Step);

        var checking = host.Sign.Check.Pending();
        host.Find<TextBox>("ServerBox").Text = "https://ai.example.test";
        host.Wizard.SubmitServerCommand.Execute(null);
        host.Pump();
        Assert.Equal(WizardStep.Checking, host.Wizard.Step);
        Assert.True(host.Wizard.IsBusy);
        Assert.True(host.Wizard.CancelCommand.CanExecute(null));

        host.Press(host.View, Key.Escape);
        Assert.Equal(WizardStep.ServerAddress, host.Wizard.Step);
        Assert.True(checking.Token?.IsCancellationRequested);

        Assert.True(host.Wizard.BackCommand.CanExecute(null));
        host.Press(host.View, Key.Escape);
        Assert.Equal(WizardStep.Choose, host.Wizard.Step);
    });

    [Fact]
    public void The_focus_goes_to_the_first_field_of_each_step() => Sta.Run(() =>
    {
        using var host = new WizardHost(o => o with { OffersDemo = false });
        host.Show(WizardStep.ServerAddress);
        Assert.Same(host.Find<TextBox>("ServerBox"), host.Focused());

        host.Show(WizardStep.Password);
        Assert.Same(host.Find<TextBox>("EmailBox"), host.Focused());

        host.Show(WizardStep.OwnKey);
        Assert.Same(host.Find<PasswordBox>("KeyBox"), host.Focused());

        host.Show(WizardStep.OwnPick);
        Assert.Same(host.Find<TextBox>("EndpointBox"), host.Focused());

        host.Show(WizardStep.OwnAccept);
        Assert.Same(host.Find<ScrollViewer>("NoticeScroller"), host.Focused());

        host.Show(WizardStep.Choose);
        Assert.IsType<Button>(host.Focused());
        Assert.Contains(host.Strings.WizardChooseServer, host.Texts());

        host.Show(WizardStep.Checking);
        var cancel = Assert.IsType<Button>(host.Focused());
        Assert.Equal(host.Strings.WizardCancel, cancel.Content);
    });

    // ---- A password and a key never stay ------------------------------------------------------------------------

    /// <summary>First start with a server, sign-in with a code shown, then "sign in with e-mail and password".</summary>
    private static WizardHost AtPassword()
    {
        var host = new WizardHost(o => o with { OffersOwnAi = false, OffersDemo = false }, start: true);
        host.Wizard.ServerAddressText = "https://ai.example.test";
        host.Wizard.SubmitServerCommand.Execute(null);
        host.Pump();
        Assert.Equal(WizardStep.PairCode, host.Wizard.Step);
        host.Wizard.UsePasswordCommand.Execute(null);
        host.Pump();
        Assert.Equal(WizardStep.Password, host.Wizard.Step);
        return host;
    }

    [Fact]
    public void The_password_goes_to_the_command_as_its_parameter_and_the_box_is_empty_after() => Sta.Run(() =>
    {
        using var host = AtPassword();
        var pending = host.Sign.Password.Pending();
        var email = host.Find<TextBox>("EmailBox");
        var password = host.Find<PasswordBox>("PasswordBox");
        email.Text = "ana@example.test";
        password.Password = "correct horse";
        Assert.Equal("ana@example.test", host.Wizard.Email); // the e-mail is bound; the password is not

        Invoke(host, host.Find<Button>("SignInButton"));

        var call = Assert.Single(host.Sign.PasswordsTried);
        Assert.Equal("ana@example.test", call.Email);
        Assert.Equal("correct horse", call.Password);
        Assert.Equal("", password.Password);
        Assert.True(host.Wizard.IsBusy);
        Assert.True(host.Wizard.CancelCommand.CanExecute(null));
        Assert.DoesNotContain("correct horse", Everything(host.Wizard));
        pending.Fail(new FadenstackBadCredentialsException(System.Net.HttpStatusCode.Unauthorized, "no"));
        host.Pump();
        Assert.Equal(WizardStep.PasswordError, host.Wizard.Step);
        Assert.Equal("", password.Password);
    });

    [Fact]
    public void Enter_in_the_password_box_sends_it_and_Enter_in_the_empty_password_goes_on_to_it_from_the_e_mail() => Sta.Run(() =>
    {
        using var host = AtPassword();
        host.Sign.Password.Pending();
        var email = host.Find<TextBox>("EmailBox");
        var password = host.Find<PasswordBox>("PasswordBox");
        email.Text = "ana@example.test";
        email.Focus();
        host.Pump();

        host.Press(email, Key.Enter);
        Assert.Empty(host.Sign.PasswordsTried);
        Assert.Same(password, host.Focused());

        password.Password = "correct horse";
        host.Press(password, Key.Enter);
        var call = Assert.Single(host.Sign.PasswordsTried);
        Assert.Equal("correct horse", call.Password);
        Assert.Equal("", password.Password);
    });

    [Fact]
    public void The_key_goes_to_the_key_store_through_the_command_and_the_box_is_empty_after() => Sta.Run(() =>
    {
        using var host = new WizardHost(o => o with { OffersServer = false, OffersDemo = false }, start: true);
        Assert.Equal(WizardStep.OwnAccept, host.Wizard.Step);
        host.Wizard.Accepted = true;
        host.Wizard.AcceptCommand.Execute(null);
        host.Pump();
        Assert.Equal(WizardStep.OwnPick, host.Wizard.Step);
        host.Wizard.SelectPresetCommand.Execute(EndpointPreset.OpenAI);
        host.Wizard.UseEndpointCommand.Execute(null);
        host.Pump();
        Assert.Equal(WizardStep.OwnKey, host.Wizard.Step);

        var key = host.Find<PasswordBox>("KeyBox");
        key.Password = "sk-test-0123456789";
        Assert.Same(key, host.Focused());
        host.Press(key, Key.Enter);

        var saved = Assert.Single(host.Keys.Saved);
        Assert.Equal("sk-test-0123456789", saved.Key);
        Assert.Equal("", key.Password);
        Assert.DoesNotContain("sk-test-0123456789", Everything(host.Wizard));
    });

    [Fact]
    public void No_box_for_a_secret_is_bound_and_every_secret_box_is_a_password_box() => Sta.Run(() =>
    {
        var xaml = File.ReadAllText(Path.Combine(SkinFolder(), "Views", "WizardView.xaml"));
        var secret = Regex.Matches(xaml, @"<PasswordBox\b[^>]*>").Select(m => m.Value).ToList();
        Assert.Equal(2, secret.Count); // the password and the key
        Assert.All(secret, box => Assert.DoesNotMatch(@"\bPassword\s*=", box)); // a PasswordBox cannot be bound anyway; never an attribute
        Assert.Equal(2, Regex.Matches(xaml, @"b:WizardSecret\.Box=""\{Binding ElementName=").Count);
    });

    /// <summary>Every text the view model shows through its public properties, to find a secret that stayed there.</summary>
    private static string Everything(ConnectionWizardViewModel wizard)
    {
        var all = new List<string>();
        foreach (var property in typeof(ConnectionWizardViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetIndexParameters().Length == 0 && p.Name != nameof(ConnectionWizardViewModel.Strings)))
        {
            try
            {
                all.Add(property.GetValue(wizard)?.ToString() ?? "");
            }
            catch (Exception)
            {
                // a property that cannot be read cannot hold a secret in the view
            }
        }
        return string.Join("\n", all);
    }

    private static string SkinFolder()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fadenstack.slnx")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "src", "Fadenstack.UI.Wpf");
    }

    /// <summary>What a screen reader's "invoke" does to a button; WPF runs it a moment later, on the dispatcher.</summary>
    private static void Invoke(WizardHost host, Button button)
    {
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
        invoke.Invoke();
        host.Pump();
    }
}
