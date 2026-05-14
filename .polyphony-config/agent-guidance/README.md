# Agent Guidance

This directory is the **per-role** surface for guidance to polyphony's
workflow agents. Each markdown file is loaded into one specific agent role's
prompt at workflow runtime — *not* into every agent's prompt.

For guidance that should reach **every agent** that runs anywhere in this
repo (style rules, naming conventions, cross-cutting invariants), use
conductor's all-agents convention surface — see
[Two surfaces for guidance](#two-surfaces-for-guidance) below.

## Layout

```
.polyphony-config/agent-guidance/
  architect.md              # role-wide, always loaded for the architect agent
  coder.md                  # role-wide, always loaded for the coder agent
  reviewer.md               # role-wide, always loaded for the reviewer agent
  architect/<typeslug>.md   # optional refinement for architect on this work-item type
  coder/<typeslug>.md       # optional refinement for coder
  reviewer/<typeslug>.md    # optional refinement for reviewer
  agents/<agent_name>.md    # optional per-named-agent override
                            # (REPLACES the role-wide block; type refinement still stacks)
```

`<typeslug>` is the lowercase-hyphenated work-item type name (e.g. `epic`,
`user-story`, `task`, `primitive`). The slug is computed by polyphony from
the work item's `System.WorkItemType` field — see
`PlanCommands.SlugifyType` for the exact rule.

## Lookup contract

Loaded by `polyphony plan load-agent-guidance --work-item N`. For each role:

1. Read `<role>.md` if present (role-wide block).
2. Read `<role>/<typeslug>.md` if present (type refinement) — **stacks** on
   top of the role-wide block.
3. If `agents/<agent_name>.md` is present, it **replaces** the role-wide
   block (type refinement still stacks on top).

Missing files are not errors — the workflow continues with whatever is
present. `polyphony validate-config` emits warnings V-11..V-13 when the
three role-wide files are missing, but never refuses to run.

See `src/Polyphony/Commands/PlanCommands.cs` (`LoadAgentGuidance`) for the
authoritative implementation.

## Two surfaces for guidance

There are two distinct mechanisms for getting context into agent prompts.
They are complementary, not parallel — pick by **scope**:

| | Per-role (this directory)                       | All-agents (conductor convention) |
|---|---|---|
| **Source** | `.polyphony-config/agent-guidance/<role>.md`     | `.github/instructions/<name>.instructions.md` (with `applyTo: "**"` frontmatter), or `AGENTS.md` / `CLAUDE.md` / `.github/copilot-instructions.md` |
| **Loaded by** | `polyphony plan load-agent-guidance` at workflow runtime | conductor's workspace preamble (every agent invocation) |
| **Authoring rule** | "the architect / coder / reviewer specifically needs this" | "every agent that runs anywhere in this repo needs this" |
| **Examples** | architect: "don't seed children for typed-only items"; coder: "rebase before push, don't merge"; reviewer: "the three zero-commit-MG cases per AB#3166" | "use the typed `PolyphonyTag` discriminated union"; "spell out `MergeGroup` in C# names, not `Mg`"; "no underscore prefix on members" |
| **Filter** | role + typeslug + agent-name lookup | conductor applies the `applyTo: "**"` predicate; scoped Copilot instructions (`applyTo: "src/**"`) are not loaded |

If a rule reads as "every agent should always do X", it belongs in
`.github/instructions/`. If it reads as "the architect should plan with X
in mind" or "the reviewer should check for X", it belongs here.

The all-agents surface arrived in conductor via
[microsoft/conductor#169](https://github.com/microsoft/conductor/pull/169)
(May 2026), which refactored `CONVENTION_FILES` into a polymorphic
`Convention = ConventionFile | ConventionDirectory` discriminated union
and added auto-discovery for `.github/instructions/**/*.instructions.md`
with `applyTo` filtering.

## When in doubt

Start with the per-role surface (this directory). Promote a rule to the
all-agents surface only after you observe that the *same* rule is needed
for two or more roles, or when the rule is genuinely about the codebase
itself (style, naming, cross-cutting invariants) rather than about how a
particular role does its job.
