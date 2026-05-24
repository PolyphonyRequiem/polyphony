---
doc_type: index
status: active
synopsis: Frontmatter schema, ADR template, Diátaxis types, citation style — the style contract for the polyphony docs corpus.
---

# Polyphony Docs Style Guide

How to author or modify a doc in this repo. Companion to [`AGENTS.md`](AGENTS.md) (rules for agents) and [`README.md`](README.md) (the docs tour).

---

## YAML frontmatter — required schema

Every `.md` file under `docs/` must start with a YAML frontmatter block:

```yaml
---
doc_type: decision | proposal | discussion | reference | concept | guide | index | plan
status: active | draft | accepted | superseded | in-progress | exploratory
synopsis: One-sentence description of the doc's purpose and scope.
---
```

Optional fields:

```yaml
diataxis: reference | explanation | how-to | tutorial   # purpose axis, used when it adds value
supersedes: [other-doc.md, another-doc.md]              # for ADRs and other versioned docs
superseded-by: replacement-doc.md
scope: polyphony | twig | conductor                      # for cross-repo decisions
audience: agent | operator | contributor | external      # rarely needed; mostly inferred from doc_type
work_item_id: 1234                                       # for plan docs that map to ADO Epics
```

### Why `doc_type:` instead of `type:`

The field `type:` is already taken: `docs/projects/*.plan.md` uses `type: Epic | Issue | Task` to carry the ADO work-item type for downstream agents. To avoid that collision, the documentation lifecycle field is `doc_type:`.

### Why two axes (`doc_type:` + optional `diataxis:`)

`doc_type:` describes **lifecycle / genre** — where in the docs corpus this doc lives. It maps 1:1 to the directory.
`diataxis:` (optional) describes **purpose** — what kind of help this doc is giving, in the [Diátaxis](https://diataxis.fr/) sense (reference / explanation / how-to / tutorial).

For files under `decisions/`, `proposals/`, `discussions/`, and `projects/`, the genre IS the purpose; `diataxis:` is omitted. For files under `reference/`, `concepts/`, and `guides/`, the directory implies Diátaxis but `diataxis:` makes it machine-explicit.

### Status values

| Status | Meaning |
|---|---|
| `active` | Current. Use this. |
| `draft` | In progress; not yet ratified. |
| `accepted` | For ADRs: the decision is in force. |
| `superseded` | Replaced by a newer doc; see `superseded-by:`. |
| `in-progress` | For plan docs: work is underway. |
| `exploratory` | For discussions: capturing thinking, not deciding anything. |

---

## ADR template (MADR 4.0)

New ADRs in `decisions/` use [MADR 4.0](https://adr.github.io/madr/) structure:

```markdown
---
doc_type: decision
status: accepted | draft | superseded
synopsis: One-sentence statement of the decision.
supersedes: [old-decision.md]   # if applicable
superseded-by: new-decision.md  # if applicable
---

# {Concise Title — Active Voice}

> **Status:** {Accepted, Rev N (YYYY-MM)}. {Optional revision history.}

## Context

What is the question / pressure? Why does this decision need to be made now?

## Decision

The decision itself. Active voice. Concrete.

## Consequences

- **Positive:** What gets better.
- **Negative:** What gets harder. Honesty about tradeoffs.
- **Neutral:** What changes that is neither.

## Alternatives considered

Bullet list of options not chosen, with one-line "why not".
```

The prose `> **Status:**` line and the `status:` frontmatter coexist deliberately. Frontmatter is normalized for tooling; prose carries the nuance ("Rev 4.2 (2026-05) — incorporates third hostile-design pass...") that an enum cannot capture.

---

## Diátaxis axis — quick reference

| Diátaxis quadrant | Folder | Example |
|---|---|---|
| Tutorial (learning-oriented) | `guides/` | `onboarding-guide.md` |
| How-to (task-oriented) | `guides/` | (e.g. future "operating-a-stuck-run.md") |
| Reference (information-oriented) | `reference/` | `polyphony-cli-reference.md` |
| Explanation (understanding-oriented) | `concepts/` | `polyphony-architecture.md` |

[Diátaxis](https://diataxis.fr/) is the dominant 2026 IA pattern across Django, NumPy, Cloudflare, Prefect, MCP, Astro, Dagster, and LangChain. We adopt it as the *purpose* axis on top of our existing *lifecycle* genre split.

---

## Citations

- **Code citations:** `path:line-start-line-end` (e.g. `src/Polyphony/Commands/RootCommands.cs:42-58`). Verifiable.
- **Doc citations:** prefer the section heading for long-lived references (`docs/concepts/polyphony-architecture.md "The three vocabularies"`). Use line numbers only for ephemeral review comments.
- **Work item citations:** `AB#NNNN` always paired with a brief title or description in the same sentence — never bare.
- **Frontmatter shifts line numbers.** When you add or change frontmatter, recheck any `:line-range` citations into that file.

---

## Vocabulary

The canonical vocabulary lives in [`glossary.md`](glossary.md). Forbidden synonyms (e.g. `apex`, `wave`, `cascade`, `primary_*`, `*_dispatch` suffix, `terminal_*` prefix) are gated by `.conductor/registry/tests/lint-vocabulary.ps1`. AB#3259 was a hard cut — there are no aliases.

When introducing a new term, add it to `glossary.md` and (if it overlaps with a forbidden synonym) extend the lint.

---

## File naming

| Pattern | Used for | Example |
|---|---|---|
| `kebab-case-with-hyphens.md` | Standard docs | `polyphony-architecture.md` |
| `kebab-case.plan.md` | Plan docs | `polyphony-core-engine.plan.md` |
| `kebab-case.user-plan.md` | User-authored plan docs | `polyphony-self-contained-orchestration.user-plan.md` |
| `NN-topic.md` (numbered) | Discussion threads | `discussions/workflow-compiler-evaluation/01-existing-evidence.md` |

Avoid `README.md` at the root of `docs/` itself? No — we use it: each folder under `docs/` has a `README.md` index, and `docs/README.md` is the landing page.

---

## Adding a new doc — checklist

1. Pick the folder by lifecycle (decisions / proposals / discussions / projects / reference / concepts / guides).
2. Add the YAML frontmatter block.
3. Write the doc with a clear H1 heading.
4. Add a one-line row to the folder's `README.md` table.
5. If high-traffic for external LLMs, add to `/llms.txt` at repo root.
6. If it supersedes an existing doc, set `superseded-by:` on the old one and `supersedes:` on the new one.
