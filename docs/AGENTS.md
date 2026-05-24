# docs/AGENTS.md — Documentation Rules for AI Agents

This file is the docs-specific deepening of the repo-root [`AGENTS.md`](../AGENTS.md). Read that first.

## Conventions for authoring or modifying docs

- **YAML frontmatter is required on every `.md` file under `docs/`.** Minimum fields: `doc_type`, `status`, `synopsis`. See [`docs/STYLE.md`](STYLE.md) for the full schema and rationale.
- **Do not introduce a `type:` field** — that name is already used by `docs/projects/*.plan.md` to carry the ADO work-item type (`Epic`, `Issue`, `Task`). Use `doc_type:` instead.
- **Citations**: prefer `path:line-start-line-end` over loose prose references. If you cite a line number, verify it after any frontmatter edits (frontmatter shifts every line below it).
- **ADRs in `decisions/`**: keep the prose `> **Status:**` block in the body for revision history nuance, AND add normalized `status:` to the frontmatter for machine queries. They are additive, not replacements.

## Where things live

| Folder | Purpose | Diátaxis fit |
|---|---|---|
| `decisions/` | Architectural decision records (ADRs). Lifecycle: ratified. | n/a (decisions are their own genre) |
| `proposals/` | Forward-looking proposals not yet accepted. Lifecycle: proposed. | n/a |
| `discussions/` | Exploratory threads, often numbered (`01-…`, `02-…`). Lifecycle: exploratory. | n/a |
| `projects/` | In-flight project plans (Epic-level). Lifecycle: in-progress / superseded by completion. | n/a |
| `reference/` | Stable reference material (CLI verbs, schemas, tag namespace). | Diátaxis "reference" |
| `concepts/` | Explanation docs (architecture, models, failure modes). | Diátaxis "explanation" |
| `guides/` | How-to + tutorial (onboarding, operating). | Diátaxis "how-to" + "tutorial" |
| (root) | Hub docs (`README.md`, `glossary.md`, `polyphony-skills-index.md`, `STYLE.md`, `AGENTS.md`, `llms.txt` lives at REPO root). | n/a |

The folder is the *lifecycle / genre*; the frontmatter `doc_type:` mirrors it for machine queries. The optional frontmatter `diataxis:` field declares the purpose axis.

## Do not edit by hand

| Path | Why |
|---|---|
| `docs/discussions/*/critiques/*.md` | Captured agent critiques. Append-only; do not rewrite. |
| `docs/discussions/*/reviews/*.md` | Captured agent reviews. Append-only; do not rewrite. |
| Anything cited as a frozen snapshot from an outside session | Treat as historical. Annotate corrections in a new sibling doc. |

## Adding a new doc

1. Pick the folder by lifecycle (`decisions/` for accepted ADRs, `proposals/` for not-yet-accepted, etc.).
2. Add YAML frontmatter per [`docs/STYLE.md`](STYLE.md).
3. Add a row to that folder's `README.md` table.
4. If it is a high-traffic doc that external LLMs should discover, add a link to [`/llms.txt`](../llms.txt) at the repo root.

## Adding line-number citations

Always verify after frontmatter edits. Prefer section headings (`docs/concepts/polyphony-architecture.md "The three vocabularies"`) for long-lived references and line numbers for ephemeral review comments.
