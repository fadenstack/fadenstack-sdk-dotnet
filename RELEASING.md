# Releasing

A tag publishes a release. `.github/workflows/release.yml` checks that the tag names the version, runs the tests
on Windows and packs the `Fadenstack.*` packages with their symbol packages. It then makes a GitHub release for the
tag with the packages, and core's shared release workflow adds an SBOM per package (CycloneDX, BSI TR-03183-2), the
OSV scan with OpenVEX and CSAF, the OpenSSF Scorecard, keyless cosign signatures and GitHub's attestations to it. Only
then does it push the packages to nuget.org with trusted publishing (no API key is stored anywhere), and
`Fadenstack.Agents.Delegation` to the fadenstack organisation's GitHub Packages feed. A high-severity finding without
a statement in `.vex/openvex.json` stops the release before anything is published.

Started by hand (**Run workflow**), the workflow is a dry run: the build and the supply chain's SBOMs, scan and
Scorecard, shown in the run's summary; nothing is signed, uploaded or published.

1. Set `<Version>` in `Directory.Build.props` and commit.
2. Tag and push:

    ```bash
    git tag v0.2.0
    git push origin main v0.2.0
    ```

3. Watch the **Release** workflow. A version with a suffix (`v0.3.0-rc.1`) is a prerelease on nuget.org.

A version can never be replaced on nuget.org; it can only be unlisted. Rehearse with an rc version. If the
workflow stops half-way, run it again: it skips the packages that are on nuget.org already.

## Before the first release (once)

1. On nuget.org, **Trusted Publishing** has a policy for repository owner `fadenstack`, repository
   `fadenstack-sdk-dotnet`, workflow file `release.yml` and environment `nuget`. While the repository is private,
   a new policy is active for 7 days only; restart it there if it ran out. The first publish binds it to this
   repository for good.
2. The repository secret `NUGET_USER` is the nuget.org profile name the policy belongs to (not an e-mail
   address).
3. In this repository's **Settings**, **Environments**, create `nuget` and allow only tags `v*` to deploy to it.

A new package ID needs no step of its own on nuget.org, as long as the policy may push new packages (its scope); the
release job created `Fadenstack.Provenance` that way with 0.3.0-rc.1. The `Fadenstack` prefix is not reserved, so an
unused ID can be taken by anyone until our first version of it is out.
