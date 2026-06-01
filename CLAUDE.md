# CLAUDE.md

See [`AGENTS.md`](AGENTS.md) for the canonical agent guidance for this repo. Claude Code's hierarchical memory will load both files; they are kept in sync deliberately. `AGENTS.md` is the cross-tool standard (Linux Foundation Agentic AI Foundation governance) and the source of truth for this repo's conventions.

Claude-specific notes:

- **Skills auto-load**: `.github/skills/*/SKILL.md` files are activated per the skill's `description:` field. Don't manually load a skill the runtime will load for you.
- **Per-worktree memory**: polyphony runs create per-root worktrees under `<repo>-runs/root-N/feature-N/`. Each worktree has its own `.twig/config` (gitignored) and inherits the repo `AGENTS.md`; the worktree-local config does not propagate back. See `docs/concepts/per-run-worktree-layout.md`.
- **Vocabulary**: This repo enforces a strict canonical vocabulary (AB#3259 hard cut). See `docs/glossary.md`. The vocabulary lint will fail PRs that introduce forbidden synonyms.
