# Skills

A skill is a folder of instructions and reference files for one kind of task, in the open
[Agent Skills](https://agentskills.io) format: a `SKILL.md` with its frontmatter, plus optional files. `Fadenstack.Skills`
gives an agent the person's own skills and the app's bundled ones. The model sees a short list. It loads a skill's
instructions when a task matches (`load_skill`) and reads a listed file when it needs one (`read_skill_resource`).
Scripts never run.

```csharp
using Fadenstack.Skills;

var options = new SkillsOptions();  // the person's own skills and switches: in their folder, users/<user>/
options.Bundled.Add(new BundledSkill("slide-design", "Designs slides ...", body) { FadenYaml = yaml });
var skills = new SkillsModule(options);

var agent = FadenstackAgent.CreateBuilder()
    .UseEndpoint(endpoint).UseLocalProfile(definition)
    .Use(skills)
    .Build();
await agent.StartAsync();  // the profile names the person: their folder is known from here

var editor = new SkillWorkshop(skills, ct => agent.ListSkillsAsync(cancellationToken: ct));  // the editor follows the module
```

## The format

`SKILL.md` keeps only the six standard frontmatter fields, so a skill moves to any agent that reads the format:
- `name`: 1 to 64 characters, lowercase letters, digits and single hyphens, equal to the folder's name;
- `description`: up to 1,024 characters;
- optional: `license`, `compatibility` (up to 500 characters), `metadata` (strings), `allowed-tools`.

A skill with another key is skipped and logged. The body is Markdown; about 5,000 tokens is the advisory size.

Fadenstack's own fields stay in a `faden.yaml` beside it, as on the server:
- `title`, `tags`, `priority` (0 to 100), `activation` (`auto`, `always`, `manual`);
- `trigger_rules`: `keywords`, `file_types`, `intents`;
- `preferred_tools`.

A skill without `faden.yaml` works. `faden.yaml` is read with a restricted reader (32 KiB, depth 8). It refuses anchors,
aliases, tags, block scalars and invisible characters, and a file it refuses is ignored. Note that `yes`, `no`, `on`
and `off` in a keyword list are booleans and make the file unreadable, as on the server: quote them.

## Which skills a turn gets

The rules are the server's (core's skills service):
- skills the person names with `$slug` come first, up to 5;
- then `always` skills, and `auto` skills by score, up to 3 in all;
- a keyword counts as a whole word or phrase (+30), a file type +30, a tag +20, on top of the skill's priority;
- `manual` skills come only by name.

The model sees the resolved list, and `auto` skills without triggers. The first strong match is put into the turn's
instructions as skill text, as the server does. A strong match is a skill the person named, an `always` skill, or a
keyword or file-type hit. The server gives every resolved skill's instructions; here only one is given
(`MaxPreloaded`), because a local model's context is short. `load_skill` still loads any skill.

## Where skills come from, and who decides

- **Bundled** with the app: read-only.
- **The person's own:** in their own folder, `users/<user>/skills/<name>/` in the SDK's data folder
  (`users/standalone/skills/` for the person's own AI; `SkillsOptions.FolderOf`), with their switches beside them
  (`users/<user>/preferences/skills.json`), so two people on one OS account never see each other's, and
  `DeleteMyDataAsync` deletes them. Further folders the person adds in the settings (`SkillsOptions.Folders`). They show
  the label "not reviewed". `SkillsOptions.OwnSkills = false` reads no person's folder; a server that names no user has
  none. See [Data on the device](data-on-device.md).
- **The organisation's,** with a server: listed beside the local ones (`ListSkillsAsync`), read-only.

With a server, local skills are on only when the agent's profile allows them: `local_skills.mode` is `allow`, or
`developer_mode` is true. Absent or unknown means off. Without a server they are on, unless the
[machine policy](machine-policy.md)'s `skills` forbids them or lists the folders allowed. The person can switch each
skill on or off (`ISkillSwitches`).

## The editor

`Fadenstack.UI.Skills` has three view models, drawn by `SkillLibraryView`, `SkillEditorView` and `SkillDraftView` in
the Avalonia and WPF skins:
- **`SkillLibraryViewModel`:** the list with its labels and switches; new, edit, duplicate, delete, import (a folder or
  a zip) and export (a zip).
- **`SkillEditorViewModel`:** the fields with the checks as one types, a Markdown preview, the `faden.yaml` fields and
  the resource files. **Test** shows, for a prompt, which skills the rules list and pre-load, and can ask the model once
  whether it would load one.
- **`SkillDraftViewModel`:** "Draft from this chat". The model proposes a `SKILL.md` from the conversation, within the
  server's limits, and the proposal opens in the editor.

**Nothing is saved until the person presses Save:** a model that could save skills could be talked into it by a
document or a web page.

## Security

- **Skill text is information, never a rule.** It never changes a tool's class, a tool mode or a grant.
  `preferred_tools` is a hint to the model only. `allowed-tools`, `forbidden_tools` and `knowledge_sources` are read
  and ignored.
- **No scripts.** The module never offers `run_skill_script`. The editor and the import refuse a `scripts/` folder
  and script or executable files.
- **Files stay inside the skill:** a resource is read only by the name the skill lists. Links are never followed.
- **Checks before saving:** a single validator serves the editor, the import and the draft. It checks the six fields,
  the name, the lengths, invisible, control and bidi characters, sizes and file counts, and that `faden.yaml` reads.
- **The import:** a zip with traversal, absolute paths, backslashes, links, oversized files (1 MiB a file, 5 MiB in
  all, 100 files, 6 MiB compressed) or a bomb (over 100:1) is refused. Nothing is written before every check passes,
  and an existing skill is replaced only after the person confirms.
- **Saves are atomic:** a staging folder, then a move.

OWASP ASVS 5.0: V5 (file handling), V15 (secure coding).
