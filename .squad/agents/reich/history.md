# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite.
- **Stack:** C# (.NET 11), conductor YAML, PowerShell 7+, Python harness, twig CLI.
- **Created:** 2026-05-28

## Learnings

- 📌 Team formed 2026-05-28. My seat: Git/Worktree Seam.
- 📌 Canonical branch tree: `feature/{root}` integration trunk; recursive `plan/`, `mg/` (merge-group), `impl/`, `evidence/` branches mirroring the work-item tree.
- 📌 Driver-enforced promotion gates with stable planner-declared MG ids. Mandatory merge commits (`--no-ff`) at promote-chain layers — `--ff-only` is WRONG at those steps.
- 📌 Default-nest trigger fires for decomposable+implementable children. Cross-sibling code-dependency rebase rule when impls depend on each other.
- 📌 Run manifest + same-root run lock prevents two concurrent runs against the same root. Renegotiation flow with parent-plan-generation serialization.
- 📌 Per-run worktree layout: `{repo}-runs/root-{id}/feature-{id}/` with recursive nesting. Worktrees are short-lived — spawn for per-item lifecycle, tear down after teardown_worktree node.
- 📌 RunManifest authoritative for: PlanGenerations, MergeGroups, TopologyHash, RetiredMergeGroupIds, HumanApprovals. Log-shaped (reconstructible): Rebases, MergedPlanPrs. TopologyHash recomputed on every Save via RunManifestStore.
- 📌 Reset flow: `Invoke-PolyphonySdlc.ps1 -Intent reset` wipes prior-run branches/PRs/worktrees/manifest/watermark. See `polyphony-dogfood-recovery` skill.
- 📌 Each worktree has its OWN `.twig/config` inherited from origin/main at creation — per-worktree edits don't propagate. Sibelius owns the twig-config implications.
- 📌 2026-05-28: Participated in squad-wide initial concerns review (10-agent fan-out). Surfaced 3 top git/worktree concerns: promotion-gate enforcement gap in Phase 6 (no audit of merge order), zombie worktree accumulation under concurrent abort/reset (teardown retries missing), state-location amendment not fully integrated (no migration code). Nominated short-term wins: document promotion-gate observability, add retry logic to worktree-manager.ps1 teardown.
