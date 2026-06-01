---
doc_type: index
status: active
synopsis: Index of in-flight and completed project plans — Epic-level implementation plans, often with ADO work-item linkage.
---

# Projects

Epic-level implementation plans. Most carry an ADO work-item id and a status (in-progress, done, superseded). Plan docs that already carry `type: Epic | Issue | Task` frontmatter keep that field — it is the work-item-type contract for downstream agents and is independent of the docs `doc_type:` taxonomy.

| Plan | Status | Work item | Synopsis |
|---|---|---|---|
| [`polyphony-core-engine.plan.md`](polyphony-core-engine.plan.md) | done | #2581 | Phase 1 — deterministic routing engine implementing route/validate/hierarchy CLI commands. Shipped. |
| [`validation-testing.plan.md`](validation-testing.plan.md) | done | #2584 | Phase 4 — validation and testing of the polyphony routing engine. Shipped. |
| [`du-preview-adoption.plan.md`](du-preview-adoption.plan.md) | in-progress | #2585 | Phase 5 — adopt the .NET 9 DU preview language feature across polyphony domain types. |
| [`polyphony-health-command.plan.md`](polyphony-health-command.plan.md) | active | — | Plan for the `polyphony health` CLI command — environment and configuration diagnostics. |
| [`on-error-migration-inventory.md`](on-error-migration-inventory.md) | active | AB#3257 | Read-only inventory of `on_error:` usage informing failure-mode gate elimination sequencing. |
| [`type-agnostic-sdlc.plan.md`](type-agnostic-sdlc.plan.md) | in-progress | (see frontmatter) | Type-agnostic SDLC plan. |
| [`workflow-yaml-refactoring.plan.md`](workflow-yaml-refactoring.plan.md) | in-progress | #2583 | Phase 3 — Workflow YAML refactoring into the `polyphony@polyphony` suite. |
| [`polyphony-self-contained-orchestration.user-plan.md`](polyphony-self-contained-orchestration.user-plan.md) | in-progress | #2978 | User-authored plan: polyphony self-contained orchestration (drives the script-to-verb migration). |
| [`workflow-vocabulary-cleanup.user-plan.md`](workflow-vocabulary-cleanup.user-plan.md) | done | AB#3259 | User-authored plan: workflow vocabulary cleanup. Shipped via PR #514. |
| [`open-questions-policy.user-plan.md`](open-questions-policy.user-plan.md) | in-progress | (see frontmatter) | User-authored plan: open-questions policy. |
