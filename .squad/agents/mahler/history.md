# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite.
- **Stack:** C# (.NET 11), conductor YAML, PowerShell 7+, Python harness, twig CLI.
- **Created:** 2026-05-28

## Learnings

- 📌 Team formed 2026-05-28. My seat: Conductor Expert.
- 📌 Companion skills: `conductor-design` (principles — workflow state/routing/re-entry/human interaction) and `conductor-mechanics` (YAML plumbing — output access, route mechanics, models, cross-platform invocation). Load both when authoring workflows.
- 📌 Polyphony driver split: `polyphony.yaml` (outer dispatch loop) → `root-batch-dispatch.yaml` (per-batch fan-out via for_each + batch integrator) → `root-item-dispatch.yaml` (per-item: classify → spawn worktree → lifecycle → teardown). The three-file split is FORCED by conductor's `for_each` constraint (exactly one thing per iteration). See `docs/decisions/polyphony.md` Q1.
- 📌 Lifecycle classification (Q2) is a SCRIPT, not YAML routing — the rule set ("if next-ready is plan_authored → plan-level; if action_satisfied → actionable; ...") consults `polyphony state next-ready` JSON output.
- 📌 Conductor web dashboard has NO HTTP gate-respond endpoint. Gates can only be answered via dashboard UI (websocket) or process TTY. Verified 2026-05-24 against conductor 0.1.16.
- 📌 2026-05-28: Participated in squad-wide initial concerns review (10-agent fan-out). Surfaced 3 top conductor-mechanics concerns: output-schema contracts undersized at cross-leg boundaries, route conditions doing script work, re-entry stability sound but node-ID pinning brittle. Nominated short-term wins: normalize renegotiation output schema (null vs empty, structured vs JSON-as-string), add dedicated error-aggregation node.
- 📌 **Conductor remote naming (all worktrees):** `origin` = microsoft/conductor (upstream); `fork` = PolyphonyRequiem/conductor (Daniel's fork). Always push feature/dogfood branches to `fork`, not `origin`.
- 📌 **Polyphony conductor pin:** CI installs `git+https://github.com/microsoft/conductor.git@main` — no version pin. Local dev may be behind.
- 📌 **v0.1.18 context.py semantic conflict:** `_add_agent_input` in `context.py` has a semantic incompatibility between the v0.1.18 set-step PR (`is_dict_output` flag + `None` seed init for non-dict outputs, required by `TestWorkflowContextNonDictOutputs`) and error-routing PR #229 commit `17c7cc7` (`agent_outputs.get()` + simplified `{"output": {}}` init). These conflate "agent never ran" with "agent produced None output". Resolution requires a `_MISSING` sentinel or keeping `is_dict_output` init + adding error-path branch separately. **Escalated to PR author.**
- 📌 **Notifications rebase onto v0.1.18:** All conflicts mechanical. `AgentDef.type` Literal union: combine both sets of type values. Adjacent field/validator blocks: include both sides in order. Pushed to `fork/feat/notifications`, HEAD `7d46793`.
- 📌 **Error-routing rebase:** Aborted at commit 3/14 (context.py semantic conflict). Branch stays at original `efa520f`-based HEAD. NOT pushed.
- 📌 **Dogfood worktree:** `C:\Users\dangreen\projects\conductor-dogfood`, branch `dogfood/on-error+notifications`, HEAD `4f5c3fc`. Base `efa520f` (pre-v0.1.18) to avoid context.py conflict. Includes full error-routing (14 commits, merged) + notifications (cherry-pick of `f5397cd`). Install: `pip install -e .`. See `DOGFOOD-INSTALL.md` in that worktree.
- 📌 **Test env:** Python 3.14 at `C:\Users\dangreen\AppData\Local\Python\pythoncore-3.14-64\python.exe`. `pip install pytest-asyncio` required for error-routing async tests. 61 new feature tests pass. 324 broader failures in `test_script.py` are pre-existing Windows subprocess issues, not caused by dogfood.
- 📌 **Top adoption pick (free win):** `conductor validate` now warns on undeclared `agent.output` refs (commit `8ec298d`). Add to CI — surfaces Jinja path drift at PR time. Filed as polyphony issue #540.
- 📌 **Adoption issues filed:** #537 (script output schemas), #538 (type: terminate), #539 (type: set), #540 (conductor validate CI). Decision doc: `.squad/decisions/inbox/mahler-conductor-dogfood-and-adoption-2026-05-28T223616-0700.md`.
- 📌 **2026-05-28 upstream sync:** Raised context.py design conflict as comment on conductor PR #229 (comment link: https://github.com/microsoft/conductor/pull/229#issuecomment-4569235887). Dogfood pinned at v0.1.17 indefinitely pending upstream resolution. DOGFOOD-INSTALL.md updated to document the pin decision and blocker. Attempted cherry-pick of notifications rename (`27006af`) but encountered conflicts in schema.py and validator.py; abandoned per directive to avoid local busywork. Dogfood remains pre-rename (`type: notification` + `notification:` key). Wagner's smoke workflow will need vocab update post-upstream redesign. This is the new working norm per directive #2: raise questions on upstream PRs and accept the version-pin result, do not pursue local rebases or escalate to PR authors.

## Learnings — 2026-05-28

### Mahler (Conductor Expert)

**Current focus:** Dogfood environment setup + conductor adoption survey  
**Status:** Dogfood environment created; Phase 1 + notifications tests passing; error-routing rebase blocked

**Session round outcomes:**
- ✅ Created dogfood worktree + branch (`dogfood/on-error+notifications`) at `efa520f` base
- ✅ Documented DOGFOOD-INSTALL.md + .squad/skills/dogfood-conductor/SKILL.md
- ✅ Ran adoption survey: 12 commits reviewed (v0.1.16→v0.1.18); 10 marked 🟢 Adopt now, 4 marked 🟡 Worth follow-up
- ✅ Filed adoption survey issues #537-#540 (script output schemas, type: terminate, type: set, conductor validate in CI)
- ✅ Applied 5 amendments to PR #213 (notifications): added validators, renamed `notification:` → `emit:`, dropped workflow_metadata, fixed coerce error handling
- ✅ All 18 notification tests pass ✅
- **BLOCKER:** Identified semantic conflict in PR #229 (error-routing) rebase: `agent_outputs.get()` vs subscript + `None`-seed initialization in context.py

**Open questions:**
- Daniel's call on context.py conflict resolution (sentinel pattern or keep `is_dict_output`?)
- Feature verification: `{{ conductor.run_id }}` availability, `type: wait` templated `seconds:`

**Next moves:**
- Wait for Daniel's conflict resolution decision
- Verify conductor feature gaps in dogfood once decision made
- Rebase PR #229 onto v0.1.18 after conflict resolved

### 2026-05-28T23:43-14Z — Inbox round (Scribe merge)
- ✅ Dogfood conductor pinned v0.1.17 decision captured
- ✅ PR #229 comment history recorded
- ✅ DOGFOOD-INSTALL.md pin note confirmed (commit 0044d84)
- Ready for next conductor upstream update

## Learnings — 2026-05-29

- 📌 **Conductor `CheckpointManager` is NOT durable per-root-item state.** Stores to `$TMPDIR/conductor/checkpoints/` keyed by workflow name + timestamp. Not root-item-ID scoped, not reboot-safe. Filesystem (`.polyphony/state/{rootId}/`) is the only durable cross-run persistence layer.
- 📌 **`max_attempts` / `RetryPolicy` is agent-node-only.** `schema.py` line 449/843 explicitly: "Only applies to provider-backed agents (not script or human_gate)." Script nodes have no built-in retry. Retry-on-network for seeding scripts must be script-internal.
- 📌 **`on_exhaust` parameter does not exist in conductor schema.** The proposed `on_error: { exit_code: 3, max_attempts: 3, on_exhaust: stop }` syntax is not real. Do not cite it in YAML examples.
- 📌 **`seeding_blocked` should be a new terminal, not `workflow_abandoned`.** Per Beethoven's trigger analysis, `workflow_abandoned` is semantically "operator gave up" (all routes volitional). Exhausted infra retries are NOT volitional. Routing them to `workflow_abandoned` corrupts the terminal's meaning.
- 📌 **Corrupt-state guard is workflow + script responsibility, not engine.** The engine has no reconciliation concept. Workflow refuses to advance without `reconciliation_passed: true` from the seeding script. Atomic writes (`.tmp` → rename) are a seeding script implementation requirement.
- 📌 **Re-entry requires seeding plan file + ADO state together.** Plan file = intent; ADO state = truth. Plan file exists but no ADO children = corrupt/partial. Both must be checked on re-entry per P3 (Re-Entry by State Discovery).

### 2026-05-29T09:33-07:00 — Seeding plan engine stance (Q1 ask)
- ✅ Investigated `checkpoint.py` and `schema.py` in conductor-dogfood for persistent state and retry primitives
- ✅ Confirmed: no new conductor engine changes needed for seeding plan pattern
- ✅ Confirmed: `max_attempts` not available on script nodes
- ✅ Filed handoff: `.squad/handoffs/mahler-seeding-plan-engine.md`
- ✅ Filed decision: `.squad/decisions/inbox/mahler-seeding-plan-engine-stance.md`
- Coordinated with Beethoven's `workflow_abandoned` trigger analysis (Route D / `seeding_blocked` recommendation)

### 2026-05-29T17:23-07:00 — Seed Manifest ADR shipped
- ✅ ADR `seed-manifest-as-durable-state.md` shipped with all ten design decisions encoded
- Your key findings embedded: no new engine primitives, no durable checkpoint state, `max_attempts` script limitation, `seeding_blocked` terminal recommendation
- When implementing seeding-plan verb + script, reference ADR "Mahler — Conductor Engine Stance" section for design constraints (especially: script-internal retry circuit, error-to-terminal routing via tagged error kinds, atomic `.tmp`+rename writes)

## Learnings — 2026-05-31

### 2026-05-31T10:51-07:00 — on_error retrofit assessment + conductor gaps report

- 📌 **PR #229 (`on_error:` routing) has NOT merged to `origin/main` as of 2026-05-31.** `origin/main` is at v0.1.18 (`085b7a5`). The `on_error:` feature lives exclusively in `dogfood/on-error+notifications`. Polyphony CI installs from `@main` with no pin — polyphony currently gets v0.1.18 (no on_error support). All AB#3257 work remains blocked on upstream merge.

- 📌 **`internal.script_error` only fires when node opts in via `raises:` OR any `on_error:` route present.** Without opt-in, non-zero script exit is legacy behavior (no envelope, no error routing). Documented in `engine/errors.py` docstring. Adding an `on_error: true` route to a node that exits non-zero on real failures WILL change behavior — audit required before Phase 2 retrofit.

- 📌 **Sub-workflow error envelope propagation is explicitly deferred to Phase 2.** `workflow.py` lines 1207–1217: `UnhandledWorkflowError` from a child is caught and re-raised as generic `ExecutionError`. Typed kind is lost. Parent's `on_error:` can only match `internal.script_error`. The three-tier polyphony dispatch stack (polyphony → root-batch → root-item → lifecycle) amplifies this: typed errors from plan-level or actionable are completely opaque to the outer loop.

- 📌 **plan-level.yaml error-gate overhead: 581 lines (18% of file).** 13 error-gate node definitions, lines measured: root_resolver_error_gate (23), type_loader_error_gate (27), ancestor_chain_error_gate (46), state_detector_error_gate (25), write_plan_error_gate (35), ensure_plan_branch_error_gate (35), commit_and_push_error_gate (39), open_plan_pr_error_gate (70), poll_error_gate (53), merge_error_gate (110), seeder_error_gate (49), open_plan_pr_ado_error_gate (39), merge_plan_pr_ado_error_gate (30).

- 📌 **Polyphony verb exit-0 contract means on_error won't fire for verb semantic errors without C# changes.** All verbs exit 0 and write `{"error": "..."}` to stdout JSON. The `internal.script_error` synthetic kind only fires on non-zero exit. Wagner's Phase 2 recommendation (Option C): use `on_error:` for infrastructure scripts (git, HTTP) that genuinely exit non-zero; keep success-path routing for polyphony verb calls. The 87 `output.error` checks in `when:` conditions will NOT be eliminated by Phase 1+2.

- 📌 **`retry:` route action does not exist in conductor (Phase 1 or upstream).** Phase 1 adds catch-and-route. It cannot retry the current node. The existing `RetryPolicy` is agent-node-only. 14 of 19 AB#3257 gates need retry+abort — they are blocked until RFC Phase 2 designs the `retry:` route action. These 14 gates currently abort silently (operators must re-trigger manually).

- 📌 **Conductor does NOT support dynamic `workflow:` paths.** `workflow: "./{{ classify.output.lifecycle }}.yaml"` is invalid. Branch-on-router with N explicit `type: workflow` nodes is the canonical workaround. root-item-dispatch.yaml has 4+1 lifecycle dispatch nodes as a result. Documented explicitly in root-item-dispatch.yaml header comment.

- ✅ Filed handoff: `.squad/handoffs/mahler-conductor-gaps-20260531.md`
- ✅ Filed decision: `.squad/decisions/inbox/mahler-on-error-greenlight-ask.md`
