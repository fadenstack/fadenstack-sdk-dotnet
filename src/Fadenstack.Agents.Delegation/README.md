# Fadenstack.Agents.Delegation

Local delegation between Fadenstack agents on one device: an add-in's agent takes tasks from another agent
(`DelegationHost`), a caller hands them over (`LocalAgentDelegate`, an `IAgentDelegate`), and `DelegateToolModule`
gives the model one `delegate(agent, task, references)` tool. The pipe is open to the current OS user only.

This package is for applications (a desktop agent, an Office add-in). It is not on nuget.org: it is on the fadenstack
organisation's GitHub Packages feed, with the A2A library it uses (the Fadenstack fork with a `netstandard2.0` build,
until upstream ships one: a2aproject/a2a-dotnet issue #197). The interface it implements, `IAgentDelegate`, is in
`Fadenstack.Agents` on nuget.org. While A2A is a preview, a stable SDK version has this package as a preview of it:
SDK 0.3.0 has `Fadenstack.Agents.Delegation` 0.3.0-preview.

- Documentation: <https://developers.fadenstack.com/sdk/dotnet/>
- Licence: Apache-2.0 (LICENSE and NOTICE are in the package)
