# ADR: Polyphony Verb Error Boundary

**Status:** Accepted  
**Date:** 2026-05-28  
**Authors:** Bach (Architect)  
**Implements:** GitHub issue #536 (verb error emission retrofit)  
**Implementation owner:** Mozart (.NET/C#)

---

## Context

Polyphony CLI verbs currently exit 0 regardless of execution outcome and write
structured JSON to stdout. On domain failure (e.g. work item not found, PR
already closed) verbs emit `{"error": "..."}` to stdout — still exit 0.

Conductor's upcoming `on_error:` routing (PR #229) routes workflow execution
based on the exit code of the previous step and on the presence of a typed
error envelope written to `CONDUCTOR_ERROR_OUT`. For `on_error:` to catch
polyphony-verb failures, verbs must emit non-zero exits for the failure classes
conductor needs to route on. Without this change, every polyphony verb failure
passes silently through `on_error:` as if it were a success.

This ADR defines the **policy** for when polyphony verbs emit non-zero exits
and what they write to `CONDUCTOR_ERROR_OUT`. Mozart owns the per-verb
implementation and the exit-code catalogue.

### Constraints

- Polyphony workflows today check stdout JSON for error conditions. Any regime
  change must not silently break workflows that currently work.
- Conductor's `on_error:` routing requires **both** a non-zero exit AND a
  typed envelope on `CONDUCTOR_ERROR_OUT` to give downstream routes meaningful
  context.
- Domain outcomes (expected failure states that a workflow should handle
  programmatically — e.g. "this PR is already merged, skip step") must NOT
  become non-zero exits; they are valid workflow signals, not infrastructure
  failures.

---

## Decision

**Option C — Hybrid exit codes:** polyphony verbs exit **0 for all domain
outcomes** (success and expected/named failure states) and exit **non-zero for
infrastructure failures only**.

| Class | Exit code | CONDUCTOR_ERROR_OUT |
|---|---|---|
| Domain success | 0 | not written |
| Domain failure (expected, named) | 0 | not written; failure signalled via stdout JSON `error` field |
| Infrastructure failure (see catalogue) | non-zero (typed) | typed error envelope |

### Exit Code Catalogue (Mozart owns — this table is the policy seed)

| Code | Meaning |
|---|---|
| 0 | Domain outcome (success or named domain failure) |
| 1 | Generic unclassified infrastructure failure |
| 2 | Configuration error (missing or invalid `.polyphony-config`) |
| 3 | Twig CLI unavailable or unresponsive |
| 4 | Git operation failure (not a domain outcome — infrastructure I/O) |
| 5 | ADO / PR platform unreachable (not a domain outcome — infrastructure I/O) |
| 6 | Polyphony-level timeout (verb-level, distinct from conductor-level `on_error: timeout`) |

Mozart may extend this table when retrofitting individual verbs. The catalogue
lives in `docs/polyphony-architecture.md` under a new § Exit Code Catalogue
section. Mozart adds entries there; this ADR defines the taxonomy only.

### What Verbs Write on Infrastructure Failure

On a non-zero exit, a verb MUST also write a typed error envelope to
`CONDUCTOR_ERROR_OUT`. Minimum required fields:

```json
{
  "kind": "twig_unavailable",
  "message": "twig CLI did not respond within the expected timeout",
  "details": {
    "exit_code": 3,
    "command": "twig state Active --id AB#4567"
  }
}
```

The `kind` field is a dotted string matching the verb's failure class (e.g.
`twig_unavailable`, `git_push_rejected`, `config_missing`). Mozart defines the
kind vocabulary per verb.

### What Verbs Write on Domain Outcome (Unchanged)

All domain outcomes — success and named domain failures — continue to produce
structured JSON on stdout and exit 0. This preserves backward compatibility
with all existing workflows that check stdout for `error` fields.

```json
{"status": "merged", "pr_number": 42}                         // success
{"error": "pr_not_found", "pr_number": 999, "retryable": false}  // domain failure
```

---

## Alternatives Considered

**Option A — Exit 0 always (current behavior):** Rejected. Conductor's
`on_error:` routing cannot detect polyphony verb failures. The 14 error gates
removed in #535 cannot be replaced with automated `on_error:` chains unless
verbs signal failures.

**Option B — Non-zero for any non-success:** Rejected. Too coarse. Domain
failures (PR already merged, work item in terminal state) are expected workflow
signals that should be routed programmatically, not surfaced as infrastructure
errors. Conflating them forces every workflow author to handle "PR already
merged" as if it were a network failure.

**Option C (chosen) — Hybrid:** Preserves backward compatibility (domain
outcomes keep exit 0), enables `on_error:` routing for infrastructure failures
that polyphony cannot recover from automatically.

---

## Consequences

### Positive
- Conductor `on_error:` can now catch and route infrastructure failures
  without human gate intervention
- Verbs remain deterministic about what is a "domain outcome" vs an
  "infrastructure failure" — the distinction is explicit and testable
- Existing workflows are not broken: exit-0 domain outcomes still produce the
  same stdout JSON they always did

### Negative
- Mozart must audit ~50 verb implementations and classify each failure path
  as domain-outcome vs infrastructure-failure — this is a non-trivial retrofit
- Any verb that currently exits 0 on an infrastructure failure (e.g. swallows
  a twig timeout and returns `{"error": "..."}`) needs to be reclassified;
  this may surface latent issues

### Neutral
- The `CONDUCTOR_ERROR_OUT` mechanism is conductor-specific. Polyphony verbs
  that are invoked outside conductor (e.g. in tests, in scripts) will still
  emit non-zero on infrastructure failure — callers should check the exit code
  and optionally read `CONDUCTOR_ERROR_OUT` if set

---

## Invariants

1. **Exit 0 NEVER means "nothing happened."** A verb that exits 0 MUST produce
   structured JSON on stdout describing the outcome.
2. **Domain failures are domain outcomes.** Expected, named failure states
   (e.g. work item already in terminal state, PR already merged) are exit 0
   with a JSON `error` field — NOT non-zero exits.
3. **Infrastructure failures exit non-zero AND write `CONDUCTOR_ERROR_OUT`.**
   A non-zero exit without a typed envelope on `CONDUCTOR_ERROR_OUT` is a
   polyphony implementation defect.
4. **Mozart owns the exit-code catalogue.** This ADR establishes the taxonomy;
   per-verb assignments live in `docs/polyphony-architecture.md`.
