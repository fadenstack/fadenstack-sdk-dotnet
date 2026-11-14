# Contributing to the Fadenstack .NET SDK

Thank you for helping. This page says how to ask, report and propose changes, and what a pull request needs.

Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## Questions

Read the [documentation](https://developers.fadenstack.com/sdk/dotnet/) first. If it does not answer your
question, open an issue with the **Question** template. Questions about the server itself belong in
[fadenstack/fadenstack](https://github.com/fadenstack/fadenstack/issues).

## Reporting a bug

1. Search the [issues](https://github.com/fadenstack/fadenstack-sdk-dotnet/issues) to see whether it is known. If
   it is, add what you know there instead of opening a new one.
2. Open an issue with the **Bug report** template. Say which package version you use, on which .NET (.NET
   Framework 4.8, .NET 8, ...) and in which host application, which server release you talk to (`faden version`
   on the server), what you did, what you expected and what happened instead. A small program that shows the
   problem helps most.
3. Leave out passwords, tokens, API keys and personal data. Shorten logs to the part that matters.

A **security problem** is not reported in an issue: see [SECURITY.md](SECURITY.md).

## Proposing a change or a feature

Open an issue with the **Feature request** template before you write code, and describe the problem you want to
solve rather than only the solution. We say there whether it fits and how we would like it done, so your work is
not wasted. Small fixes (a typo, a broken link) can go straight to a pull request.

## Pull requests

- **Link the issue** it resolves ("Fixes #123"). A bug fix needs an issue that describes the bug; a feature needs
  an issue we agreed on.
- **One change per pull request**, as small as it can be.
- **Tests:** add or change tests that show the change works, and run `dotnet test Fadenstack.slnx`.
- **Documentation:** when you change what a developer sees or does, change the page in `docs/`.
- **Compatibility:** the libraries target `netstandard2.0` for .NET Framework hosts, so use only APIs that exist
  there. A change to a public API that breaks existing code needs to be agreed in the issue first.
- **Commit messages** say what the change does, in a plain sentence ("Agents: a declined tool call ends the
  turn"), with the reason in the body when it is not obvious.
- **No confidential information**: nothing about your company, clients or systems that you don't want public.

### Contributor licence agreement

Before we can merge your first pull request, you sign the
[contributor licence agreement](CONTRIBUTOR_LICENSE_AGREEMENT.md) once. When you open the pull request, the CLA
Assistant bot comments on it with a link; you read the agreement there and sign with your GitHub account. It is
three sentences long: you let Emagin8 UG (haftungsbeschränkt), Fadenstack's steward, license your contribution on
any terms, and you give it without warranty. If your employer or a client has rights in your work, make sure you
may sign.

## Architecture rules

The SDK is built in layers. The tests in `tests/Fadenstack.Architecture.Tests` fail when a change breaks one of these
rules, so they show up in `dotnet test`:

| Layer | Libraries | May reference |
|---|---|---|
| 1 · Connection | `Fadenstack.Client`, `Fadenstack.Secrets` | Client: no other library of the SDK. Secrets: Client |
| 2 · Agent runtime | `Fadenstack.Agents` | Client; of the Microsoft Agent Framework only its Abstractions |
| 3 · Modules | `Fadenstack.Mcp`, `Fadenstack.Provenance`, `Fadenstack.Workspaces` | Agents and Client, never another module or the UI |
| 4 · UI logic | `Fadenstack.UI` | Agents and Client, and no UI framework |
| 5 · Skins | `Fadenstack.UI.Wpf`, `Fadenstack.UI.Avalonia` | the UI layer and below, and their own framework |

- **References point down only.** A module brings its own heavy dependency, so the core stays light: it runs inside
  Outlook. A new library needs its row in `LayerTests`.
- **A module owns its texts**, in its own resources and in the SDK's five languages (English, German, French, Spanish,
  Simplified Chinese), never in the UI layer.
- **Strict MVVM.** View models live in `Fadenstack.UI` and show no UI type in their API (`Brush`, `Visibility`, ...).
  A skin's code-behind holds view-only behaviour (focus, scrolling, animation); its members are listed in
  `codebehind-allowlist.json`, and a change is reviewed there (`FADENSTACK_ARCH_ALLOWLIST=write dotnet test`
  rewrites the list). No text is written in a skin's XAML: every text comes from `UiStrings`.
- **Invisible characters** (zero-width characters, a BOM, variation selectors) are written as escapes (`\uFEFF`),
  never as themselves.

The rules come from the maintainers' SDK architecture note (sdk/52).

## Setting up for development

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Everything builds and runs on Windows. On Linux
and macOS the WPF projects build (`EnableWindowsTargeting`), but the WPF sample does not run.

```bash
git clone https://github.com/fadenstack/fadenstack-sdk-dotnet.git
cd fadenstack-sdk-dotnet
dotnet test Fadenstack.slnx
```

The tests run against a fake server and need no Fadenstack installation. `Fadenstack.Agents.Delegation` and the tests that
use it need the A2A library from the Fadenstack fork, which is not on nuget.org: with access to the fadenstack GitHub
Packages feed, add it with A2A mapped to it ([Delegating to agents](docs/delegation.md#getting-the-package)). Without,
clone `https://github.com/fadenstack/a2a-dotnet` (branch `feat/netstandard2.0`) next to this repository and pack it into
the folder `.local/sdk-feed` beside both clones, which the project picks up:
`dotnet pack src/A2A/A2A.csproj -c Release -p:Version=1.0.0-preview4-fs.1 -o ../.local/sdk-feed`. To preview the documentation:
`pip install zensical`, then `zensical serve`.

## Licence

The SDK is licensed under the [Apache License 2.0](LICENSE). Your contribution is licensed under it too, and the
contributor licence agreement above adds the steward's right to license it on other terms.
