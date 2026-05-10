# Workflows

The polyphony conductor workflow YAMLs and their supporting scripts live in
this repo at:

- `.conductor/registry/workflows/` — workflow YAMLs
- `.conductor/registry/scripts/`   — supporting PowerShell helpers

This top-level `workflows/` directory is intentionally empty (apart from this
README) — it survives only as a signpost; an earlier iteration kept the
workflows in a sibling `polyphony-conductor-workflows` repo, but they have
been co-located back into polyphony so the CLI binary, the workflow suite,
and the dogfood `.conductor/` ship and version together.

## Canonical SDLC entry point

The keystone orchestrator is **`apex-driver@polyphony`** — a tree-walking
dispatcher that builds a worklist for an apex (run-root) work item, drives
each EdgeGraph wave through its lifecycle in parallel with per-item worktree
isolation, integrates wave outputs into the apex feature branch, and loops
until the apex root reports `satisfied`. It supersedes the deleted
`polyphony-full.yaml` single-shot pipeline.

Minimum invocation:

```powershell
conductor run apex-driver@polyphony --input apex_id=<ID> --web
```

Full invocation (all inputs explicit):

```powershell
conductor run apex-driver@polyphony `
  --input apex_id=<ID> `
  --input intent=new `
  --input platform=ado `
  --input organization=<org> `
  --input project=<proj> `
  --input repository=<repo> `
  -m tracker=ado `
  -m project_url=https://dev.azure.com/<org>/<proj> `
  -m git_repo=<absolute repo path> `
  -m workitem_id=<ID> `
  -m worktree_name=<repo>-<ID> `
  -m cwd=<absolute worktree path> `
  --web
```

Inputs:

| Input | Required | Default | Notes |
|---|---|---|---|
| `apex_id` | yes | — | ADO work item id of the apex (run-root) feature. |
| `intent` | no | `new` | One of `new` / `resume` / `replan`. Drives preflight; the dispatch loop is observable-state-driven and identical across all three. |
| `platform` | no | `ado` | Work-item source platform. Threaded through to lifecycle sub-workflows. |
| `organization` | no | `""` | ADO organization. Required by feature-pr / plan-level on the ADO leg. |
| `project` | no | `""` | ADO project name. |
| `repository` | no | `""` | ADO repository identifier (GUID or name). |

ADR: [`docs/decisions/apex-driver.md`](../docs/decisions/apex-driver.md).
Skill: [`.github/skills/polyphony-sdlc/SKILL.md`](../.github/skills/polyphony-sdlc/SKILL.md).

## Advanced / single-leg invocations

The sub-workflows below are the composable building blocks the apex-driver
dispatches into. Most users should not invoke these directly — `apex-driver`
re-derives the right leg per item from observable state. Invoke a sub-workflow
directly only when you want to *replay* or *override* a single leg of a run.

```powershell
conductor run plan-level@polyphony           --input work_item_id=<ID> --web
conductor run implement-merge-group@polyphony --input work_item_id=<ID> --web
conductor run feature-pr@polyphony           --input work_item_id=<ID> --web
```

Each sub-workflow declares its own `--input` shape; consult the YAML in
`.conductor/registry/workflows/` for the exact contract. Standard `-m`
metadata (`tracker`, `project_url`, `git_repo`, `workitem_id`,
`worktree_name`, `cwd`) is the same regardless of which leg is invoked —
see the polyphony-sdlc skill's "Workflow Metadata" section.

## Why the in-repo layout

- Polyphony's own `scripts/` (e.g. `load-agent-guidance.ps1`,
  `scope-closer.ps1`) are referenced by the workflows via relative paths
  resolved at the **consumer repo's** working directory. When polyphony
  dogfoods itself, those paths just work.
- Other consumer repos either expose the same `.conductor/registry/`
  layout or wrap polyphony invocations with an env-var resolver. That is
  a deferred v0.2 concern — not solved by duplicating the scripts.

See `docs/decisions/` for the design ADRs (P5/P8) that govern this layout.
