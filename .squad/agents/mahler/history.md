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
