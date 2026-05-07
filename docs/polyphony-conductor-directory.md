# `.conductor/` Directory Reference

Companion to `polyphony-process-config-schema.md`. That doc covers the schema
of `process-config.yaml`. This doc covers **everything else** in `.conductor/`:
per-type definition and template files, agent-guidance files, and
`profile.yaml`.

Polyphony itself reads only `process-config.yaml`. The rest is consumed by
agents inside the `apex-driver@polyphony` workflow suite (planning,
implementation, review, close-out). Polyphony only checks *existence* of
these files via warnings V-9..V-14
(`src/Polyphony/Configuration/ConfigValidator.cs:97-148`).

---

## Layout

```
.conductor\
├── process-config.yaml                     # required (V-1..V-8 errors)
├── profile.yaml                            # warning V-14 if missing
├── agent-guidance\                         # consumed by sub-workflow agents
│   ├── architect.md                        # warning V-11
│   ├── coder.md                            # warning V-12
│   └── reviewer.md                         # warning V-13
└── work-item-types\                        # one set per type in process-config.yaml
    ├── <slug>.md                           # warning V-9 per type
    └── templates\
        └── <slug>-template.md              # warning V-10 per type
```

`<slug>` is `typeName.ToLowerInvariant().Replace(' ', '-')`
(`src/Polyphony/Configuration/ConfigValidator.cs:162-163`). So `Issue` →
`issue`, `User Story` → `user-story`, `Product Backlog Item` →
`product-backlog-item`.

The reference repo for "what does a complete `.conductor/` look like" is the
polyphony repo itself: `C:\Users\dangreen\projects\polyphony\.conductor\`.
Every file path cited below has a real example there (with the exception of
`agent-guidance/` — see § 4 — which is currently absent and triggers V-11
through V-13).

---

## 1 · `process-config.yaml`

See **`polyphony-process-config-schema.md`** for the full schema, validation
rules V-1..V-8, and worked examples per ADO process template (Basic, Agile,
Scrum, CMMI, custom). Not duplicated here.

---

## 2 · `work-item-types/<slug>.md` — Type Definition Files

### Purpose

The authoritative human- and agent-readable definition of a single work-item
type. Tells planning agents:

- What this type **is** (a one-paragraph definition).
- What question it **answers** (its purpose statement).
- What lives **in scope** vs. **out of scope** for this type.
- **Naming conventions** with good and bad examples.
- **Hierarchy rules** — what can this type contain, and what can it live
  under.
- Where the **description template** lives (link to the templates/ subdir).

The architect and planner agents in the v2 SDLC workflow read these files
when filing a new item of this type or when deciding whether to decompose an
existing one.

### Naming convention

Filename = `<slug>.md` where slug = `ConfigValidator.ToSlug(typeName)`. From
`src/Polyphony/Configuration/ConfigValidator.cs:162-163`:

```csharp
public static string ToSlug(string typeName) =>
    typeName.ToLowerInvariant().Replace(' ', '-');
```

| Type name in `process-config.yaml` | Expected file path                                      |
|------------------------------------|---------------------------------------------------------|
| `Epic`                             | `.conductor/work-item-types/epic.md`                    |
| `Issue`                            | `.conductor/work-item-types/issue.md`                   |
| `Task`                             | `.conductor/work-item-types/task.md`                    |
| `User Story`                       | `.conductor/work-item-types/user-story.md`              |
| `Bug`                              | `.conductor/work-item-types/bug.md`                     |
| `Product Backlog Item`             | `.conductor/work-item-types/product-backlog-item.md`    |
| `Requirement`                      | `.conductor/work-item-types/requirement.md`             |

V-9 is emitted *per type defined in `process-config.yaml`*
(`ConfigValidator.cs:100-110` — the loop iterates `config.Types.Keys`). A
five-type config with no `<slug>.md` files emits five warnings.

### Recommended structure

The polyphony repo's three type-definition files are the reference shape:
`.conductor/work-item-types/{epic,issue,task}.md`. Skeleton (used by all
three):

```markdown
# <TypeName> — Work Item Type Definition (<Process> Process)

## Definition           <one paragraph: what this type is>
## Purpose              <one-sentence question this type answers>
## Audience             <table: PO / Contributor / AI Agent — how each uses it>
## Ownership            <Owner / Assigned To / Reviewer>
## In Scope for a <T>   <bullet list>
## Out of Scope for <T> <bullet list>
## Naming Conventions   <guidelines + good examples + bad examples>
## Description Template <link: templates/<slug>-template.md>
## Hierarchy Rules      <contains / lives under / nesting / sibling rules>
## Relationship to Plan Documents
                        <does this type get a plan document? at which level?>
```

### Anti-patterns

- State names, transitions, facets all live in `process-config.yaml`.
  The `.md` files describe *intent and shape*, not *lifecycle*.
- Concrete work-item IDs belong in plan documents under `docs/projects/`,
  not here.
- One file per type — V-9 looks for `<slug>.md` per type. A single
  `types.md` will emit one V-9 per type.

---

## 3 · `work-item-types/templates/<slug>-template.md` — Description Templates

### Purpose

A pre-canned section structure for a *new* work item of a given type. When
a planning agent files a new Epic, Issue, Task, etc., it copies the template
into the work item's description and fills in the angle-bracketed
placeholders.

These are **descriptions** for the work item, not orchestration scripts.
They get pasted into ADO's `System.Description` (or equivalent on GitHub)
field.

### Naming convention

Filename = `<slug>-template.md` under `.conductor/work-item-types/templates/`.
The path expectation is at `ConfigValidator.cs:113-114`:

```csharp
var templatePath = Path.Combine(repoRoot, ".conductor", "work-item-types", "templates",
    $"{slug}-template.md");
```

| Type           | Expected template path                                                |
|----------------|----------------------------------------------------------------------|
| `Epic`         | `.conductor/work-item-types/templates/epic-template.md`              |
| `User Story`   | `.conductor/work-item-types/templates/user-story-template.md`        |

### Recommended structure

The polyphony repo's three templates are the reference:
`.conductor/work-item-types/templates/{epic,issue,task}-template.md`. A
template is a series of `## Section` headings with angle-bracket placeholders
the planning agent replaces. Example shape (from `issue-template.md`):

```markdown
## Summary
<2–3 sentences: what we're changing, why it matters, expected outcome>

## Problem / Motivation
<What's broken, slow, missing, or poorly structured? Why now?>

## Proposed Approach
<High-level technical approach. File paths, class names, patterns.>

## Acceptance Criteria
- [ ] <Criterion 1 — measurable, binary pass/fail>
- [ ] Build passes with zero errors and warnings
- [ ] All existing tests pass

## Child Tasks (if decomposed)
<Populated during decomposition — omit for directly-implemented Issues>

## Context (optional)
**Plan:** `docs/projects/<slug>.plan.md`
```

### Authoring guidance

- Templates get pasted into work-item descriptions humans skim — aim for
  ≤30 lines.
- Mark optional sections explicitly (`(optional)` or `(if decomposed)`) so
  agents omit them rather than emit empty placeholders.
- Use `- [ ]` checkbox lists for acceptance criteria; renders nicely in ADO
  and on GitHub.
- One template per type — V-10 emits per type.

---

## 4 · `agent-guidance/` — Workflow Agent Tuning

Three files, each consumed by one of the v2 SDLC workflow's named agents:

| File                            | Consumed by                       | V-rule |
|---------------------------------|------------------------------------|--------|
| `agent-guidance/architect.md`   | planning architect agent           | V-11   |
| `agent-guidance/coder.md`       | implementation coding agent        | V-12   |
| `agent-guidance/reviewer.md`    | PG and feature PR reviewer agent   | V-13   |

Path expectations: `ConfigValidator.cs:122-141` — exact filenames, exact
case, no slugging. Unlike the work-item-type files, these are not per-type;
they are *per-role* and there are exactly three.

### Purpose of each file

- **`architect.md`** — repo-specific guidance for the architect agent that
  writes plan documents (`docs/projects/<slug>.plan.md`). Topics: preferred
  layering, naming taxonomy, when to defer to existing patterns, what
  "good" looks like for this codebase. Read by the planning sub-workflow
  before plan generation.
- **`coder.md`** — repo-specific guidance for the implementation agent that
  writes code in PG branches. Topics: language idioms, test patterns,
  formatter conventions, performance constraints, things the agent has
  historically gotten wrong here.
- **`reviewer.md`** — repo-specific guidance for the reviewer agent on PG
  PRs and feature PRs. Topics: what counts as a blocker vs. nit, what
  patterns are tolerated despite being "wrong elsewhere", areas where the
  reviewer should be especially strict.

### Status in this repo

The polyphony repo currently has **no** `agent-guidance/` directory. Running
`polyphony validate-config --config .conductor --output human` from this
repo's root emits V-11, V-12, V-13 today. This is intentional during
bootstrapping; it should be addressed before promoting this repo into
production SDLC use.

### Recommended structure

There is no schema. Each file is free-form Markdown loaded by its
corresponding agent as part of its system prompt. Suggested skeleton:

```markdown
# <Role> Guidance — <Repo Name>

## Repo conventions you must follow      <bullets, ≤10 items>
## Patterns to prefer                    <bullets with citations>
## Anti-patterns specific to this repo   <bullets with citations>
## When in doubt                         <escalation / fallback paths>
```

Keep each file under ~3KB — the same file is loaded on every invocation of
that role.

---

## 5 · `profile.yaml` — Project Profile (V-14)

### Purpose

A YAML manifest describing the project: tech stack, build/test/publish
commands, conventions, estimation defaults, MCP servers in play. **Currently
a reserved placeholder.**

Polyphony's CLI does not load it (no field on `ProcessConfig` maps to it).
It only checks existence at `ConfigValidator.cs:144-148`:

```csharp
if (!File.Exists(Path.Combine(repoRoot, ".conductor", "profile.yaml")))
{
    warnings.Add(Warning("V-14",
        "Profile file missing: .conductor/profile.yaml"));
}
```

**No live consumer exists today** — verified by grep across the conductor
Python CLI, the v2 workflow YAMLs, the polyphony scripts, and the agent
prompts in `polyphony/prompts/`. Even `twig2/.conductor/profile.yaml` claims
"Used by conductor SDLC workflow prompts via Jinja2 template variables" but
no template actually loads it. Authoring a `profile.yaml` today silences V-14
and prepares for future use; it does not change agent behavior. When a
consumer lands, the architect agent will likely use it to contextualise plan
documents and the coder agent to know which build command to run after edits.

### Reference example

The polyphony repo's `.conductor/profile.yaml` is the working reference. Its
shape:

```yaml
project:
  name: <name>
  description: >
    <short paragraph>
  repository: <owner/repo>

tech_stack:
  language: <e.g. C# 14>
  framework: <e.g. .NET 11>
  testing: <e.g. xUnit + Shouldly + NSubstitute>
  # …

build:
  restore: <command>
  build:   <command>
  test:    <command>
  publish: <command>

conventions:
  - <one-line convention>

estimation:
  unit: <hours | days>
  confidence_default: <low | medium | high>

mcp_servers:
  - <mcp server id>
```

### Authoring guidance

- Treat this as the agent's onboarding cheat sheet. If a contributor wrote a
  paragraph for a new agent joining the team, that paragraph belongs here.
- `build:` values get pasted into shells. Use commands valid from repo root.
- `mcp_servers:` lists IDs; conductor resolves ID → transport from its
  registry.

There is no formal schema yet. Unknown keys are ignored. Keep under ~3KB.

---

## 6 · Minimum-viable vs. complete `.conductor/`

### Minimum viable — `validate-config` exits 0 with no warnings

Required for V-9..V-14 to all be silent (N = number of types in
`process-config.yaml`):

```
.conductor\
├── process-config.yaml                              # V-1..V-8 satisfied
├── profile.yaml                                     # V-14
├── agent-guidance\
│   ├── architect.md  /  coder.md  /  reviewer.md    # V-11 / V-12 / V-13
└── work-item-types\
    ├── <slug>.md                  ×N                # V-9 ×N
    └── templates\
        └── <slug>-template.md     ×N                # V-10 ×N
```

### Complete (mature repo) — adds plan-document conventions

A repo running v2 SDLC for a while typically grows the following alongside
the minimum-viable layout. None of these are checked by polyphony:

```
.conductor\                           (as above)
docs\
├── projects\
│   ├── <slug>.plan.md                # one per Issue, generated by architect
│   └── archive\                      # closed plans rolled here on completion
└── adrs\                             # architecture decision records (optional)
```

These paths are convention only; referenced from templates (e.g.
`templates/issue-template.md` → `docs/projects/<slug>.plan.md`) but not
enforced by polyphony.

---

## 7 · `repoRoot` resolution for V-9..V-14

V-9..V-14 only fire when `ConfigValidator.Validate` is called with a
non-null `repoRoot` (`src/Polyphony/Configuration/ConfigValidator.cs:21,
98`). The CLI derives it from the parent of `--config`
(`ValidateConfigCommand.cs:39`):

```csharp
var repoRoot = Path.GetFullPath(Path.Combine(config, ".."));
```

So `polyphony validate-config --config .conductor` from repo root resolves
correctly. Unit tests calling `ConfigValidator.Validate(config)` with no
`repoRoot` get **only** V-1..V-8.

---

## 8 · Summary

| Path                                              | Severity if missing | Consumer                        |
|---------------------------------------------------|---------------------|---------------------------------|
| `process-config.yaml`                             | Error (V-1..V-8)    | All four polyphony verbs        |
| `work-item-types/<slug>.md`                       | Warning (V-9)       | Architect / planner agent       |
| `work-item-types/templates/<slug>-template.md`    | Warning (V-10)      | Filer agent                     |
| `agent-guidance/architect.md`                     | Warning (V-11)      | Architect agent                 |
| `agent-guidance/coder.md`                         | Warning (V-12)      | Coder agent                     |
| `agent-guidance/reviewer.md`                      | Warning (V-13)      | Reviewer agent                  |
| `profile.yaml`                                    | Warning (V-14)      | Multiple agents (project intro) |

Bootstrap order — which file to write first, second, third — see
**`polyphony-bootstrap.skill.md`** § 7b.

