# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite.
- **Stack:** C# (.NET 11), conductor YAML, PowerShell 7+, Python harness, twig CLI.
- **Created:** 2026-05-28

## Learnings

- 📌 Team formed 2026-05-28. My seat: PowerShell Expert.
- 📌 CARDINAL TRAP: `[System.IO.File]::ReadAllText` with a relative path uses `[Environment]::CurrentDirectory` (NOT `$PWD`). After `cd worktree-dir`, sync them with `[Environment]::CurrentDirectory = $PWD.Path` or use absolute paths. Otherwise scripts read from the .NET process's original cwd and silently overwrite target files with stale content.
- 📌 `Invoke-PolyphonySdlc.ps1` runs `twig show <RootId>` in cwd before init-root. The cwd's twig workspace/cache MUST contain the root item.
- 📌 PowerShell `&&` only chains native/external commands. Do NOT use `&&` before pwsh keywords (`if`, `foreach`, `$var =`). Use `;`.
- 📌 NEVER use `git -C {path}` in scripts (unreliable on Windows). `cd` into the dir first, then run git.
- 📌 NEVER embed newlines in `git commit -m "..."` (backtick-n fails silently in pwsh). Write the message to a temp file and use `git commit -F $msgFile`.
- 📌 `.conductor/registry/tests/*.Tests.ps1` is NOT auto-discovered by ci.yml — new Pester test files need explicit CI workflow wiring.
- 📌 Each polyphony worktree has its OWN `.twig/` workspace config inherited from origin/main at creation. Per-worktree edits don't propagate to other worktrees; commit area-path fixes in the worktree to satisfy assert-clean.
- 📌 2026-05-28: Participated in squad-wide initial concerns review (10-agent fan-out). Surfaced 3 top PowerShell concerns: `git -C` in production launcher (line 833), heredoc injection surface in child-command building, `git -C` in test scaffolding. Nominated short-term wins: fix line 833 (Push-Location/Pop-Location), sanitize `$Comment` parameter.
- 📌 2026-05-28: Implemented issue #530 (PR #531). Three fixes: (1) replaced `git -C $mainWorktree` at line 833 with Push-Location/Pop-Location block; (2) added `Get-SanitizedComment` helper — strips `\r\n` and escapes backticks — applied at reset-path boundary before any shell interpolation; (3) backported GitWorktreeDeleter.cs retry-with-backoff (delays: 0/200/500/1000 ms) to worktree-manager.ps1 teardown path. AST parse: 0 errors both files. Pester: lint-polyphony 29/29 passed.
- 📌 SANITIZATION PATTERN: `Get-SanitizedComment` is the canonical boundary for `$Comment` before shell embedding. Apply once at entry to the reset path — never inline-sanitize at individual use sites.
- 📌 WORKTREE RETRY SHAPE: `$delays = @(0, 200, 500, 1000)` foreach with `Start-Sleep -Milliseconds` + `Test-Path` short-circuit matches `GitWorktreeDeleter.cs` intent. Use this shape for any PS worktree-remove retry.
- 📌 2026-05-28: Participated in implementation round 1 — shipped PR #531 on issue #530 (short-term win).
