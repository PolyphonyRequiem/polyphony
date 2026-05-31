# Conductor Gaps — Consolidated 7-Agent Squad Report
**Date:** 2026-05-31
**Coordinator:** Squad Coordinator (synthesizing Mahler, Bach, Wagner-1, Liszt, Stravinsky, Beethoven, Brahms)
**Requested by:** Daniel Green (offline ~1 hour)
**Inputs:** Three asks: (1) is on_error the "huge cleanup" we hoped? (2) install "holographic" for memory. (3) where else does conductor hurt us at scale?

---

## 0. TL;DR (one screen)

### on_error verdict — **Real cleanup, but not the slam dunk we thought.**
- Eliminates **~830 lines** of YAML, **27 error-gate nodes**, **42 routes**, across **8 workflow files**. Mahler grades it **4/5**, not 5/5.
- **Three-phase delivery**, not one:
  - Phase 1 (already designed, blocked on merge) — 5 of 19 idempotent-retry gates.
  - Phase 2a (catch-all abort) — 5 more gates.
  - Phase 2b — 14 gates, the **actual UX win** (transient network = silent recovery instead of silent abort). Requires a `retry:` route action that **conductor has not yet designed.**
- **Critical blocker:** conductor PR #229 has NOT merged upstream. v0.1.18 has no on_error at all. The dogfood branch sits at v0.1.17 + 14 cherry-pick commits. The merge is gated on a `context.py` sentinel conflict raised on 2026-05-28 that the upstream team must resolve.
- **Implication for PR #547:** Don't frame future PRs as "Phase 2 complete" with only Phase 1 gates — call it Phase 2a and scope Phase 2b explicitly.

### The single highest-leverage conductor gap — **universal 6/7 consensus**
**`on_error:` + `retry:` route action for `type: script` nodes.** Six of seven agents named this their #1 gap, naming it from completely different seats (engine expert, workflow author, script author, mission keeper, architect, testability). The convergence is the strongest signal in the entire fan-out.

### Fork verdict — **No, contribute. But the runway is shorter than it looks.**
All three agents asked about it (Bach, Beethoven, Mahler) independently said: **don't fork**. But Beethoven's framing matters: *"the risk is not absence of a path — it's contribution delay."* Every week these gaps stay open, polyphony embeds deeper workarounds (routing-style envelopes are now in ~50 verbs; `compose_addendum` is in 3 workflows; aggregator scripts are in the renegotiation critical path). When conductor finally ships, retiring those workarounds is active work, not automatic.

### "Holographic" memory — **I could not identify what you meant.**
Searched: `~/.copilot/skills/`, `~/.copilot/agents/`, `~/.copilot/mcp-config.json`, npm, GitHub, your repos, PATH. No match. Closest npm hit is a placeholder package (`holographic@0.0.1`, "Reserved for future development"). See §6 for candidates and best-guess questions back to you.

---

## 1. Mahler's Deep Verdict on the on_error Retrofit

### What still exists post-PR #535

After PR #535 removed 19 trivial gates, the codebase still carries:

| File | Error-gate defs | Routes to gates | `output.error` checks |
|---|---:|---:|---:|
| `plan-level.yaml` | **13** | **15** | 29 |
| `actionable.yaml` | 2 | 10 | 18 |
| `feature-pr.yaml` | 3 | 4 | 5 |
| `implement-merge-group.yaml` | 3 | 6 | 12 |
| `ado-pr.yaml` | 2 | 3 | 7 |
| `github-pr.yaml` | 1 | 1 | 2 |
| `polyphony.yaml` | 2 | 2 | 12 |
| `restack-remedy.yaml` | 1 | 1 | 2 |
| **TOTAL** | **27** | **42** | **87** |

`plan-level.yaml` alone has **13 error gates occupying 581 lines — 18% of the file.** The worst single gate, `merge_error_gate`, is 110 lines of diagnostic prompt — and every route ultimately ends at retry or abort_run. Zero operator judgment is required.

### What Phase 1 (PR #229) provides — and what it doesn't

✅ **In Phase 1:** `on_error: true`, `on_error: "some.kind"`, `on_error: [list]`, `raises: [kinds]`, `$CONDUCTOR_ERROR_OUT` env var, `internal.{script_error,schema_violation,undeclared_kind}`, `{{ node.error.kind }}` templating, when-conditions on error routes. **61 tests passing in dogfood.**

❌ **NOT in Phase 1 (gaps for Phase 2):**
- `retry:` route action — **14 of 19 AB#3257 gates blocked**
- Post-retry-exhaustion routing
- Sub-workflow error envelope propagation (parent only sees generic `ExecutionError`)
- Workflow-level default `on_error:`
- `provider.exhausted` routable kind

### The sneakiest blocker: polyphony verb exit-0 semantics

Every polyphony CLI verb exits 0 and writes `{"error": "...", "success": false}` to stdout JSON on semantic failure. For `on_error:` to fire on verb failures, one of:
- (A) Verbs also write to `$CONDUCTOR_ERROR_OUT` on failure (C# change, Mozart/Liszt scope)
- (B) PowerShell wrappers detect `output.error` and re-emit (Liszt scope)
- (C) Keep success-path routing for verbs; use `on_error:` only for genuine infrastructure scripts (git, HTTP polls)

**Wagner's Phase 2 plan is Option C.** This means the 87 `output.error` checks in `when:` conditions **will NOT be eliminated** by Phase 1+2 — they stay for the polyphony verbs. The 27 error-gate nodes target infrastructure failures; those are what move to `on_error:`.

### Worked example — `write_plan_error_gate`

**Before** (37 lines of YAML, including the gate node + the route condition).
**After Phase 1** (5 lines):
```yaml
- to: commit_and_push           # success path
- to: abort_run                 # catch-all abort on script error
  on_error: true
```
**After Phase 2 (with retry)** (7 lines):
```yaml
- to: commit_and_push
- on_error: true
  retry: { max: 3, backoff: exponential, initial_seconds: 5 }
- to: abort_run
  on_error: true
```

### Risk surface Mahler flagged

1. **Silent abort regression** — Phase 1 retrofits change "pause for human" → "auto-abort." Intended UX, but no chance to inspect state before abort.
2. **Retry storms** — Phase 2 needs careful audit: `open_plan_pr` looks idempotent, but if it succeeds on attempt 1 and returns an error envelope due to a parsing bug, retry creates N duplicate PRs.
3. **`internal.script_error` opt-in semantics** — Without explicit `on_error:` route or `raises:` declaration, non-zero exit is LEGACY behavior (no envelope raised). Adding `on_error: true` to existing nodes that exit non-zero on real failures CHANGES behavior. Per-node audit needed.
4. **Error-disposition signal loss** — PR #535 removed the original `workflow_error_gate` retry-at-executor path. Phase 2 should restore it via `on_error:` + retry.
5. **`seeder_error_gate` auto-continue danger** — Currently routes catastrophic failures to `child_router` (continues). Phase 2 must route to `abort_run`. (Already covered by Beethoven's D1 decision.)

### Mahler's net score
**4/5. Big, but gated on two upstream deliverables.** The 14 retry+abort gates are the actual UX win, and they require a NOT-YET-DESIGNED `retry:` route action. Phase 1 alone is a smaller win (5 gates).

---

## 2. The Top Conductor Gaps — Cross-Agent Convergence

Where multiple agents independently named the same gap, the signal is strong. Where only one agent named it, the gap is real but specific to their seat.

### Gap A — `on_error:` + `retry:` for `type: script` nodes
**Named by:** Mahler, Bach, Wagner-1, Liszt, Beethoven, Brahms **(6/7)**
**Impact:** 5/5 universal.
**Different angles:**
- **Mahler (engine):** 27 gates, 830 lines; Phase 2 `retry:` action not yet designed.
- **Bach (seam):** Pure engine concern leaking into polyphony domain code (verb-error-boundary ADR exists *only* because of this gap).
- **Wagner-1 (workflows):** Invisible global "exit 0 always" contract; no linter for it; new contributors learn it by breaking it.
- **Liszt (scripts):** `Poll-PrStateDelta.ps1` proves it's architecturally impossible for blocking pollers to fold errors into exit 0.
- **Beethoven (mission):** 19 gates burning human attention for deterministic decisions — direct gate-philosophy violation (§5.2).
- **Brahms (tests):** Error paths are **untestable** because the harness has no `exit_code: 1` simulation. Blocks detection of ~20% of polyphony failure modes.

### Gap B — Output schema declared but not enforced
**Named by:** Bach, Brahms, Stravinsky **(3/7)**
**Impact:** 4/5.
- **Stravinsky:** Every `type: agent` carries an `output:` block declaring field names and types. **Conductor validates none of them at runtime.** A capitalization drift on `verdict` ("approved" vs "Approved") silently falls through to catch-all → coder retry loop. At `max_iterations: 200` and ~30-80K tokens per invocation, this is the **highest token-cost gap** in the analysis.
- **Bach:** The `CONDUCTOR_OUTPUT` schema contract between C# JSON serialization and YAML Jinja paths is implicit. Field-name drift caused dogfood regressions; verb-output-schema-registry ADR exists as "Proposed."
- **Brahms:** Sub-workflow output schemas are also unvalidated — parents read `{{ child.output.foo }}` via untyped Jinja paths; if a child stops emitting, parents silently get null at runtime.
- **Mitigation already underway:** Mozart's `[VerbResult(typeof(X))]` attribute backfill is ~4h of mechanical work. Unblocks the Jinja path lint.

### Gap C — Sub-workflow error envelope propagation
**Named by:** Mahler, Beethoven, Brahms **(3/7)**
**Impact:** 4/5.
- **Mahler:** When a sub-workflow hits `UnhandledWorkflowError`, the parent only sees a generic `ExecutionError`. The typed envelope (kind/message/details) is swallowed. `workflow.py:1207–1217` explicitly says "Phase 2 will introduce envelope propagation with parent frames."
- **Beethoven:** Polyphony's three-tier dispatch (polyphony → root-batch-dispatch → root-item-dispatch) has a ~100-line `aggregate_renegotiation` PowerShell script that manually parses `for_each` error shape. **Silent aggregation failure = missed surrender gate.**
- **Brahms:** Parents must trust child JSON contract because conductor has no schema validation at sub-workflow boundaries.

### Gap D — Agent context injection (skills / MCPs / prompt_addendum)
**Named by:** Beethoven, Stravinsky **(2/7)**
**Impact:** 4/5.
- **Beethoven:** `compose_addendum` is **orchestration behavior** — deciding which capabilities bind to this invocation. Direct north-star §4.1 violation: "No orchestration runtime inside polyphony."
- **Stravinsky:** Two conflicting tool lists at the same prompt authority — conductor's `tools:` (actually connected) vs the addendum's recommended MCPs (advisory text). Agent has no mechanical way to reconcile. Skills are injected as *names*, forcing an extra filesystem tool call at agent runtime to read the SKILL.md.
- **Fix is upstream:** Schema extension, not redesign.

### Gap E — Dynamic `workflow:` path templating
**Named by:** Bach, Mahler **(2/7)**
**Impact:** 3-4/5.
- Every lifecycle dispatch requires a hand-maintained branch table in `root-item-dispatch.yaml`. Adding a new lifecycle (research, spike, etc.) = surgical edit across two files. The branch-on-router pattern works but is "load-bearing dead weight" (Bach).
- Implementation tension: dashboard wants static graph rendering. Acceptable fix: `workflow_variants: [...]` annotation for pre-loading + `workflow: "{{ expr }}"` at runtime.

---

## 3. Per-Agent Unique Signals (1/7 — still real, narrower)

### Wagner-1 — Declarative mid-graph re-entry anchor
**Gap:** `entry_point:` is static; "resume" means restarting from the top and praying every node is idempotent. Every workflow with re-entry needs its own `state_detector` pattern (plan-level.yaml has 4 such nodes). **When re-entry breaks, the failure mode is silent duplicate work, not a clear error.**
**Fix shape:** Conductor evaluates `resume_from:` conditions against observable state at workflow start.

### Liszt — Between-invocation state primitive (`type: store`)
**Gap:** `Poll-PrStateDelta.ps1` invents file-based watermarks in `$TEMP`. Silently resets on machine-boundary, reboot, or run cancellation. **The entire `initial_observation` re-entry loop exists only because conductor has no "first-run" primitive.**
**Fix shape:** `type: store` with `op: get/set` + `key:` template, OR a workflow-scoped `state:` section with env-var injection.

### Bach — `run_id` correlation template variable
**Gap:** Conductor knows its own run_id (it's in `data.run_id` of `.notifications.jsonl`) but doesn't expose `{{ conductor.run_id }}` to workflow templates. Polyphony threads it through as a workflow input on every domain-signal node.
**Fix shape:** First-class `{{ conductor.run_id }}` (or `{{ env.CONDUCTOR_RUN_ID }}`). Small change; high cleanliness payoff.

### Bach — Cross-run artifact persistence
**Gap:** `CheckpointManager` is single-run-scoped. The seed manifest ADR exists *only* because conductor has no named-artifact store keyed by `(workflow_name, root_identifier)`.
**Fix shape:** `conductor artifact read/write` API. Not urgent (seed manifest works), but a category error in the seam.

### Beethoven — Workflow checkpoint / durable step state (P3 absorption)
**Gap:** Killed workflows restart from `entry_point`. Polyphony absorbed this into every CLI verb (idempotency discipline). Large runs (epic-scale, days) re-run completed architect/reviewer agents on resume — burns LLM tokens and breaks operator trust ("resume means continue, not restart").

### Stravinsky — Context window observability (token budget)
**Gap:** No way for conductor to emit `prompt_tokens` to the workflow. Mid-turn truncation (output truncated, not input) is the most dangerous failure mode — produces partially-formed JSON that passes nominal validation but is wrong (e.g., missing `research_request_kind`).
**Affordable near-term:** Just emit `conductor.agent_prompt_tokens` as a node output. Even observability alone unblocks post-hoc triage.

### Brahms — In-process .NET conductor SDK for tests
**Gap:** `tests/harness/` runs real conductor with Python's FakeProvider. The .NET test suite (`tests/Polyphony.Tests/`) has **zero workflow-level integration tests** because conductor offers no .NET test-mode library. Bugs found by the harness can't be regression-tested in xUnit.
**Fix shape:** Either a `conductor.testing.net` NuGet, or a `polyphony harness validate --scenario-dir ...` CLI that .NET tests can shell out to.

### Liszt + Mahler — Script-node contract underspecification
**Gap:** No documented contract for stdin / stdout / cwd / env-var inheritance / process-group behavior on script nodes. `Poll-PrStateDelta.ps1` invented conventions (single JSON object at end, progress to stderr, no stdin reads) that aren't specified anywhere. Future script authors will rediscover these by breaking them.
**Fix:** Documentation + reference script template. Cheap.

---

## 4. Fork verdict — three agents asked, three said NO

All three agents asked about fork-or-contribute said the same thing in different words:

> **Beethoven (mission):** "Not yet, and not the right move… The risk is not absence of a path — it's contribution delay."
>
> **Bach (seam):** "The architecture will hold; the debt accumulates directionally until conductor delivers `on_error:` for script nodes, dynamic sub-workflow dispatch, cross-run artifact persistence, and run_id template exposure."
>
> **Mahler (engine):** "The biggest single dependency is PR #229 merging upstream. Until then, nothing ships."

**The shared framing:** Every gap above has a known upstream path. None require redesign. The risk is one of *delivery latency*, not architectural compatibility. If contribution stays slow, the workarounds calcify (routing-style envelopes already in ~50 verbs; `compose_addendum` in 3 workflows; aggregator scripts in the renegotiation critical path), and retiring them becomes active engineering work — not automatic cleanup.

**Posture recommendation:** Push for upstream merges and contribute the RFC for `retry:`. Do not fork.

---

## 5. Recommended Next Bites (sized)

### Small — this week
| Item | Owner | Why |
|------|-------|-----|
| Push PR #229 review — resolve context.py sentinel conflict with upstream conductor team | Daniel + Mahler | Blocking everything Phase 1 and Phase 2a |
| Cherry-pick conductor PR #213 (`type: notification` → `type: emit`) | Liszt/Wagner | 4 TODO comments waiting; semantic-debt cleanup |
| Add `meta.min_conductor_version` field to workflow YAMLs | Wagner | Bach's ask; makes conductor dependencies explicit |
| Mozart: `[VerbResult(typeof(X))]` backfill (~4h) | Mozart | Unblocks Jinja path lint (Gap B); high impact for low cost |
| Document `script:` node contract (stdin / stdout / cwd / env / process-group) | Liszt | Cheap; prevents future script authors rediscovering the conventions by breaking them |

### Medium — next sprint
| Item | Owner | Why |
|------|-------|-----|
| Wagner: Phase 1 retrofit (5 pure-abort gates) once PR #229 merges | Wagner | First on_error win; ~150 lines deleted |
| Liszt: thread CONDUCTOR_OUTPUT contract through the harness for error simulation | Liszt + Brahms | Unblocks error-path testability (Gap A from Brahms's seat) |
| Polyphony Jinja path lint (ADR #175 companion) after Mozart's backfill | Wagner | Catches Gap B at edit-time, not 30-min dogfood time |
| `conductor.agent_prompt_tokens` emission RFC | Stravinsky → upstream | Cheap conductor change; observability for mid-turn truncation |

### Large — RFC-level, multi-week
| Item | Owner | Why |
|------|-------|-----|
| **RFC Phase 2 `retry:` route action** — design + ship | Mahler → upstream | THE highest-leverage thing in the whole report. Without it, Phase 2b's 14 gates stay broken. |
| Sub-workflow error envelope propagation (Phase 2 in `workflow.py:1207`) | Mahler → upstream | Eliminates 100-line `aggregate_renegotiation` PowerShell |
| Conductor `type: store` / `state:` primitive | Liszt → upstream | Eliminates the temp-file watermark hack; closes Gap from Liszt seat |
| Conductor `skills:` + `prompt_addendum:` schema fields | Stravinsky/Beethoven → upstream | Retires `compose_addendum` verb; closes north-star §4.1 violation |
| Dynamic `workflow:` path templating + `workflow_variants:` annotation | Bach/Mahler → upstream | Collapses `root-item-dispatch.yaml` from N branch-on-router to 1 templated dispatch |

---

## 6. Open question — "Install holographic for memory"

**I could not identify what you meant.** Here's what I searched and what I found:

| Probe | Result |
|---|---|
| `where.exe holographic` | Not on PATH |
| `~/.copilot/skills/` | 18 skills present — none named holographic |
| `~/.copilot/agents/` | 11 agents present (Council + code-style + tone-of-voice + speckit) — no holographic |
| `~/.copilot/mcp-config.json` | playwright, enghub, twig-mcp, workiq — no holographic |
| Grep `holographic|holo` in `~/.copilot/*.{json,md}` | Zero matches |
| `~/projects/*holograph*` | No matching directory |
| `npm search holographic` | `holographic@0.0.1` (onkar-giram, "Reserved for future development") + `holographic-sticker` (React component) |
| GitHub search | Research/hobby projects only — NeoVertex1/nuggets ("First AI assistant with holographic memory"), MatthewAKelly/HDM, academic repos |

**Most likely interpretations — please disambiguate:**
1. An MCP server for memory that you have in mind but isn't yet in your `mcp-config.json`?
2. The Copilot CLI's built-in `store_memory` tool used more aggressively for this session's facts?
3. A personal skill you intend to install at `~/.copilot/skills/holographic/`?
4. The Polyphony squad's `.squad/skills/` (currently has `conductor-primitive-vetting`, `dogfood-conductor`, `pr-gate-compression`, `watermark-poll-pattern` — no holographic)?
5. A different word entirely (voice-to-text artifact)?

**What I did instead:** Captured the 7-agent fan-out in this report and dropped per-agent learnings into each agent's `.squad/agents/{name}/history.md`. If you tell me what holographic resolves to, I'll install/configure it on your return.

---

## 7. Decision Asks (one screen, each with default)

| # | Ask | Default | Notes |
|---|-----|---------|-------|
| 1 | Frame future on_error PRs as "Phase 2a" (not "Phase 2 complete") since Phase 2b's `retry:` action isn't designed | **yes** (per Mahler) | Avoid signaling false completion |
| 2 | Track "conductor feature blockers" as polyphony backlog items + add `meta.min_conductor_version` to workflow YAMLs | **yes** (per Bach) | Makes upstream dependencies operator-visible |
| 3 | Greenlight Mahler + Wagner to **draft the RFC Phase 2 `retry:` design** and contribute upstream | **yes** | Highest single leverage item in the report |
| 4 | Greenlight Mozart's `[VerbResult(typeof(X))]` backfill (~4h) | **yes** | Unblocks Gap B mitigation |
| 5 | Cherry-pick conductor PR #213 (`type: emit` rename) now or wait for the context.py resolution? | **now** | 4 TODOs waiting; cleanup-only |
| 6 | What did "install holographic" mean? | (no default — needs you) | See §6 |

---

## 8. Where to dig deeper

Per-agent full reports (all in `.squad/handoffs/`):

| Agent | Path | Bytes | Headline |
|-------|------|------:|----------|
| Mahler | `mahler-conductor-gaps-20260531.md` | 26,982 | on_error feasibility + retry-route gap |
| Stravinsky | `stravinsky-conductor-gaps-20260531.md` | 28,736 | Agent-prompt contract; output schema enforcement |
| Brahms | `brahms-conductor-gaps-20260531.md` | 25,314 | Testability: 5 critical seams blocking high confidence |
| Wagner-1 | `wagner-conductor-gaps-20260531.md` | 23,327 | Workflow-author seat; re-entry anchor gap |
| Liszt | `liszt-conductor-gaps-20260531.md` | 22,051 | Script author; between-invocation state primitive |
| Bach | `bach-conductor-gaps-20260531.md` | 20,218 | Architectural seam; 4 stress fractures all pointing same way |
| Beethoven | `beethoven-conductor-gaps-20260531.md` | 19,806 | Mission view; fork-vs-contribute verdict |

**Total source material:** ~166 KB. This report is the synthesis.

---

## 9. Coordinator's note

The convergence on **`on_error:` + `retry:`** across six independent seats is the strongest cross-agent signal I've seen in any fan-out so far. When the workflow author, the script author, the engine expert, the architect, the mission keeper, and the testability designer all independently name the same gap as their #1, that's a near-perfect alignment signal. The case for prioritizing the conductor `retry:` RFC contribution is as strong as squad analysis can make it.

Mahler's nuance — that PR #229 itself is only Phase 1, and the actual UX win needs Phase 2b's not-yet-designed `retry:` action — is the most important piece of new information in the fan-out. Without it, we'd risk shipping Phase 1 and calling the cleanup "done" while 14 of 19 idempotent-retry gates remained silently broken.

**One non-engineering observation:** the gate-philosophy thread runs through every agent's analysis (not just Beethoven's). Bach frames it as "seam violation"; Wagner-1 as "invisible global contract"; Mahler as "polyphony absorbing engine concerns"; Brahms as "untestable error paths." Different vocabulary, same underlying complaint. Polyphony's identity ("type-agnostic SDLC routing domain") is structurally sound — and structurally pressured by the conductor gaps. The architecture holds today; it will not hold indefinitely without upstream movement.

— Squad Coordinator
2026-05-31
