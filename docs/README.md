---
doc_type: index
status: active
synopsis: Landing page for the polyphony docs corpus. Map by purpose and by folder; orientation for fresh agents and humans.
---

# Polyphony Documentation

Landing page for the polyphony docs corpus. This is the doc to read first if you are landing in this repo with no context.

For canonical agent guidance, see [`AGENTS.md`](../AGENTS.md) at the repo root. For the task-to-doc-routing index (skills + docs), see [`polyphony-skills-index.md`](polyphony-skills-index.md).

---

## Three-line orientation

Polyphony is a .NET 11 CLI that decides *what should happen* to a work item in an SDLC pipeline — phase, transition, branch hint — based on a per-repo `.polyphony-config/process-config.yaml`. It pairs with **`twig`** (which executes decisions against Azure DevOps) and with **`conductor`** (which orchestrates the surrounding AI workflows). Polyphony itself never writes to ADO.

---

## Map by purpose

### "I just want to understand polyphony"
1. [`concepts/polyphony-architecture.md`](concepts/polyphony-architecture.md) — layering, three-vocabulary contract, platform abstraction, data flow.
2. [`reference/polyphony-cli-reference.md`](reference/polyphony-cli-reference.md) — every verb, JSON shape, exit code.
3. [`concepts/polyphony-agent-failure-modes.md`](concepts/polyphony-agent-failure-modes.md) — six concrete failures the docs prevent; calibration.

### "I'm onboarding a fresh repo"
1. [`guides/onboarding-guide.md`](guides/onboarding-guide.md) — step-by-step with a worked example.
2. [`reference/polyphony-conductor-directory.md`](reference/polyphony-conductor-directory.md) — every file in `.polyphony-config/`.
3. [`reference/polyphony-process-config-schema.md`](reference/polyphony-process-config-schema.md) — full schema with rules V-1..V-14.

### "I'm authoring or modifying a workflow YAML / PowerShell helper"
1. Skill: `.github/skills/polyphony-workflow-author/SKILL.md`.
2. [`reference/polyphony-cli-reference.md`](reference/polyphony-cli-reference.md) — JSON shapes the YAML reads.
3. [`concepts/polyphony-architecture.md`](concepts/polyphony-architecture.md) "The three vocabularies".

### "I'm adding or modifying a polyphony CLI verb"
1. Skill: `.github/skills/polyphony-cli-developer/SKILL.md`.
2. [`reference/polyphony-cli-reference.md`](reference/polyphony-cli-reference.md) — confirm the verb doesn't already exist.
3. [`concepts/polyphony-architecture.md`](concepts/polyphony-architecture.md) — confirm it belongs in the polyphony layer.

### "I'm authoring a doc"
1. [`STYLE.md`](STYLE.md) — frontmatter schema, ADR template (MADR 4.0), Diátaxis types.
2. [`AGENTS.md`](AGENTS.md) — docs-folder rules for agents.
3. The relevant folder's `README.md`.

---

## Map by folder

| Folder | What's in it | Lifecycle |
|---|---|---|
| [`reference/`](reference/README.md) | CLI reference, tag namespace, state effects, conductor directory, process-config schema | Stable |
| [`concepts/`](concepts/README.md) | Architecture, worktree model, agent failure modes | Stable |
| [`guides/`](guides/README.md) | Onboarding | Stable |
| [`decisions/`](decisions/README.md) | 19 architectural decision records | Ratified |
| [`proposals/`](proposals/README.md) | 3 forward-looking proposals | Proposed / draft |
| [`projects/`](projects/README.md) | 10 in-flight or completed Epic-level project plans | In-progress / superseded |
| [`discussions/`](discussions/README.md) | Numbered exploratory threads | Exploratory |

## Hub docs at this level

| Doc | Purpose |
|---|---|
| [`glossary.md`](glossary.md) | Canonical vocabulary. AB#3259 hard cut — forbidden synonyms are gated by a CI lint. |
| [`polyphony-skills-index.md`](polyphony-skills-index.md) | Task-to-doc / task-to-skill routing index. |
| [`AGENTS.md`](AGENTS.md) | Docs-folder rules for AI agents. |
| [`STYLE.md`](STYLE.md) | Frontmatter schema, ADR template, Diátaxis types, citation style. |

`llms.txt` (repo root): a curated index for external LLMs. Updated alongside this file.

---

## Conventions in 30 seconds

- **Every doc has YAML frontmatter** (`doc_type`, `status`, `synopsis`). See [`STYLE.md`](STYLE.md).
- **ADRs use MADR 4.0** structure, with both prose `> Status:` (for revision history nuance) AND machine-readable `status:` frontmatter.
- **Citations use `path:line-start-line-end`** for verifiability. Use section names for long-lived refs.
- **Vocabulary is canonical**. See [`glossary.md`](glossary.md). Forbidden synonyms (`apex`, `wave`, `cascade`, `primary_*`, …) will fail the vocabulary lint.
- **Type-agnosticism (P5)** is load-bearing: no work-item type name (Epic / Issue / Task / Bug / custom) appears in any routing condition.

---

## Not covered here

- **The `twig` CLI** (set / state / sync / note / show / new / patch / …). See the **twig-cli** skill or the [twig repo](https://github.com/PolyphonyRequiem/twig).
- **The `conductor` workflow engine** (YAML schema, routing semantics, re-entry, human gates). See the **conductor** skill.
- **Per-repo agent guidance** (`.polyphony-config/agent-guidance/architect.md` etc.) — those are tuning files for downstream agents, not polyphony documentation.
