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
- **Tests:** add or change tests that show the change works, and run `dotnet test Faden.slnx`.
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

## Setting up for development

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Everything builds and runs on Windows. On Linux
and macOS the WPF projects build (`EnableWindowsTargeting`), but the WPF sample does not run.

```bash
git clone https://github.com/fadenstack/fadenstack-sdk-dotnet.git
cd fadenstack-sdk-dotnet
dotnet test Faden.slnx
```

The tests run against a fake server and need no Fadenstack installation. To preview the documentation:
`pip install zensical`, then `zensical serve`.

## Licence

The SDK is licensed under the [Apache License 2.0](LICENSE). Your contribution is licensed under it too, and the
contributor licence agreement above adds the steward's right to license it on other terms.
