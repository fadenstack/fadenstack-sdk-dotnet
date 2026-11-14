# Fadenstack.Agents.Delegation

Local delegation between Fadenstack agents on one device: an add-in's agent takes tasks from another agent
(`DelegationHost`), a caller hands them over (`LocalAgentDelegate`, an `IAgentDelegate`), and `DelegateToolModule`
gives the model one `delegate(agent, task, references)` tool. The pipe is open to the current OS user only.

This package is for applications (a desktop agent, an Office add-in). It is not published to nuget.org with the SDK:
the interface it implements, `IAgentDelegate`, is in `Fadenstack.Agents`.

- Documentation: <https://developers.fadenstack.com/sdk/dotnet/>
- Licence: Apache-2.0 (LICENSE and NOTICE are in the package)
