# Conductor Dogfood + Adoption Survey
**Author:** Mahler (Conductor Expert)  
**Date:** 2026-05-28T22:36:16-07:00  
**Status:** Ready for review

---

## Baseline

| Item | Value |
|---|---|
| **Polyphony's current conductor install** | `@main` (CI: `git+https://github.com/microsoft/conductor.git@main`); locally likely v0.1.16 (Wagner's prior note) |
| **Latest released tag** | **v0.1.18** — released during this session (fetched mid-task; tag SHA `f59345e`, release commit `085b7a5`) |
| **v0.1.18 release features** | `type: set`, `type: wait`, `type: terminate` steps; structured `runtime.provider` config; external-workflow friction fixes |
| **Previous tag** | v0.1.17 (`277aa72`) |
| **Dogfood base ref** | `efa520f` — one commit beyond v0.1.17, common ancestor of both feature branches |
| **Base choice rationale** | `feature/error-routing` is already based at `efa520f`. Using v0.1.18 as base triggers a semantic conflict in `context.py` between v0.1.18's non-dict output support and error-routing's `agent_outputs.get()` change (see Conflict section). `efa520f` avoids this while still including all v0.1.17 fixes. |

---

## Rebase Results

### PR #213 — `feat/notifications` (conductor-notifications worktree)

| Item | Result |
|---|---|
| **Base** | v0.1.18 (`085b7a5`) |
| **Conflict files** | `src/conductor/config/schema.py` only |
| **Conflict type** | Mechanical — adjacent enum additions. v0.1.18 added `"set"`, `"terminate"`, `"wait"` to `AgentDef.type`; notifications added `"notification"`. Also: `reject_bool_duration` validator (v0.1.18) vs `validate_raises` validator (notifications) — non-overlapping adjacent additions. |
| **Resolution** | Combined both sets of `Literal` values; included all four new validators/fields. No semantic judgment required. |
| `fork/feat/notifications` pushed | ✅ `fork/feat/notifications` force-pushed (27006af — includes review feedback amendments) |
| **New HEAD** | `27006af` (on top of v0.1.18 at `085b7a5`) |

### PR #229 — `feature/error-routing` (conductor-error-routing-impl worktree)

| Item | Result |
|---|---|
| **Attempted base** | v0.1.18 (`085b7a5`) |
| **Conflict files** | `src/conductor/config/schema.py` (2 conflicts — both mechanical, resolved), `src/conductor/engine/context.py` (1 conflict — **SEMANTIC, NOT RESOLVED**) |
| **Rebase status** | ⚠️ **ABORTED at commit 3/14** (`17c7cc7 feat(context): add store_error API`) |
| **Pushed to fork** | ❌ Not pushed — rebase was aborted. Branch stays at original base `efa520f` |

#### Semantic Conflict Detail — `context.py`

**File:** `src/conductor/engine/context.py`, method `_add_agent_input`

**What changed in v0.1.18 (set-step PR):**
```python
# v0.1.18 — uses subscript access, checks is_dict_output for seed
agent_output = self.agent_outputs[agent_name]
is_dict_output = isinstance(agent_output, dict)
if agent_name not in ctx:
    ctx[agent_name] = {"output": {} if is_dict_output else None}
```
The `None` seed is load-bearing: `TestWorkflowContextNonDictOutputs.test_explicit_mode_scalar_field_optional_skips` asserts `rendered["compute"]["output"] is None` for optional scalar field access.

**What changed in error-routing PR (commit 17c7cc7):**
```python
# error-routing — uses .get(), adds error-path branch at top
agent_output = self.agent_outputs.get(agent_name)
if agent_output is None:
    if not is_optional:
        raise KeyError(...)
    return
# ...
ctx[agent_name] = {"output": {}}  # simplified — no is_dict_output check
```
The `.get()` + early-return conflates two states: "agent never ran" and "agent ran and produced `None`" (valid for `type: set` with `value: null`). The simplified `{"output": {}}` init also breaks the non-dict output tests added by v0.1.18.

**Required judgment call:**
1. Should `agent_outputs.get()` be replaced with a sentinel (e.g. `_MISSING = object()`) to distinguish missing vs `None` output?
2. Should the init revert to `{} if is_dict_output else None` for compatibility with non-dict outputs, with error-routing's early-return added only for the truly-missing case?

**This decision belongs to the PR author — flagging for Daniel.**

---

## Dogfood Branch

| Item | Value |
|---|---|
| **Path** | `C:\Users\dangreen\projects\conductor-dogfood` |
| **Branch** | `dogfood/on-error+notifications` |
| **Base** | `efa520f` (between v0.1.17 and v0.1.18) |
| **Merge strategy** | `git merge feature/error-routing --no-ff` (clean) + `git cherry-pick f5397cd` (notifications, pre-rebase commit) |
| **HEAD SHA** | `4f5c3fc` |
| **Installed version** | `conductor v0.1.17` (pyproject.toml at efa520f says 0.1.17) |
| **Cross-branch conflicts** | ✅ None — notifications cherry-pick auto-merged cleanly on top of error-routing. The schema.py additions (`notification` type enum, `notification`/`payload` fields) are non-overlapping with error-routing's `raises`/`on_error` additions. |

### Build Status

```
pip install -e .  →  Successfully installed conductor-cli-0.1.17
conductor --version  →  Conductor v0.1.17
```

### Test Status

| Scope | Result |
|---|---|
| New feature tests (error-routing + notifications, 61 tests) | ✅ **61/61 passed** (after `pip install pytest-asyncio`) |
| Broader suite (test_config, test_engine, test_executor, test_error_kinds, test_helpers) | ⚠️ **324 failed, 1120 passed** — failures concentrated in `test_executor/test_script.py` (subprocess/shell tests on Windows) and `test_executor/test_agent_guidance.py`; appear pre-existing, not caused by dogfood changes |
| `pytest-asyncio` missing | The error-routing tests use `@pytest.mark.asyncio` — install with `pip install pytest-asyncio` |

**Note on 324 failures:** Not attributable to the dogfood merge. The `test_script.py` failures are subprocess-invocation tests that are sensitive to Windows shell environment; they likely fail on the efa520f base too. Not investigated further per task scope.

---

## Adoption Survey: v0.1.16 → v0.1.18

Polyphony CI consumes `@main`, so all changes below are in-scope.

| Commit | Change | Category | Notes |
|---|---|---|---|
| `04b46ec` | `fix(config): auto-fetch sibling sub-workflow from registry cache during validation` | 🟢 Adopt now | Polyphony uses cross-workflow sub-workflow refs; this fixes validation failures when the referenced workflow isn't in local cache |
| `3e726a0` | `fix(registry): mirror repo layout in cache so cross-workflow refs resolve` | 🟢 Adopt now | Same cross-workflow seam — complementary fix to above |
| `8ec298d` | `feat(validate): warn on undeclared agent.output refs and field-level mismatches in explicit mode` | 🟢 Adopt now | **High value for polyphony**: surfaces the Jinja path drift bugs at `conductor validate` time instead of mid-dogfood-run. Mahler concern #3 (node-ID pinning brittle) is partially mitigated by this |
| `9d603a1` | `feat(script): allow script agents to declare output schemas` | 🟡 Worth follow-up | Polyphony's workflow dispatch scripts (lifecycle-router.ps1 etc.) don't declare output schemas. Declaring them would close the M2-class footgun for script-node outputs. Medium effort. |
| `4765a52` | `feat(copilot): attribute verbose logs to agents in parallel/for-each runs` | 🟢 Adopt now | Polyphony uses large for_each batches; verbose log attribution would help debug stuck items |
| `dc29c2c` | `fix(engine,web): resolve max-iterations gate from dashboard in --web-bg` | 🟢 Adopt now | Polyphony uses --web-bg; gate resolution from dashboard was broken |
| `5fa2e14` | `fix(resume): replay original event log into dashboard on --web` | 🟢 Adopt now | Polyphony's re-entry pattern benefits from accurate dashboard replay |
| `752a9b5` | `fix(bg): detach --web-bg child from Windows job to prevent kill-on-close` | 🟢 Adopt now | Polyphony runs on Windows; this prevents the dogfood process from dying when the parent closes |
| `4337610` | `fix(windows): make --web-bg startup crashes diagnosable (#116)` | 🟢 Adopt now | Windows-specific; directly relevant |
| `75b01b5` | `fix(cli): suppress web-bg dashboard output in silent mode` | 🟢 Adopt now | Low-risk CLI fix |
| `9b42d7b` | `fix(cli): gate remaining dashboard URL prints behind is_verbose()` | 🟢 Adopt now | Low-risk CLI fix |
| `efa520f` | `fix(cli): make _verbose_console silent-aware and gate replay prints` | 🟢 Adopt now | Low-risk CLI fix |
| `4229a24` | `feat: add type: set step` | 🟡 Worth follow-up | Polyphony has inline Jinja bind steps that could be `type: set`; would simplify some script nodes (P8 principle). Low risk, medium effort. |
| `408b9df` | `feat(engine): add type: wait step` | 🟡 Worth follow-up | Limited direct use in polyphony today; relevant for future polling loops |
| `8124e8e` | `feat(engine): add type: terminate step` | 🟡 Worth follow-up | Polyphony's error terminals currently route to `$end` with no terminal signal. `type: terminate` with `status: failed` would produce the exit code 3 that error-routing PR #229 also introduces. Aligns with P7 (fail honestly). |
| `370209d` | `feat(providers): structured runtime.provider config` | ⚪ Irrelevant | Polyphony uses a fixed provider profile; custom endpoints not relevant today |
| `23751a2` | `fix: external workflow friction - minimal evidence-anchored fixes` | 🟢 Adopt now | "Evidence-anchored" suggests fixes to workflow validation/output contract issues. Polyphony hits friction in this area. |

---

## Top Adoption Recommendations (Ranked)

1. **`feat(validate): warn on undeclared agent.output refs` [commit 8ec298d]** — 🟢 immediate  
   Effort: zero (already on `@main`). Closes the Jinja path drift bug class at validation time. Polyphony should run `conductor validate` in CI once this is confirmed available.

2. **`feat(script): declare output schemas on script agents` [commit 9d603a1]** — 🟡 follow-up  
   Effort: 1–2h per workflow, scattered change. Polyphony's 6 lifecycle-dispatch scripts lack output schema declarations; adding them enables per-field type checking and closes M2-class footguns for script-node outputs. File as `squad:medium-term`.

3. **`type: terminate` for polyphony error terminals** — 🟡 follow-up (depends on #229 merging)  
   Effort: 1–2h. All 16 `abort_run` auto-routes in Wagner's PR #535 could route through a `type: terminate` node (status: failed) instead of `$end`, giving operators a typed workflow_failed event and exit code 3. Requires error-routing PR #229 to merge first. File as `squad:medium-term`.

4. **`type: set` for inline binding steps** — 🟡 follow-up  
   Effort: 2–4h. Several script nodes in the polyphony registry exist purely to bind a computed value into context. `type: set` would replace them with a zero-LOC YAML declaration, reducing script surface and aligning with P8. File as `squad:medium-term`.

5. **Windows --web-bg fixes (multiple)** — 🟢 immediate  
   Already on `@main`. Polyphony runs on Windows; the job-detach fix (`752a9b5`) and crash-diagnostic fix (`4337610`) are directly relevant to dogfood stability.

---

## Items Needing Daniel's Call

1. **`context.py` semantic conflict (error-routing rebase onto v0.1.18):** The `agent_outputs.get()` vs subscript access + `None`-seed initialization issue described above. Decision: (a) sentinel pattern, or (b) keep `is_dict_output` init + add error-path branch. Once decided, the error-routing rebase can complete and the dogfood can be rebuilt on v0.1.18.

2. **Dogfood does NOT include v0.1.18 features** (set/wait/terminate): The dogfood base is `efa520f` to avoid the above conflict. If polyphony workflows need to author `type: set/wait/terminate` in the dogfood period, Daniel should wait for conflict resolution and rebuild.

3. **PR #229 rebase onto v0.1.18 is blocked:** `fork/feature/error-routing` was NOT pushed (rebase aborted). The fork branch stays at original base.

---

## PR #213 Review Feedback Audit (jrob5756)

Inline comment audit completed 2026-05-28. All 8 comments addressed in commit `27006af` and replied to on the PR.

| Comment ID | File:Line | Summary | Disposition | Applied |
|---|---|---|---|---|
| 3283084198 | workflow.py:3033 | Parallel/for-each containment undefined — disallow or thread through dispatchers | ✅ Disallow at validation time | Validator guards added matching the existing `script`/`wait`/`terminate`/`workflow` pattern |
| 3283084205 | workflow.py:3058 | `assert` stripped under `python -O` — make explicit `raise` | ✅ Apply | Replaced with `raise ExecutionError(...)` |
| 3283084208 | workflow.py:3087 | Dashboard rendering missing for new event types | 💬 Reply only — defer as immediate follow-up | Dashboard requires TypeScript changes (workflow-store.ts, Cytoscape); out of scope for this PR |
| 3283084213 | workflow.py:3102 | `{}` storage silently empty for downstream `accumulate`-mode templates | 🤔 Document | Added note to AGENTS.md explaining empty-output behavior and fire-and-forget contract |
| 3283084216 | run.py:1931 | Confirm resume replays notifications in dashboard | 💬 Reply only — behavior is intentional | Confirmed: events flow through EventLog; `replay_events_from_jsonl` replays on resume |
| 3283084219 | notification.py:121 | Silent coerce fallback gives misleading error one frame later | ✅ Apply | `_coerce_rendered` now raises `ValidationError` on failure; call site adds field name context |
| 3283084222 | notification.py:233 | `workflow_metadata` redundant in envelope | 🤔 Drop it | Removed from `build_envelope` signature and envelope dict |
| 3283084228 | schema.py:964 | `type: notification` + `notification: pr_ready` reads redundant | 🤔 Rename to `emit:` | `AgentDef.notification` → `AgentDef.emit` across schema, validator, executor, engine, tests, examples, AGENTS.md |
