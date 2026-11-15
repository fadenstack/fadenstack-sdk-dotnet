using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Fadenstack.Agents;
using Fadenstack.Auth;
using Fadenstack.Endpoints;
using Fadenstack.UI.Tests;
using Fadenstack.UI.Wizard;

namespace Fadenstack.UI.Avalonia.Tests;

/// <summary>
/// The connection wizard's Avalonia view (sdk/51 F15): every step of the view model draws its panel in English and German
/// without a binding error, Enter and Esc and the focus work as the view model says, and a password or a key goes to its
/// command as the parameter and is gone from the box right after, without a binding on the box.
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

    public static TheoryData<WizardStep, string> StepsInBothLanguages()
    {
        var data = new TheoryData<WizardStep, string>();
        foreach (var step in Enum.GetValues<WizardStep>())
        {
            foreach (var language in new[] { "en", "de" })
            {
                data.Add(step, language);
            }
        }
        return data;
    }

    /// <summary>A real view model, with every offer on, shown at <paramref name="step"/> with the state that step reads.</summary>
    internal static async Task<WizardHost> AtAsync(WizardStep step, string language)
    {
        var host = await WizardHost.OpenAsync(o => o with { Current = EndpointConnection }, language);
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
        await host.ShowAsync(step);
        return host;
    }

    /// <summary>What the step says first, in the language: a headline or its message (null where the step only shows progress or nothing).</summary>
    private static string? Said(WizardHost host, WizardStep step)
    {
        var s = host.Strings;
        var wizard = host.Wizard;
        return step switch
        {
            WizardStep.Choose => s.WizardChooseServer,
            WizardStep.ServerAddress => s.WizardServerTitle,
            WizardStep.Checking or WizardStep.AppPrecondition or WizardStep.ServerAccepted => s.WizardChecking,
            WizardStep.PairStarting => s.WizardStarting,
            WizardStep.Approved or WizardStep.Redeeming or WizardStep.AfterConnect => s.WizardRedeeming,
            WizardStep.OwnSearching => s.WizardOwnSearching,
            WizardStep.OwnChecking or WizardStep.BeforeCheck => s.WizardOwnChecking,
            WizardStep.OwnDone => s.WizardOwnDone,
            WizardStep.Unreachable => s.WizardUnreachable,
            WizardStep.CertificateNotTrusted => s.WizardCertificateNotTrusted,
            WizardStep.NotFadenstack => s.WizardNotFadenstack,
            WizardStep.StartFailed => wizard.StartFailureText,
            WizardStep.ServerTooOld => s.WizardTooOld,
            WizardStep.Denied => s.WizardDenied,
            WizardStep.Expired => s.WizardExpired,
            WizardStep.PairFailed => s.WizardPairFailed,
            WizardStep.NoAccess => s.WizardNoAccess,
            WizardStep.PasswordError => wizard.PasswordFailureText,
            WizardStep.OwnFailed => wizard.ProblemText,
            WizardStep.PairCode => s.WizardCodeIntro,
            WizardStep.Password => s.WizardEmail,
            WizardStep.SignedIn => wizard.SignedInText,
            WizardStep.OwnAccept => s.WizardOwnDataFlow,
            WizardStep.OwnPick => s.WizardOwnPickTitle,
            WizardStep.OwnPlainHttp => s.WizardPlainHttpWarning,
            WizardStep.OwnKey => s.WizardKeyLabel,
            WizardStep.OwnHelp => s.WizardOwnHelp,
            WizardStep.OwnResult => s.WizardOwnWorks,
            WizardStep.OwnModel => s.WizardModelLabel,
            WizardStep.Settings => wizard.ConnectedText,
            WizardStep.SignOut => s.WizardRemoveTitle,
            _ => null,
        };
    }

    [AvaloniaTheory]
    [MemberData(nameof(StepsInBothLanguages))]
    public async Task Every_step_draws_its_panel_in_both_languages_without_a_binding_error(WizardStep step, string language)
    {
        using var host = await AtAsync(step, language);

        Assert.Empty(host.BindingErrors);
        var texts = host.Texts();
        if (Said(host, step) is { } said)
        {
            Assert.False(string.IsNullOrWhiteSpace(said), $"{step}: the view model has no text for it in {language}");
            Assert.Contains(said, texts);
        }
        else if (step == WizardStep.Finished)
        {
            Assert.Empty(host.Visible().OfType<Button>());
            Assert.Empty(texts);
        }
        else
        {
            Assert.NotEmpty(host.Visible().OfType<ProgressBar>()); // BeforeChoice: the app's own step runs, with nothing to say
        }
        // Not the other language's text.
        var other = UiStrings.For(System.Globalization.CultureInfo.GetCultureInfo(language == "en" ? "de" : "en"));
        if (SaidInOther(other, step) is { } wrong)
        {
            Assert.DoesNotContain(wrong, texts);
        }
    }

    private static string? SaidInOther(UiStrings other, WizardStep step) => step switch
    {
        WizardStep.Choose => other.WizardChooseServer,
        WizardStep.ServerAddress => other.WizardServerTitle,
        WizardStep.Unreachable => other.WizardUnreachable,
        WizardStep.Denied => other.WizardDenied,
        WizardStep.Expired => other.WizardExpired,
        WizardStep.PairCode => other.WizardCodeIntro,
        WizardStep.Password => other.WizardEmail,
        WizardStep.OwnAccept => other.WizardOwnDataFlow,
        WizardStep.OwnKey => other.WizardKeyLabel,
        WizardStep.OwnModel => other.WizardModelLabel,
        _ => null,
    };

    [AvaloniaFact]
    public async Task The_binding_error_capture_sees_a_broken_binding()
    {
        using var host = await WizardHost.OpenAsync();
        var broken = new TextBlock { DataContext = host.Wizard };
        broken.Bind(TextBlock.TextProperty, new global::Avalonia.Data.ReflectionBinding("NoSuchProperty"));
        host.Window.Content = broken;
        await host.PumpAsync();

        Assert.NotEmpty(host.BindingErrors);
    }

    [AvaloniaFact]
    public async Task The_choice_shows_what_is_offered_and_the_titles_take_the_apps_name()
    {
        using var host = await WizardHost.OpenAsync(o => o with { OffersDemo = false });
        await host.ShowAsync(WizardStep.Choose);

        Assert.Equal(host.Strings.WizardChooseTitle("Test app"), host.Find<TextBlock>("ChooseTitle").Text);
        Assert.Contains(host.Strings.WizardChooseOwnAi, host.Texts());
        Assert.DoesNotContain(host.Strings.WizardChooseDemo, host.Texts());
        Assert.DoesNotContain(host.Strings.WizardNothingAllowed, host.Texts());

        host.View.AppName = "Faden Writer";
        await host.PumpAsync();
        Assert.Equal(host.Strings.WizardChooseTitle("Faden Writer"), host.Find<TextBlock>("ChooseTitle").Text);
    }

    [AvaloniaFact]
    public async Task When_the_settings_of_the_computer_allow_nothing_the_choice_says_so()
    {
        using var host = await WizardHost.OpenAsync(o => o with
        {
            OffersServer = false, OffersOwnAi = false, OffersDemo = false,
        });
        await host.ShowAsync(WizardStep.Choose);

        Assert.Contains(host.Strings.WizardNothingAllowed, host.Texts());
        Assert.DoesNotContain(host.Visible(), c => c is Button && c.Classes.Contains("choice"));
    }

    [AvaloniaFact]
    public async Task The_terms_are_unticked_and_the_binding_version_is_said_in_every_language_but_german()
    {
        using var english = await WizardHost.OpenAsync(language: "en");
        await english.ShowAsync(WizardStep.OwnAccept);
        Assert.Contains(english.Strings.WizardOwnBindingVersion, english.Texts());
        Assert.Contains(english.Strings.WizardOwnNotice, english.Texts());
        Assert.Contains(english.Strings.WizardOwnLiability, english.Texts());
        Assert.All(english.Visible().OfType<CheckBox>(), box => Assert.False(box.IsChecked));
        Assert.Equal(2, english.Visible().OfType<CheckBox>().Count());
        Assert.False(english.Wizard.AcceptCommand.CanExecute(null));

        english.Visible().OfType<CheckBox>().First().IsChecked = true;
        Assert.True(english.Wizard.Accepted);
        Assert.True(english.Wizard.AcceptCommand.CanExecute(null));

        using var german = await WizardHost.OpenAsync(language: "de");
        await german.ShowAsync(WizardStep.OwnAccept);
        Assert.DoesNotContain(german.Strings.WizardOwnBindingVersion, german.Texts());
    }

    [AvaloniaFact]
    public async Task The_search_consent_shows_only_where_the_app_can_search_this_computer_and_the_full_terms_only_with_a_link()
    {
        using var host = await WizardHost.OpenAsync(o => o with { CanSearchThisComputer = false });
        await host.ShowAsync(WizardStep.OwnAccept);
        Assert.Single(host.Visible().OfType<CheckBox>());
        var terms = host.Find<Button>("FullTermsButton");
        Assert.False(terms.IsEffectivelyVisible);

        Uri? opened = null;
        host.View.LinkOpener = link => opened = link;
        host.View.FullTermsLink = new Uri("https://fadenstack.com/terms");
        await host.PumpAsync();
        Assert.True(terms.IsEffectivelyVisible);
        Invoke(terms);
        Assert.Equal(new Uri("https://fadenstack.com/terms"), opened);
    }

    [AvaloniaFact]
    public async Task Sign_out_says_remove_for_an_endpoint_and_sign_out_for_a_server()
    {
        using var endpoint = await WizardHost.OpenAsync(o => o with { Current = EndpointConnection });
        await endpoint.ShowAsync(WizardStep.SignOut);
        Assert.Contains(endpoint.Strings.WizardRemoveTitle, endpoint.Texts());
        Assert.DoesNotContain(endpoint.Strings.WizardSignOutTitle, endpoint.Texts());

        using var server = await WizardHost.OpenAsync(o => o with { Current = ServerConnection });
        await server.ShowAsync(WizardStep.SignOut);
        Assert.Contains(server.Strings.WizardSignOutTitle, server.Texts());
        Assert.Contains(server.Strings.WizardSignOutBody, server.Texts());
        Assert.DoesNotContain(server.Strings.WizardRemoveTitle, server.Texts());
        Assert.False(server.Wizard.DeleteLocalChats);
        var chats = server.Visible().OfType<CheckBox>().Single();
        chats.IsChecked = true;
        Assert.True(server.Wizard.DeleteLocalChats);
    }

    [AvaloniaFact]
    public async Task An_app_step_is_drawn_by_the_apps_own_template_while_it_runs()
    {
        var step = new FakeStep(WizardPoint.BeforeChoice);
        var running = step.Script.Pending();
        using var host = await WizardHost.OpenAsync(o => o with { Steps = new IWizardStep[] { step } });
        host.View.DataTemplates.Add(new global::Avalonia.Controls.Templates.FuncDataTemplate<IWizardStep>((_, _) => new TextBlock { Text = "The licence" }));
        _ = host.Wizard.StartAsync();
        await host.PumpAsync();

        Assert.Equal(WizardStep.BeforeChoice, host.Wizard.Step);
        Assert.NotNull(host.Wizard.AppStep);
        Assert.Contains("The licence", host.Texts());
        running.Complete(true);
        await host.PumpAsync();
        Assert.Equal(WizardStep.Choose, host.Wizard.Step);
        Assert.DoesNotContain("The licence", host.Texts());
    }

    // ---- The keyboard -----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Enter_in_the_address_box_runs_the_submit_command()
    {
        using var host = await WizardHost.OpenAsync(o => o with { OffersOwnAi = false, OffersDemo = false }, start: true);
        Assert.Equal(WizardStep.ServerAddress, host.Wizard.Step);
        var box = host.Find<TextBox>("ServerBox");
        box.Text = "https://ai.example.test";
        box.Focus();
        await host.PumpAsync();

        host.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await host.PumpAsync();

        Assert.Single(host.Sign.Checked);
        Assert.Equal("https://ai.example.test/", host.Sign.Checked[0].AbsoluteUri);
        Assert.Equal(WizardStep.PairCode, host.Wizard.Step);
    }

    [AvaloniaFact]
    public async Task Esc_on_a_busy_step_runs_cancel_and_on_a_step_with_a_back_goes_back()
    {
        using var host = await WizardHost.OpenAsync(start: true);
        Assert.Equal(WizardStep.Choose, host.Wizard.Step);
        host.Wizard.ChooseServerCommand.Execute(null);
        await host.PumpAsync();
        Assert.Equal(WizardStep.ServerAddress, host.Wizard.Step);

        var checking = host.Sign.Check.Pending();
        host.Find<TextBox>("ServerBox").Text = "https://ai.example.test";
        host.Wizard.SubmitServerCommand.Execute(null);
        await host.PumpAsync();
        Assert.Equal(WizardStep.Checking, host.Wizard.Step);
        Assert.True(host.Wizard.IsBusy);
        Assert.True(host.Wizard.CancelCommand.CanExecute(null));

        host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await host.PumpAsync();
        Assert.Equal(WizardStep.ServerAddress, host.Wizard.Step);
        Assert.True(checking.Token?.IsCancellationRequested);

        Assert.True(host.Wizard.BackCommand.CanExecute(null));
        host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await host.PumpAsync();
        Assert.Equal(WizardStep.Choose, host.Wizard.Step);
    }

    [AvaloniaFact]
    public async Task The_focus_goes_to_the_first_field_of_each_step()
    {
        using var host = await WizardHost.OpenAsync(o => o with { OffersDemo = false });
        await host.ShowAsync(WizardStep.ServerAddress);
        Assert.Same(host.Find<TextBox>("ServerBox"), Focused(host));

        await host.ShowAsync(WizardStep.Password);
        Assert.Same(host.Find<TextBox>("EmailBox"), Focused(host));

        await host.ShowAsync(WizardStep.OwnKey);
        Assert.Same(host.Find<TextBox>("KeyBox"), Focused(host));

        await host.ShowAsync(WizardStep.OwnPick);
        Assert.Same(host.Find<TextBox>("EndpointBox"), Focused(host));

        await host.ShowAsync(WizardStep.OwnAccept);
        Assert.Same(host.Find<ScrollViewer>("NoticeScroller"), Focused(host));

        await host.ShowAsync(WizardStep.Choose);
        Assert.IsType<Button>(Focused(host));
        Assert.Contains(host.Strings.WizardChooseServer, host.Texts());

        await host.ShowAsync(WizardStep.Checking);
        Assert.Equal(host.Strings.WizardCancel, ((Button)Focused(host)!).Content?.ToString() ?? "");
    }

    private static object? Focused(WizardHost host) => host.Window.FocusManager?.GetFocusedElement();

    // ---- A password and a key never stay ------------------------------------------------------------------------

    /// <summary>First start with a server, sign-in with a code shown, then "sign in with e-mail and password".</summary>
    private static async Task<WizardHost> AtPasswordAsync()
    {
        var host = await WizardHost.OpenAsync(o => o with { OffersOwnAi = false, OffersDemo = false }, start: true);
        host.Wizard.ServerAddressText = "https://ai.example.test";
        host.Wizard.SubmitServerCommand.Execute(null);
        await host.PumpAsync();
        Assert.Equal(WizardStep.PairCode, host.Wizard.Step);
        host.Wizard.UsePasswordCommand.Execute(null);
        await host.PumpAsync();
        Assert.Equal(WizardStep.Password, host.Wizard.Step);
        return host;
    }

    [AvaloniaFact]
    public async Task The_password_goes_to_the_command_as_its_parameter_and_the_box_is_empty_after()
    {
        using var host = await AtPasswordAsync();
        var pending = host.Sign.Password.Pending();
        var email = host.Find<TextBox>("EmailBox");
        var password = host.Find<TextBox>("PasswordBox");
        email.Text = "ana@example.test";
        password.Text = "correct horse";
        Assert.Equal("ana@example.test", host.Wizard.Email); // the e-mail is bound; the password is not

        Invoke(host.Find<Button>("SignInButton"));
        await host.PumpAsync();

        var call = Assert.Single(host.Sign.PasswordsTried);
        Assert.Equal("ana@example.test", call.Email);
        Assert.Equal("correct horse", call.Password);
        Assert.True(string.IsNullOrEmpty(password.Text));
        Assert.True(host.Wizard.IsBusy);
        Assert.True(host.Wizard.CancelCommand.CanExecute(null));
        Assert.DoesNotContain("correct horse", Everything(host.Wizard));
        pending.Fail(new FadenstackBadCredentialsException(System.Net.HttpStatusCode.Unauthorized, "no"));
        await host.PumpAsync();
        Assert.Equal(WizardStep.PasswordError, host.Wizard.Step);
        Assert.True(string.IsNullOrEmpty(password.Text));
    }

    [AvaloniaFact]
    public async Task Enter_in_the_password_box_sends_it_and_Enter_in_the_empty_e_mail_box_goes_on_to_it()
    {
        using var host = await AtPasswordAsync();
        host.Sign.Password.Pending();
        var email = host.Find<TextBox>("EmailBox");
        var password = host.Find<TextBox>("PasswordBox");
        email.Text = "ana@example.test";
        email.Focus();
        await host.PumpAsync();

        host.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await host.PumpAsync();
        Assert.Empty(host.Sign.PasswordsTried);
        Assert.Same(password, Focused(host));

        password.Text = "correct horse";
        host.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await host.PumpAsync();
        var call = Assert.Single(host.Sign.PasswordsTried);
        Assert.Equal("correct horse", call.Password);
        Assert.True(string.IsNullOrEmpty(password.Text));
    }

    [AvaloniaFact]
    public async Task The_key_goes_to_the_key_store_through_the_command_and_the_box_is_empty_after()
    {
        using var host = await WizardHost.OpenAsync(o => o with { OffersServer = false, OffersDemo = false }, start: true);
        Assert.Equal(WizardStep.OwnAccept, host.Wizard.Step);
        host.Wizard.Accepted = true;
        host.Wizard.AcceptCommand.Execute(null);
        await host.PumpAsync();
        Assert.Equal(WizardStep.OwnPick, host.Wizard.Step);
        host.Wizard.SelectPresetCommand.Execute(EndpointPreset.OpenAI);
        host.Wizard.UseEndpointCommand.Execute(null);
        await host.PumpAsync();
        Assert.Equal(WizardStep.OwnKey, host.Wizard.Step);

        var key = host.Find<TextBox>("KeyBox");
        key.Text = "sk-test-0123456789";
        Assert.Same(key, Focused(host));
        host.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await host.PumpAsync();

        var saved = Assert.Single(host.Keys.Saved);
        Assert.Equal("sk-test-0123456789", saved.Key);
        Assert.True(string.IsNullOrEmpty(key.Text));
        Assert.DoesNotContain("sk-test-0123456789", Everything(host.Wizard));
    }

    [AvaloniaFact]
    public void No_box_for_a_secret_has_a_binding_on_its_text()
    {
        var xaml = File.ReadAllText(Path.Combine(SkinFolder(), "Views", "WizardView.axaml"));
        var boxes = Regex.Matches(xaml, @"<TextBox\b[^>]*>").Select(m => m.Value).ToList();
        var secret = boxes.Where(b => b.Contains("PasswordChar")).ToList();
        Assert.Equal(2, secret.Count); // the password and the key
        Assert.All(secret, box => Assert.DoesNotMatch(@"\bText\s*=", box));
        Assert.Equal(2, Regex.Matches(xaml, @"b:WizardSecret\.Box=""\{Binding #").Count);
    }

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
        return Path.Combine(root!.FullName, "src", "Fadenstack.UI.Avalonia");
    }

    private static void Invoke(Button button)
    {
        var peer = global::Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(button);
        ((global::Avalonia.Automation.Provider.IInvokeProvider)peer).Invoke();
    }
}
