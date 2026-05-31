# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite.
- **Stack:** C# (.NET 11), conductor YAML, PowerShell 7+, Python harness, twig CLI.
- **Created:** 2026-05-28

## Recent Status — 2026-05-29

**Current Role:** Workflow Author (Wagner)  
**Active Focus:** PR #547 PR-gate-compression pattern; monitoring conductor RFC Phase 2

### Latest Entries

---

### 2026-05-31T10:51:14-07:00 — Conductor gaps report delivered

Produced `wagner-conductor-gaps-20260531.md` at Daniel's request (offline ~1 hour). Post-PR #547 debrief and workflow-author angle on conductor's top gaps.

**Top 5 Gaps identified:**

1. **`on_error:` routing absent** (Impact 1/5) — every script must exit 0 and fold errors into JSON envelope; 19 trivial human gates exist solely to compensate. Blocked on AB#3257 / RFC Phase 2. Active TODOs at `github-pr.yaml:1087` and `ado-pr.yaml:1196`.
2. **Re-entry has no mid-graph anchor** (Impact 2/5) — `entry_point:` is static; every workflow must be idempotent from scratch. State-detector pattern (`plan-level.yaml:457`) is the manual workaround.
3. **Sub-workflow data can't cross boundaries natively** (Impact 3/5) — arrays must be JSON-encoded strings (`root-batch-dispatch.yaml:83`); output passthrough requires explicit field threading up every layer.
4. **`notification` / `emit` name instability** (Impact 4/5) — six nodes in `github-pr.yaml` + `ado-pr.yaml` carry `TODO(post-upstream-merge)` rename markers; conductor PR #213 not yet cherry-picked.
5. **No `else:` keyword; catch-all routes are implicit** (Impact 5/5) — M4 catch-all is enforced by convention not schema; policy-router nodes (4+ per major workflow) exist to avoid compound `when:` expressions.

**PR-platform abstraction verdict:** Interface is clean at the sub-workflow boundary (symmetric inputs/outputs). Leaks in two places: (a) ADO-specific inputs (`organization`, `project`, `repository`, `root_id`) bleed into `feature-pr.yaml`'s `input_mapping:`; (b) remediation cycle in `feature-pr.yaml` branches on `workflow.input.platform` inline rather than delegating entirely to platform-specific sub-workflows.

**Output file:** `.squad/handoffs/wagner-conductor-gaps-20260531.md`

---

### 2026-05-29T13:41:04-07:00 — PR #547 merged to main (eab39cb)

Gate compression PR merged with Liszt's Poll-PrStateDelta bug fixes.

**Status:** ✅ FIXED — PR #547 Unblocked

Liszt patched 5 pre-existing bugs in Poll-PrStateDelta.ps1 + .Tests.ps1 on your branch (commit b37d85f5770713639f949f7febffeb68a6156f20):

1. Removed -not .Exe -and from Invoke-AssertCli (StrictMode PropertyNotFoundException)
2. Tightened --jq * → --jq {* in Build-GhStub PR-core pattern (wildcard collision with reviews sub-path)
3. Wrapped Find-GitHubDeltas/Find-AdoDeltas in @() (empty list → , single-item → bare hashtable, broke .Count)
4. Removed [System.Collections.Generic.List[hashtable]] type constraint (StrictMode during argument binding)
5. Normalized ConvertFrom-Json DateTime auto-conversions before Should -Match

**Result:** Pester 30/30 PASS (1 intentional skip, 82.41s)

**PR #547 Status:** Unblocked for Daniel's review. Both bugs were pre-existing, not introduced by the PR logic.

**PR Comment:** https://github.com/PolyphonyRequiem/polyphony/pull/547#issuecomment-4579223684

---
