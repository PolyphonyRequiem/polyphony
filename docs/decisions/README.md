---
doc_type: index
status: active
synopsis: Index of polyphony architectural decision records (ADRs) — 19 entries with status, scope, and one-line synopsis.
---

# Decisions (ADRs)

Architectural Decision Records for polyphony. New ADRs follow the [MADR 4.0](https://adr.github.io/madr/) template; see [`../STYLE.md`](../STYLE.md). Both YAML frontmatter `status:` and prose `> **Status:**` coexist — frontmatter is normalized for tooling, prose carries the revision-history nuance.

| ADR | Status | Scope | Synopsis |
|---|---|---|---|
| [`polyphony.md`](polyphony.md) | accepted | polyphony | Root driver dispatches lifecycle work via tree-walking with per-item worktree isolation. |
| [`branch-model.md`](branch-model.md) | draft | polyphony | Feature-trunk + plan/merge-group/task tree branch grammar with `_`-delimited `mg_path` (Rev 4.2). |
| [`per-run-worktree-model.md`](per-run-worktree-model.md) | accepted | polyphony | Per-root worktree contract + same-root run lock for concurrency. |
| [`actionable-executor-split.md`](actionable-executor-split.md) | accepted | polyphony | `actionable.yaml` uses an in-workflow router to split execution between polyphony and human executors. |
| [`scope-renegotiation.md`](scope-renegotiation.md) | accepted | polyphony | HTML-comment fence + four-cell verdict matrix for cross-PG scope renegotiation. |
| [`stuck-review-timeout.md`](stuck-review-timeout.md) | accepted | polyphony | Hard-coded poll cap on pending-review gates promotes silent reviewers to an escalation surface. |
| [`run-reset.md`](run-reset.md) | accepted | polyphony | Per-root run watermark + observer filter + proactive cleanup (PR 1 of 3 of the reset effort). |
| [`run-epoch-and-reset.md`](run-epoch-and-reset.md) | draft | polyphony | Adds run-epoch + first-class reset verb so a root can be re-dispatched cleanly. |
| [`ado-feature-pr-parity.md`](ado-feature-pr-parity.md) | accepted | polyphony | Wires the ADO leg of `feature-pr.yaml` end-to-end so both platforms run the same chain. |
| [`harness-mvp.md`](harness-mvp.md) | accepted | polyphony | Path-coverage harness MVP enabling end-to-end workflow validation without dogfooding ADO. |
| [`jinja-resolver-lint.md`](jinja-resolver-lint.md) | draft | polyphony | Lint that validates `{{ step.output.path }}` Jinja references against verb-output schemas. |
| [`verb-output-schema-registry.md`](verb-output-schema-registry.md) | draft | polyphony | Generates a verb-output schema registry from source verb classes (powers the jinja-resolver lint). |
| [`architect-children-contract-audit.md`](architect-children-contract-audit.md) | accepted | polyphony | Audit (no code changes) of the architect-children contract; supports F2/AB#3065. |
| [`polyphony-verb-migration.md`](polyphony-verb-migration.md) | accepted | polyphony | Locked design contract for the script-to-verb migration driving Epic 2978. |
| [`states-in-process-config.md`](states-in-process-config.md) | accepted | polyphony | `state→category` mapping moves into `process-config.yaml` as a per-template required field. |
| [`versioning-strategy.md`](versioning-strategy.md) | accepted | polyphony | Bundled SemVer — one tag drives both the CLI binary and every workflow `version:` in the registry. |
| [`twig-domain-du-candidates.md`](twig-domain-du-candidates.md) | accepted | twig | Catalog of seven Twig.Domain types evaluated for DU conversion; three recommended, one deferred, three skipped. |
| [`state-category-du-assessment.md`](state-category-du-assessment.md) | accepted | twig | `StateCategory` stays an enum; data-free labels with ordinal semantics are not DU candidates. |
| [`du-twig-decision.md`](du-twig-decision.md) | accepted | twig | Recommend DU adoption for three Twig.Domain types; defer implementation to the Twig repository. |

Sorted by topic cluster (driver/branch/lifecycle → policy → cross-repo Twig.Domain). For chronological order, use `git log --diff-filter=A -- docs/decisions/`.
