# Fadenstack.Skills

Agent Skills on the device for a [Fadenstack](https://fadenstack.com) agent: a folder with a `SKILL.md` and files, as two
read tools, `load_skill` and `read_skill_resource`. It builds on
[Fadenstack.Agents](https://www.nuget.org/packages/Fadenstack.Agents) and on Microsoft Agent Framework's
`AgentSkillsProvider`, which reads the folders and checks the paths.

```bash
dotnet add package Fadenstack.Skills
```

```csharp
using Fadenstack.Skills;

var skills = new SkillsOptions();
skills.Folders.Add(SkillsOptions.UserFolder);                      // <DataRoot>/skills, per OS user
skills.Bundled.Add(new BundledSkill("slide-design", "Designs a slide.", "One idea per slide."));

builder.Use(new SkillsModule(skills));
```

- **Two tools, class read.** The model is told the skills' names and descriptions, loads a skill's instructions with
  `load_skill` and reads one of its listed files with `read_skill_resource`. Neither asks the person. `run_skill_script` is
  never offered: no script runs.
- **Policy.** With a server, local skills are on only when the agent's profile says `local_skills.mode` is `allow` or the
  user's developer mode is on; absent or unknown is off. Standalone they are on unless the person or the machine policy
  turned them off; a machine policy that lists folders keeps only those.
- **The six fields.** A SKILL.md may carry `name` (the folder's name), `description`, `license`, `compatibility`, `metadata`
  and `allowed-tools`. A skill with another key, or one that cannot be read, is skipped and logged; the others stay.
- **`faden.yaml` pre-selection.** A `faden.yaml` beside a SKILL.md (or `BundledSkill.FadenYaml`) is read once per `Refresh`, by core's rules
  (`activation`, `priority`, `tags`, `trigger_rules.keywords` and `file_types`, `preferred_tools`), with the limits of core's `safe_yaml`: 32 KiB, 8 levels,
  no anchors, aliases or tags. Each turn the model is listed the skills its message matches (up to 3, named ones first) and the skills without
  triggers; a skill named with `$slug`, always on, or matched on a keyword gets its instructions loaded for the turn, marked as skill text. The
  preferred tools are a hint in that text. A skill without a `faden.yaml`, or with one that does not read (logged), is listed as before.
- **Skill text is untrusted.** It never changes a tool's class, a mode or a grant.
- **Heavier than the core.** The package takes `Microsoft.Agents.AI` and its dependencies on; the rest of the SDK does not.
