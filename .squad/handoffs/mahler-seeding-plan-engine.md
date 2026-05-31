# Conductor Engine Stance: Seeding Plan & Restart-Friendly State

**Author:** Mahler (Conductor Engine Specialist)  
**Date:** 2026-05-29T09:33:07-07:00  
**Context:** Q1 ask — durable seeding plan, retry cap, restart-friendly re-entry  

---

## Q1: Does conductor need to know about the seeding plan?

**No. This stays entirely in script + YAML territory.**

A `type: script` node reads `.polyphony/state/{rootId}/seeding-plan.json`, compares it to ADO via `polyphony state`, and routes based on what it finds. Conductor needs no new primitives for this. The engine is already the right shape: deterministic routing based on observable outputs. The seeding plan file IS an agent output — it just lives on disk rather than being piped through `conductor.output`. No engine changes required.

If Bach proposes a conductor-native "artifact registry" or "shared state store," I will push back: that's over-engineering a filesystem problem. The filesystem is already the right persistence layer here.

---

## Q2: Persistent per-root-item state — what does conductor actually provide?

**Nothing useful for this.** I checked `checkpoint.py` directly.

Conductor's `CheckpointManager` stores to `$TMPDIR/conductor/checkpoints/`, keyed to **workflow name + timestamp**, not root item ID. It's crash recovery for a single failed run, not durable cross-run state. It is gone on reboot. There is no built-in mechanism for "this artifact survives across separate runs of the same root."

**Filesystem IS the entire answer.** The seeding plan belongs at `.polyphony/state/{rootId}/seeding-plan.json` (within the repo/worktree), written atomically by the seeding script before any children are created. That path survives process restarts, reboots, and re-runs. The engine doesn't need to know it exists.

---

## Q3: Re-entry by state discovery — what's the right observable artifact?

**Seeding plan file + ADO state together.** Not just the file.

Per P3 (Re-Entry by State Discovery): a workflow that restarts must be able to distinguish "no prior attempt" from "partially complete" from "fully complete." The seeding plan file alone can't tell you whether the children it describes actually exist in ADO. The correct re-entry check is:

1. Does `.polyphony/state/{rootId}/seeding-plan.json` exist? If not → fresh seed path.
2. Does it exist? → Compare planned items to `polyphony state children` (ADO). If match → already seeded, skip. If mismatch → retry seeding for the missing items.

The seeding plan file is the source of intent. ADO is the source of truth for what actually landed. Both are needed. A plan file with no ADO children is corrupt/partial — that's exactly the "don't let corrupt state just move forward" case.

---

## Q4: 3-retry-on-auth/network — engine or script?

**Script must count its own retries. The proposed syntax does not exist.**

The directive mentions something like `on_error: { exit_code: 3, max_attempts: 3, on_exhaust: stop }`. That is not in the conductor schema. `max_attempts` (via `RetryPolicy`) exists only on **provider-backed agent nodes**, not `type: script` nodes (`schema.py` line 449, 843: "Only applies to provider-backed agents (not script or human_gate)"). There is no `on_exhaust` field anywhere in the schema.

What DOES exist: `on_error: "kind"` on routes leaving a script node, matching error kinds emitted via the `ConductorError` helper (exit code → kind mapping). The idiomatic pattern for 3-retry-on-network is:

- Script handles retries internally (fast loop, 3 attempts, then exits with `seeding.network_error` kind on exhaustion).
- YAML then routes on `on_error: "seeding.network_error"` to a `seeding_blocked` terminal.

The script-internal approach is cleaner here because network/auth retries are fast and transient — there's no benefit to surfacing each attempt to the YAML router. The YAML only needs to see "exhausted" vs "success."

**Bottom line:** script counts retries, emits a kind on exhaustion, YAML routes to terminal. That's idiomatic and fully supported today.

---

## Q5: "Stop on exhausted retries" — `seeding_blocked`, `workflow_abandoned`, or domain signal?

**New terminal: `seeding_blocked`. Not `workflow_abandoned`.**

Beethoven's analysis (`.squad/handoffs/beethoven-workflow-abandoned-triggers.md`) is clear: `workflow_abandoned` means **operator gave up** — every route into it requires a human click. "Exhausted network retries" is not an operator decision; it's an infrastructure failure. Routing it to `workflow_abandoned` would corrupt the semantics Beethoven correctly identified.

`seeding_blocked` is the right answer:
- Signals a specific, non-volitional failure mode (infra failed, not operator gave up).
- The batch aggregator can distinguish it from volitional abandonment.
- An operator can decide to retry the root item later without clicking through an abandon gate.
- Keeps `workflow_abandoned` semantically pure.

If Bach proposes a generic "blocked" terminal reused for all error states, push back: specificity here is cheap and the observability gain is real.

---

## Q6: The corrupt-state guard — engine or workflow?

**Workflow's job entirely. Engine role: zero.**

The engine has no concept of "seeding reconciliation passes." The corrupt-state guard is a predicate computed by the seeding script: does the plan file match ADO? If not, don't emit a success route. Full stop.

The workflow enforces this by refusing to leave the seeding stage until the script outputs `reconciliation_passed: true`. The YAML routing is the gate. The engine already supports this — a script that never emits a success condition keeps the workflow in the seeding stage (or routes to `seeding_blocked` on exhaustion). No engine changes needed.

The one risk: if the seeding script writes a partial plan file and then crashes before atomically completing it, re-entry will see a partial artifact. The guard against this is **atomic plan file writes** (write to `.tmp`, rename to final path) — a script-level concern, not engine. Recommend we document this as a requirement on the seeding script implementation.

---

*Filed: 2026-05-29T09:33:07-07:00*
