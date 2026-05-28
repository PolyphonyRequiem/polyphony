# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite.
- **Stack:** C# (.NET 11), conductor YAML, PowerShell 7+, Python harness, twig CLI.
- **Created:** 2026-05-28

## Learnings

- 📌 Team formed 2026-05-28. My seat: Twig/ADO Seam.
- 📌 Polyphony references twig two ways: (1) library for reads — Twig.Domain + Twig.Infrastructure as ProjectReferences, wired via `services.AddTwigCoreServices(twigDir: twigDir)` in `src/Polyphony/Infrastructure/PolyphonyServiceRegistration.cs:33`; (2) CLI for writes — helper scripts shell out to `twig set / state / sync / note`. Polyphony itself NEVER writes to ADO.
- 📌 Per-worktree `.twig/config` is inherited from origin/main at worktree creation. Per-worktree edits do NOT propagate. To fix area-path / workspace issues in a worktree, edit the `.twig/config` file in the worktree, `git add`+commit on the feature branch.
- 📌 `twig workspace area remove/add` only updates `areaPathEntries` array (NOT legacy `defaults.areaPaths`). Both fields may need explicit attention.
- 📌 `.twig/config` is tracked in (some) polyphony-onboarded repos like cloudvault — verify before assuming gitignore.
- 📌 Common ADO failure: TF51011 on `declare_root` when worktree's `.twig/config` has stale area paths — fix by committing the area-path correction on the feature branch.
- 📌 ADO process templates the system must support: Basic (Epic→Issue→Task), Agile, Scrum, CMMI, custom. All loaded from `.polyphony-config/process-config.yaml` at runtime — never hardcoded.
- 📌 2026-05-28: Participated in squad-wide initial concerns review (10-agent fan-out). Surfaced 3 top twig/ADO concerns: state-transition durability gap (no post-state `twig sync` in dispatch), per-worktree `.twig/config` area-path staleness (no validation at clone), IAdoClient boundary undocumented. Nominated short-term wins: audit all `twig state` calls + add post-state `twig sync`, harden preflight sync with retry loop.
- 📌 2026-05-28: Implemented issue #529 (PR #533). Key findings: (1) `github-pr.yaml` was missing `already_merged_emitter` in its output guards — re-entry path returned merged=false silently; (2) `close-out.yaml` used `| json` (not a real Jinja2 filter) instead of `| tojson`; (3) plan-level/root-item-dispatch scope bubble-up was already complete; (4) twig sync audit found ALL 3 actual `twig state` callers in workflow YAMLs already have post-state `twig sync` — the earlier concern from the fan-out was about C# code (covered by lint-sync-after-mutation.ps1), not YAML.
- 📌 `already_merged_emitter` pattern: when mirroring ado-pr.yaml's output guard for a GitHub-platform workflow, use `poll_status.output.pr_url` as the `pr_url` fallback (not `already_merged_emitter.output.pr_url`) — matches ado-pr.yaml's uniform approach of using the last poll_status read for the URL across all non-merger exits.
- 📌 The `lint-sync-after-mutation.ps1` test under `tests/` checks C# `SetStateAsync`/`PatchFieldsAsync` → `SyncAsync` pairing, NOT YAML `twig state` → `twig sync`. The YAML workflow `twig state` callers must be audited separately (grep `.conductor/registry/workflows/*.yaml`).
- 📌 2026-05-28: Participated in implementation round 1 — shipped PR #533 on issue #529 (short-term win).
