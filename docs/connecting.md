# Connecting an agent

The first time an app starts, and later in its settings, the person chooses how the agent works: with the
organisation's Fadenstack server ("Connect to my organisation") or with an AI server of their own ("Use my own AI").
`ConnectionWizardViewModel` (in `Fadenstack.UI.Wizard`) walks them through either way. It is one state machine that
the skins draw: `WizardView` in `Fadenstack.UI.Avalonia` and in `Fadenstack.UI.Wpf`. At the end the app gets a
`ConnectionResult` and builds the agent from it, with `UseServer` or `UseEndpoint`.

```csharp
using Fadenstack.UI.Wizard;

var options = new WizardOptions
{
    AppName = "Word assistant",
    Agents = ["word-assistant"],               // the app's agents, the main one first: one approval signs in all
    PasswordFallback = true,                   // offer e-mail and password for a server without pairing
    TermsVersion = "2026-11",                  // kept in the person's acceptance record
    Policy = MachinePolicy.Load(policyPath),   // MachinePolicy.None when there is none
    Steps = [new LicenceStep()],               // the app's own steps, if any
};
var wizard = new ConnectionWizardViewModel(options, connectionStore,
    server: new ServerSignIn(tokenStore, options.Agents, deviceLabel, PairingClient.ForThisDevice("Word assistant", version), options.Policy),
    keys: endpointKeyStore,
    dispatcher: new AvaloniaUiDispatcher(), clipboard: clipboard, links: linkOpener);
wizard.Completed += (_, outcome) => { /* WizardOutcome.Connected: build the agent from wizard.Result */ };
view.Content = new WizardView { DataContext = wizard, AppName = options.AppName, FullTermsLink = termsLink };
await wizard.StartAsync();
```

## The two ways

**Connect to my organisation.** The person types the server's address, unless the machine policy sets it and locks
it. The wizard checks the address with `ServerCheck` (`GET /api/health`). It then shows a pairing code, and the person
approves it on the console's `/pair` page. One approval signs in all of the app's agents. The tokens go to the token
store. A server without pairing gets the e-mail and password sign-in when `PasswordFallback` is on. Every failure has
its own step, and its own words in five languages:
- the server cannot be reached;
- its certificate is not trusted (a CA comes from the policy or the app's settings, never from a click);
- it does not answer like Fadenstack;
- the server is too old;
- too many tries;
- the sign-in page is on another address;
- the request was denied;
- the code expired;
- a required agent was refused.

**Use my own AI.** The person reads where their data goes, the provider's terms and the standalone notice, and ticks
the consent box, which starts unticked. The wizard keeps an acceptance record: terms version, time, language and mode.
With the person's consent it searches this computer for Ollama, LM Studio, vLLM and llama.cpp. Otherwise the person
picks a preset or types an address. A key goes straight into the key store. Plain http goes only:
- to this computer, where a key may go too (the check says it travels unencrypted);
- to the local network after the person confirms it, and never with a key.

The app's trusted CA (`WizardOptions.TrustedCaCertificate`, from the policy or the app's settings) reaches the endpoints too, so an
organisation's own model server behind its own CA connects; the wizard never offers to trust a certificate.

The endpoint check (`EndpointCheck`) then says in plain words what works, what limits the connection (chat only, the
context length, ...) and whether it watermarks text. Last, the person picks the model.

## What the app gives

| Contract | What it is |
|---|---|
| `IConnectionStore` | Where the app keeps the connection (settings file, Office settings): the server, the result, the acceptance record, and sign-out. Never a key or a token. |
| `IEndpointKeyStore` | The key store for endpoint keys (DPAPI, Keychain, libsecret). `SaveAsync` returns a token provider that reads the key when a request needs it. |
| `IWizardStep` | The app's own steps, at four points: before the choice (the desktop's licence), before the server check, before every endpoint check (a permission), after connecting. A step that says no stops the flow. |
| `WizardOptions` | What the app offers (server, own AI, demo), its agents and the required ones, the password fallback, whether it restarts onto a new server (the desktop), whether it can search this computer, the terms version, the machine policy. |
| `ServerSignIn`, `EndpointChecker` | The default server and endpoint sides over the SDK. Tests pass fakes of `IServerSignIn` and `IEndpointChecker`. |

`WizardEntry` says how the wizard opens:
- `FirstRun`;
- `ServerKnown`, after the desktop restarted onto a new server, or when a policy names the server;
- `ReSignIn`, with the "your sign-in ended" banner;
- `Settings`: change the server or the endpoint, connect to the organisation, sign out, remove the endpoint.

## The machine policy

The [machine policy](machine-policy.md) shapes the choices:
- `server` opens the server path with the address locked;
- `standalone: forbidden` hides "Use my own AI";
- `endpoints` narrows the presets, the servers found and a typed address;
- `plainHttp: forbidden` refuses plain http to the local network, and a key over plain http even to this computer.

## Secrets

No secret stays in the view model (OWASP ASVS 5.0 V13):
- the password and the key are command parameters, read from the skin's password box when the button runs, handed on
  at once, and cleared from the box;
- only `KeySet` remains;
- the pairing code stays inside the SDK.

The view model returns to the UI thread through `IUiDispatcher` after every await. A newer action makes every older
one's answer stale. A store that fails says so (`StoreFailed`) and never leaves the wizard on a busy step.

## The texts

Every text is a `UiStrings` member whose name starts with `Wizard`, in English, German, French, Spanish and Simplified
Chinese. The view model adds the texts that follow its state:
- `AddressErrorText`, `StartFailureText`, `PasswordFailureText` and `ProblemText`;
- `WaitingText` ("The code expires in 9:41");
- `SignedInText`;
- `ResultLines`;
- `ConnectedText`.

When the app's language changes, `wizard.Retext(culture)` (or `Retext(UiStrings)`) reads every one of these texts again in
the new language and raises the change notifications, so a skin follows at once. The step, the typed values and the check
result stay as they are.

The standalone notice and the liability sentence are legal texts: their German version is the binding one.
