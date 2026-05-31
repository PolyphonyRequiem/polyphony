# Beethoven: Mission-Bending Conductor Gaps — 2026-05-31

**Author:** Beethoven, Mission Keeper  
**Audience:** Daniel Green (offline ~1 hour)  
**Date:** 2026-05-31T10:51:14-07:00  
**Context:** Top conductor gaps framed through the "human-assisted automated SDLC" mission lens — where conductor's shape is bending polyphony *away* from its north star.

---

## Frame

Polyphony's north star (§4.1, locked) is unambiguous: "when polyphony needs a capability conductor lacks, we **grow conductor**, we do not absorb its domain." The gate philosophy (§5.2, locked) is equally clear: human gates exist at **inflection, surrender, or attestation moments only** — never as logistics scaffolding for deterministic routing.

The gaps below are ranked by how badly they violate these two invariants. Each gap either (a) forces human gates where no judgment is required, or (b) pulls orchestration engine behavior into polyphony-side code that the engine should own.

---

## Gap 1 — No Native Error Routing (`on_error:`)

**Impact rank: 5/5**

### What conductor lacks

Conductor has no `on_error:` routing primitive at the step level. When a script step exits non-zero, conductor halts the workflow run. There is no way to route *within* a workflow to a different node when a step fails — the workflow author cannot say "if this step fails, go to this recovery node instead."

### How polyphony works around it today

Polyphony wraps every potentially-failing verb in the routing-style envelope pattern: every script step always exits 0 and reports success/failure via JSON envelope fields (`output.error`, `output.error_code`). Workflow YAML then routes on those envelope fields to human gate nodes. This produces the 19 `*_error_gate` human gates catalogued in the AB#3257 inventory: `poll_error_gate` (plan-level.yaml, github-pr.yaml, ado-pr.yaml), `workflow_error_gate` (actionable.yaml), and equivalents in implement-merge-group.yaml and restack-remedy.yaml. The TODO comments in github-pr.yaml:1087 and ado-pr.yaml:1196 make the gap explicit: *"add on_error: to: poll_error_gate once conductor RFC Phase 2 ships."*

### Mission cost

**Forces MORE human gates than the mission wants.** Every one of these 19 gates presents an operator with a binary (Retry / Abort) for an infrastructure failure — a network timeout, a transient API error, a script invocation failure. There is zero judgment required. The mission says human gates exist where *direction-setting authority, decision authority, or attestation* is needed. Routing around a transient network error is none of those things. These gates are burning real wall-clock latency (§5.2: "every surface costs wall-clock latency, regardless of how cheaply the operator answers once they see it") on decisions that are deterministic. The trust-ramp trajectory (§5.3) cannot advance while operators are being interrupted by logistics.

Compounding the cost: because routing-style envelopes must be added to every verb to enable this workaround, polyphony has built a parallel error-signaling discipline (routing-style exits) that is now deeply embedded in the CLI contract. If conductor later ships `on_error:`, removing the routing-style envelope discipline will require touching all ~50 verb implementations and all workflow YAML call sites simultaneously. The workaround is calcifying.

### What "fixed" looks like in mission terms

With native `on_error:` routing, all 19 error gates collapse to `on_error: to: [auto_retry(n) | abort_run]` declarations on the individual steps. Infrastructure failures retry silently (or abort silently on exhaustion). Human attention is reserved for failures that genuinely require judgment — which, in the cases above, means *none* of them. The operator's gate queue shrinks by ~19 noise events per run. The trust ramp becomes credible: "fewer surfaces → shorter runs" finally applies to infrastructure failures, not just policy-configured surfaces. The routing-style envelope discipline becomes optional hygiene rather than mandatory survival.

---

## Gap 2 — No Structured Agent Context Injection (Skills / MCPs / Prompt Addendum)

**Impact rank: 4/5**

### What conductor lacks

Conductor's agent step schema supports a static `tools: list[str]` field but has NO `skills:`, `mcps:`, or `prompt_addendum:` fields. The runtime cannot dynamically bind contextual capabilities — skills, MCP servers, or per-item guidance — to an agent invocation based on workflow inputs or computed context. This is confirmed explicitly in actionable.yaml:52-57: *"Conductor-schema note: agent steps in conductor have a `tools:` list field but NO `skills:` / `mcps:` / `prompt_addendum:` fields."*

### How polyphony works around it today

Polyphony built a full `compose_addendum` pipeline step: a dedicated CLI verb (`polyphony agent compose-addendum`) that reads the work item's facet set, resolves the bound skills and MCP servers from `process-config.yaml`, and packages all per-item guidance into a text block. The `actionable_agent` prompt template then injects this block as plain prompt text via Jinja2 conditionals. The same pattern is repeated in `guidance_loader` (a separate step that loads repo-specific guidance files). Every agent that needs dynamic context requires an upstream script step to compute it and a Jinja2 template to inject it as raw prompt text. This is implemented across actionable.yaml (compose_addendum + guidance_loader → actionable_agent, evidence_reviewer), plan-level.yaml (guidance_loader), and implement-merge-group.yaml.

### Mission cost

**Forces polyphony to absorb engine orchestration work.** The north star §4.1 is explicit: "No orchestration runtime inside polyphony." `compose_addendum` is orchestration behavior — it decides which capabilities bind to this invocation, in this context, for this work item. That is a first-class engine responsibility. Polyphony is carrying it because conductor cannot.

The secondary cost is correctness: dynamic skills/MCPs are "advisory context the agent reads from its prompt" (actionable.yaml:315) — they're not actually connected at the conductor runtime level. The agent can read a skill recommendation from its prompt and still not have the tool available. This creates a gap between what the orchestration layer *promises* the agent and what the runtime *delivers*. In mission terms, the bounded nature of agent invocations (§3: "bounded where it can't be") is undermined — the scope-bounding mechanism is partially advisory, not enforced.

### What "fixed" looks like in mission terms

With native `skills:` and `prompt_addendum:` fields on conductor agent steps (or an equivalent injection contract), polyphony's `compose_addendum` verb becomes unnecessary. The YAML declares the binding directly. Conductors wires the actual MCP connections and passes the structured addendum to the agent. The distinction between "what the YAML says the agent should use" and "what the runtime makes available" closes. Polyphony's north-star seam (§4.1: "polyphony authors, conductor executes") is honored. The `compose_addendum` verb can retire. Agent invocations become genuinely bounded — not just advisory.

---

## Gap 3 — No First-Class Observable Events (`type: emit`)

**Impact rank: 4/5**

### What conductor lacks

Conductor does not have a first-class `type: emit` step kind — a YAML step that emits a structured domain event as an observable signal without blocking or routing to a human. There is a `type: notification` approximation, but it is not semantically equivalent to a non-blocking, typed domain signal. The workflows use `type: notification` as a stopgap, with four `TODO(post-upstream-merge): rename type: notification → type: emit` comments in github-pr.yaml and ado-pr.yaml. Conductor PR #213 apparently delivers the fix, but the cherry-pick has not landed.

### How polyphony works around it today

The domain-signal pattern (emitting `pr_review_required`, `pr_ci_attention_required`, `notify_pr_review_resolved`) is implemented via `type: notification` steps. The semantic mismatch is managed by convention (the payload schema carries a `disposition` field; downstream platespinner reads it). The `notify_pr_pending` → `poll_pr_state_delta` flow in both github-pr.yaml and ado-pr.yaml uses this pattern to notify the operator asynchronously while the workflow continues polling — a pattern that exists specifically because conductor lacks a non-blocking signal primitive.

### Mission cost

**Forces LESS automation than the mission promises and undermines the trust ramp.** The north star §5.6 (observability, still open) explicitly depends on non-blocking signal emission: "awareness *between* surfaced moments." Without clean `type: emit`, polyphony cannot build §5.6 trustworthy observability — the domain-signal layer that tells the operator what is happening between human gates. When the operator cannot see what the run is doing between gates, the trust ramp cannot advance (§5.3: "Trust grows via observation. Better observability between surfaces → faster, better-informed trust ramp"). A stuck trust ramp means more human gates, not fewer. The chain is: conductor lacks `type: emit` → observability is a workaround → operator can't see between gates → trust doesn't grow → operator can't safely move surfaces from `manual` to `warning` or `auto`. That is a direct mission regression.

Additionally, the mislabeled `type: notification` steps create a semantic debt: polyphony's domain-event vocabulary is expressed via a conductor type that doesn't mean what polyphony needs it to mean. When the upstream conductor PR eventually lands, polyphony will need to rename all four sites simultaneously — a simple mechanical change that carries refactor risk if the semantic difference has been papered over long enough that authors forget the distinction.

### What "fixed" looks like in mission terms

With `type: emit` shipping, domain signals become a first-class workflow primitive. The platespinner integration (non-blocking CTA emission, resolved-disposition updates) is expressed in the language the engine understands, not via a notification-type approximation. The `pr_review_required` / `pr_ci_attention_required` signals become auditable, schema-validated events. The observability layer (§5.6) can be built on a stable foundation rather than a semantic workaround. The trust ramp has genuine signal to grow on.

The fix is already upstream — conductor PR #213 cherry-pick is the action item, not a new feature request.

---

## Gap 4 — No Workflow Checkpoint / Durable Step State

**Impact rank: 3/5**

### What conductor lacks

Conductor does not checkpoint step results mid-workflow. If the conductor process is killed — or if a resume is triggered after a failure — the workflow restarts from the entry point. Every step that ran to completion in the prior run re-runs. There is no mechanism to say "step X completed successfully with output Y in run Z; skip it on re-entry."

### How polyphony works around it today

Polyphony's entire `P3 (re-entry)` design exists to compensate for this gap. Every CLI verb is idempotent: `branch ensure-evidence-branch` reuses an existing branch; `pr open-impl-pr` reuses an existing PR; `polyphony worklist build` re-derives the worklist from ADO + git observable state. The north star §5.5 describes this explicitly: "Observable-state re-entry rebuilds the worklist." The `polyphony.yaml` Q4 note confirms: "polyphony writes NO checkpoint state of its own; the source of truth is the work-item tree." The `intent: resume` codepath re-runs every preflight step (lock check, edges check, worklist build) on every re-entry, even for a run that was killed 90% of the way through.

The `plan-level.yaml` re-entry surface is the clearest cost example: a workflow interrupted mid-run between `architect` and `pr_poster` will re-run the architect entirely on resume (the plan the architect already produced is on the branch, but conductor doesn't know this — it just sees "architect not yet completed in this process"). The architect agent re-reads the work item, regenerates the plan, and the idempotency of the downstream steps (push-to-branch is a no-op if SHA matches) makes this safe but wasteful.

### Mission cost

**Forces MORE automation cost than the mission promises and makes long runs fragile.** The north star §5.4 (time scale) declares polyphony must serve "large" runs (days, epic-scale work). At that scale, re-running an architect or reviewer agent on every conductor restart is a material latency cost. Each re-run burns LLM tokens, takes minutes, and produces output polyphony then has to check for idempotency against an artifact it already made. The mission says "polyphony overhead is negligible relative to agent work and operator response latency" — but at scale, re-running completed agent steps on every resume is not negligible. It is also a trust problem: an operator who resumes a paused run expects to pick up from where the run was. Watching the architect re-run when the plan PR is already open erodes trust in the engine.

The P3 idempotency investment is impressive and genuinely mitigates much of this. But it is engine-behavior implemented in the CLI layer — polyphony absorbed conductor's durability problem into every single verb and every re-entry codepath.

### What "fixed" looks like in mission terms

With conductor-level step checkpointing, a killed workflow resumes from the first non-completed step, not from the entry point. Agent steps that completed emit their output once; re-entry reads the stored output without re-invoking the LLM. Polyphony's P3 idempotency discipline can thin out significantly: verbs still benefit from idempotency for correctness, but the urgent need to design every single step around "this will re-run from scratch on resume" goes away. Large runs become genuinely restartable without silent LLM rework. The operator's trust that "resume means continue, not restart" is backed by the engine, not by a discipline that any future verb author must remember to follow.

---

## Gap 5 — No Structured Sub-Workflow Output Aggregation

**Impact rank: 3/5**

### What conductor lacks

Conductor's `for_each` construct does not provide structured aggregation of sub-workflow outputs. When a `for_each` over a list of items invokes a sub-workflow per item, the caller cannot natively aggregate results into a typed array or structured object. The caller sees raw error lists and per-item output blobs, but has no built-in way to express "collect all `renegotiation_request` fields from all dispatched sub-workflows into a summary."

### How polyphony works around it today

The three-tier dispatch architecture (polyphony.yaml → root-batch-dispatch.yaml → root-item-dispatch.yaml) is a direct response to this gap. `aggregate_renegotiation` in root-batch-dispatch.yaml (lines 300–417) is a PowerShell script that manually walks conductor's `for_each` error shape, parses per-item outputs, and constructs a summary renegotiation array. Mahler's concern #2 from the initial review names this exactly: "Route conditions are doing work that scripts should own... The aggregator script in root-batch-dispatch is manually parsing conductor's `for_each` error shape to emit `items_failed_count` and `failed_items`. This parsing logic should be a route condition on root-batch-dispatch, not embedded in PowerShell." The renegotiation aggregation output travels across workflow boundaries as a JSON-encoded string (not a structured array) because conductor has no native aggregation type.

### Mission cost

**Forces engine orchestration work into agent scripts.** This is the same class of violation as Gap 2: polyphony is absorbing orchestration domain behavior. `aggregate_renegotiation` and `outer_loop_evaluator` contain multi-batch aggregation logic that is, architecturally, the engine's job. The immediate human-gate impact is indirect: the aggregation script is a point of failure (if parsing breaks, the renegotiation bubble-up silently drops). A silent drop in renegotiation signaling could mean the operator never sees a child plan that needs revision — a missed surrender gate. That is a mission failure: polyphony would fail to surface a moment that the gate philosophy (§5.2) says must be surfaced.

### What "fixed" looks like in mission terms

With native `for_each` output aggregation, the aggregator scripts are eliminated. Polyphony's dispatch topology simplifies from three files to two (or the batch-level workflow becomes a thin routing shim with no custom aggregation logic). The renegotiation summary travels as a native conductor array, not a JSON-string-inside-a-string. The risk of silent aggregation failure goes away. More importantly, the boundary §4.1 establishes ("polyphony authors, conductor executes") is cleanly honored: dispatch logic belongs in the YAML, aggregation belongs in the engine.

---

## Bonus: Should We Fork Conductor?

No — but we are approaching the moment where contributing upstream becomes non-optional.

The north star §4.1 committed to conductor as the execution engine and established "grow conductor first" as the default response to any capability gap. That commitment was made with clear eyes. The five gaps above are real, and all five have active or pending upstream fixes: on_error is tracked via AB#3257 and the RFC; `type: emit` is conductor PR #213 (cherry-pick blocked, not design-blocked); agent context injection is a schema extension, not a runtime redesign. Forking would fragment polyphony's YAML contract, lose the benefit of conductor's own investment trajectory, and add a maintenance surface polyphony is not resourced to own.

The risk is a different shape: **contribution delay, not absence of path**. With each passing week that these gaps stay open, polyphony builds deeper workarounds — the routing-style envelope discipline is now in ~50 verb implementations; the `compose_addendum` verb is now in three workflows; the aggregator scripts are now in the critical renegotiation path. These workarounds are not going to be removed just because conductor ships the native capability; someone will have to actively retire them. The debt compounds. The right posture is to accelerate upstream contribution on Gaps 1, 2, and 3 — not as a favor to conductor, but as debt-prevention for polyphony. If AB#3257 and the `type: emit` cherry-pick have been waiting weeks, that is the gap to close today.

---

## Summary Table

| # | Gap | Workaround today | Mission cost | Impact |
|---|-----|------------------|--------------|--------|
| 1 | No `on_error:` routing | 19 human error gates + routing-style envelopes on all verbs | Humans doing deterministic retry decisions | **5/5** |
| 2 | No agent context injection (skills/MCPs/addendum) | `compose_addendum` verb + guidance_loader as pre-agent steps | Polyphony absorbs orchestration work; bounded scope is advisory | **4/5** |
| 3 | No `type: emit` | `type: notification` stopgap; 4 rename TODOs open | Observability (§5.6) is a workaround; trust ramp stalls | **4/5** |
| 4 | No workflow checkpoint / durable step state | P3 idempotency discipline on every verb; full worklist rebuild on every resume | Long-run restarts re-run completed agent steps | **3/5** |
| 5 | No `for_each` output aggregation | Aggregator PowerShell scripts (aggregate_renegotiation, outer_loop_evaluator) | Engine work in scripts; silent aggregation failure = missed surrender gate | **3/5** |

---

*End of report. Plain-text summary follows below.*
