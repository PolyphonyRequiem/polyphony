# Mozart — Seeding Plan Error Circuit
**Date:** 2026-05-29  
**Author:** Mozart (Polyphony Verb / Tooling Specialist)

---

## ⚠️ First: The Exit Code Map in the Briefing is Wrong

The squad briefing says my catalogue is "2 bad args, 3 auth, 4 not-found, 5 network." The actual ADR says: 2 = config error, 3 = twig CLI unavailable, 4 = git operation failure, 5 = ADO platform unreachable. No auth-specific code exists. No not-found code exists. If the rest of the team is designing against the briefing summary, we're building on sand. Fix the team brief before we wire anything.

---

## Q1: Map the Directive to Exit Codes

"Auth" failures (ADO 401/403 via twig) surface as **code 5** (ADO platform unreachable) — that's the right home because polyphony doesn't talk to ADO directly, only through twig. There is no separate auth code and I don't think we need one: auth failure and network failure have the same retry posture.

"404 for a child we tried to create" is almost certainly a **domain outcome (exit 0)**. If the parent work item doesn't exist, retrying won't fix it — that's a permanent planning error. The seeding plan itself is wrong. Stop and surface it in stdout JSON.

**Unclassified code 1 (crash): do not retry.** We can't know if re-running is safe. Treat code 1 as permanent, stop immediately, page a human. Retrying on unknown failure is how you corrupt state.

---

## Q2: Who Owns the Retry Counter

The **workflow node** via `on_error: { exit_code: [3, 5], max_attempts: 3 }`. Verbs are stateless; they just emit exit codes and structured envelopes. Retry orchestration belongs in conductor. This is consistent with every other verb pattern we have.

The wrinkle: "restart friendly" means the counter needs to survive a conductor crash. My recommendation is that conductor persists retry state (its job), but each failed attempt writes its attempt number into `CONDUCTOR_ERROR_OUT` so the orchestration layer can reconstruct it on restart.

---

## Q3: Idempotency Contract

`polyphony seed` is almost certainly **not idempotent today**. The name implies one-time initialization. If it partially completes and we re-run, there's no guarantee twig returns a clean "already exists, skipped" rather than an error.

The fix: the **seeding plan document** (written to the work root, durable per work root ID) is the enabler of idempotency. Re-running must compare plan to current ADO state and only act on gaps. Until that read-compare-act loop exists, we're retrying blind. The verb should be renamed — `apply-seeding-plan` is precise; `reconcile-seed` is acceptable. `seed` alone is not.

---

## Q4: The Reconciliation Verb

Two verbs, not one:
1. **`polyphony plan-seed`** — writes the seeding plan to the work root artifact. Deterministic from inputs, exits 0 or 2. No ADO calls.
2. **`polyphony apply-seeding-plan`** — reads the plan, queries ADO state, creates only what's missing, returns a structured result. Exit codes: 0 (fully converged), 5 (ADO unreachable), potentially a new partial code (see Q5). Domain outcome if the plan references a non-existent parent: exit 0, error in stdout.

One verb trying to do both is an idempotency footgun.

---

## Q5: Partial-Success Exit Code

I **partially disagree** with adding a code 6 = partial success. Here's why: if `apply-seeding-plan` is designed to be re-run until it reaches full convergence, partial success is just "not done yet" — the verb exits 5 (ADO unreachable) or exits 0 with a structured result showing what converged. The conductor retries on 5. On exit 0, the workflow checks the result and decides whether to loop.

If we do add a partial-success code, it belongs at **7** (6 is timeout). But I'd rather prove the design works without it first. A new code forces every `on_error:` handler in every workflow to handle it. That's a fleet-wide change.

---

## Q6: Cross-Platform

Exit codes must be **platform-agnostic**. Code 5 = "upstream platform unreachable." The `kind` field in `CONDUCTOR_ERROR_OUT` carries the specifics: `"kind": "ado_unreachable"` vs `"kind": "github_unreachable"`. The caller knows the platform from its own config. The exit code just tells conductor "this is retryable infrastructure" — conductor should not need to inspect `kind` for retry decisions.
