# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite. .NET 11 CLI (`src/Polyphony/`) + conductor workflow YAMLs (`.conductor/registry/workflows/`) + PowerShell helpers (`scripts/`) + tests (xunit + Pester + Python harness).
- **Stack:** C# (.NET 11, ConsoleAppFramework, AOT JSON), conductor workflow YAML, PowerShell 7+, Python (harness), twig CLI (companion), git worktrees.
- **Created:** 2026-05-28

## Learnings

- 📌 Team formed 2026-05-28. Universe: Classical Composers (custom). My seat: Architect. Co-members: Beethoven (Mission), Brahms (Testability), Mahler (Conductor), Stravinsky (AI Agents), Mozart (.NET/C#), Liszt (PowerShell), Wagner (Workflow Author), Sibelius (Twig/ADO), Reich (Git/Worktrees) + Scribe + Ralph.
- 📌 Polyphony NEVER writes to ADO directly. All writes go through `twig set / state / sync / note`. This is the core layering invariant. See `docs/polyphony-architecture.md`.
- 📌 Platform abstraction (GitHub vs ADO PRs) lives in workflow YAML, NOT in C# interfaces. There is no `IPlatform` — the split is in `pr_platform_router` inline pwsh nodes. Verified by `git grep -l "IPlatform\|IProcessAdapter" src/Polyphony` returning nothing.
- 📌 Glossary at `docs/glossary.md` is authoritative. Forbidden terms (AB#3259): `apex`→`root`, `apex-driver` (top-level is `polyphony.yaml`), `tree-walker`, `primary_*`→`root_*`, `Primary*`→`Root*`, `wave`→`batch`, `cascade`→`restack`, `*_dispatch` suffix dropped, `terminal_*` prefix dropped. Lint-enforced via `lint-vocabulary.ps1`.
- 📌 2026-05-28: Participated in squad-wide initial concerns review (10-agent fan-out). Surfaced 3 top architectural concerns: verb output schema registry deferred, process-config state-name specificity, twig CLI response parsing fragility. Nominated short-term wins: backfill `[VerbResult]` attributes (Mozart), document twig response contract (Sibelius).
