# Polyphony North Star

> **Status:** Living document. Sections labelled **(locked)** are the result of explicit decisions reached through grilling sessions; sections labelled **(open)** are deferred to follow-up sessions. Re-grill before changing a locked decision.
>
> **Scope:** What polyphony is, who it's for, why it exists, where its seams live, the shape of operator engagement, the philosophy that governs when polyphony surfaces a moment for operator input, the trust-ramp posture between polyphony and the operator, the time-scale agnosticism of the engine, and the failure/recovery posture. Observability, multi-run concurrency, and success signal are still open (§5.6–§5.8) and will be filled as the corresponding grilling completes.
>
> **Companion docs:** [`docs/glossary.md`](glossary.md) is the ubiquitous-language source of truth; [`docs/polyphony-architecture.md`](polyphony-architecture.md) is the layering/data-flow reference; [`docs/decisions/`](decisions/) holds the ADR catalogue.

---

## 1. Identity (locked)

Polyphony is a **type-agnostic SDLC routing engine for Azure DevOps work items**. Its architecture is **platform-portable by design**: ADO and twig sit behind a YAML-level seam so a future polyphony can adopt other platforms without re-architecting the engine. Today, ADO is the only supported platform; portability is a property we preserve, not a feature we ship.

This is the **A-today / B-by-design** framing: Identity-A (ADO-only) describes current reality; Identity-B (platform-portable) describes the architecture we will not regress.

---

## 2. Audience (locked)

**Primary audience.** A single **operator** driving their own ADO backlog with AI agents. The operator supervises; the agents do the keystrokes; polyphony is the deterministic skeleton both sides trust.

**Supported beyond primary.**

- **iii-B (in-goal):** Teams adopting polyphony as a shared pattern — each operator runs their own instance against a committed `.polyphony-config/`. Achieved through docs and conventions, not by building new machinery.
- **iii-A (supported but not optimized):** Concurrent multi-operator runs against the same repo, as a side effect of the existing run-lock model and per-run worktree layout.

**Deferred — not built for, not foreclosed.**

- **iii-C:** Hosted/multi-tenant polyphony service. Would require a different product (auth, RBAC, queuing, persistence-across-restarts). We do not build for it, but we do not actively foreclose it either — e.g., we do not hard-wire `~/.polyphony/` deep into the engine.
- **iii-D:** Unattended CI-style runs without a live operator. Requires answering "what does polyphony do at a `human_gate` when nobody is reachable?" — deferred until we have a real use case.

**Long-arc aspiration informing design without gating features.**

- **iv:** Agents operating with progressively less human supervision. This is the trajectory polyphony's existence points along; it is not a feature we currently ship.

---

## 3. Mission (locked)

Polyphony exists to make **AI-agent-driven SDLC trustworthy**. Agents are individually capable but collectively unreliable: they hallucinate states, skip steps, take destructive shortcuts, and lose track of lifecycle. Polyphony pins down the decisions agents shouldn't be making — what phase a work item is in, what branch to cut, when to call the PR done, when to merge, when to escalate — as deterministic verbs over typed inputs.

**Agents do the work; polyphony decides what work, in what order, against what guardrails. The operator supervises outcomes instead of steps.**

**"Trustworthy" decomposes as:**

- **Deterministic** where it can be (routing, validation, state interpretation).
- **Bounded** where it can't be (agent invocations have explicit scope, tool addenda, evidence requirements).
- **Recoverable** when it goes wrong (gates, evidence, reset, restack).

### The α / β / γ framing

| | Role | What it is | Notes |
|---|---|---|---|
| **α** | **Mission** | Make agent-driven SDLC trustworthy. | What we exist for. Arbitrates priorities. |
| **γ** | **Mechanism (currently chosen)** | Config-driven type-agnosticism so the same engine works across ADO process templates (Basic / Agile / Scrum / CMMI / custom) without rewriting workflows. | How we currently achieve α. Could change. |
| **β** | **Hook** | Relieve the SDLC busywork (branch naming, state transitions, PR plumbing) humans hate. | How we explain polyphony to skeptics. Does not drive priorities. |

When α, β, γ conflict in feature prioritization, **α wins**.

---

## 4. Seams

Polyphony is defined as much by what sits on the *other* side of each seam as by what's inside it.

### 4.1 Polyphony ↔ conductor (locked, S1)

Polyphony **authors** SDLC workflows and the deterministic verbs they call; **conductor executes** them. The boundary is **directional** — when polyphony needs a capability conductor lacks, we **grow conductor**, we do not absorb its domain.

Polyphony may grow adjacent operator surfaces (visualizations, run tooling, and possibly a workflow-authoring DSL that compiles to YAML at build time — see [`docs/discussions/workflow-compiler-evaluation/`](discussions/workflow-compiler-evaluation/)) that complement conductor without replacing its execution role. The YAML interface between authoring and execution is a **stable, jointly-evolved contract**.

The polyphony C# engine stays engine-agnostic (no conductor types leak in) — **hygiene that keeps the verbs usable standalone**, not a substitutability claim. We are committed to conductor as the engine, not preserving optionality to swap it out.

**Generates the invariants:**

- No orchestration runtime inside polyphony.
- No conductor types in `src/Polyphony/{Routing,Configuration,Policy}/`.
- Workflow suite and CLI ship from the same repo and release together (single version stamp).
- "Grow conductor first" is the default response when tempted to build orchestration behaviour polyphony-side.

### 4.2 Polyphony ↔ twig (locked, S2)

**Engine ↔ ADO seam (load-bearing).** Polyphony's core engine (routing, validation, hierarchy, state interpretation, policy) reads ADO state through twig's read-side library (`Twig.Domain`, `Twig.Infrastructure`, project-referenced and in-process) and **never directly accesses ADO**. Verified by grep: `src/Polyphony/{Routing,Configuration,Policy}/` holds zero references to `Polyphony.Infrastructure.AzureDevOps`. Promotable to a lint rule.

**Work-item writes (load-bearing).** ADO work item state writes — fields, states, tags, comments, sync — cross a process/service boundary into twig. Twig owns the write transaction.

**Bounded carve-outs (current reality, not invariant).** Polyphony's infrastructure layer holds platform transport for capabilities no upstream tool covers today:

- **ADO Pull Requests:** in-process REST via `src/Polyphony/Infrastructure/AzureDevOps/AdoClient.cs`, used by the `Commands/PrCommands.*Ado.cs` family. Polyphony holds ADO credentials *only for this purpose*.
- **GitHub Pull Requests:** shell-out to `gh` via `src/Polyphony/Infrastructure/Processes/GhClient.cs`.

These carve-outs live in `Infrastructure/` and the `Commands/Pr*` family, never in the engine.

**Operating norm (provisional, may evolve).** Delegate to the dedicated upstream tool when one exists; carry transport in-process only when forced to. The current mix follows this rule. May be promoted to a load-bearing invariant after surviving more carve-out comparisons.

**Open architectural question (deferred to project-level).** Should ADO PR transport migrate to twig (or another tool) over time? "Grow twig, don't absorb twig" points toward yes (i.e., Option A — twig grows PR support, polyphony's `AdoClient` retires). Decision deferred — to be resolved through a stakeholder conversation with twig when capacity permits, not in the north star.

**Generates the invariants:**

- No ADO REST in `src/Polyphony/{Routing,Configuration,Policy}/`.
- Polyphony holds no ADO credentials for work-item operations (twig holds them).
- Verb naming discipline: engine verbs decide (`route`, `validate`, `ensure`, `resolve`, `check`); they do not execute ADO writes.
- Twig's CLI surface is a stable contract — arg shapes are load-bearing across releases.
- "Grow twig first" is the default response when tempted to build twig-overlapping behaviour polyphony-side.

### 4.3 Polyphony ↔ agents (open, S3)

Not yet grilled. Likely shape: polyphony scopes and bounds the work agents do (facet profiles, addendum composition, tool addenda, evidence requirements); agents produce open-ended content (code, plans, reviews); polyphony reads the artifacts the agent produced. To be verified in a follow-up session.

### 4.4 Polyphony ↔ ADO (open, S4)

Not yet grilled. Likely shape: ADO is the system of record; polyphony owns no authoritative state; every read goes through twig's cache; every work-item write goes through twig (with the bounded ADO PR carve-out from S2). To be verified in a follow-up session.

---

## 5. Behavioral / lifecycle

### 5.1 Operator engagement (locked)

**Polyphony surfaces moments; the operator chooses the depth of engagement.**

At each surfaced moment — a gate fires, a phase completes, evidence is ready, a decision needs sign-off — polyphony presents a concise summary and yields. The operator picks the depth:

- **Rubber-stamp** — "looks fine, continue" — friction-free, one click.
- **Light engagement** — skim the evidence, then continue.
- **Deep engagement** — pause, read carefully, discuss with the agent, redirect.

Polyphony does not force depth. It guarantees *visibility* and *the option to engage*. The operator's time is theirs.

**Policy-configured.** Surfacing thresholds, rubber-stamp eligibility, and force-read requirements (for destructive or high-stakes actions) are governed by **policy** — `.polyphony-config/` for the team-shared shape, `~/.polyphony/` for per-operator preferences. Default policy ships safe; operators tune as trust grows. This is the entry point for the trust-ramp question (§5.3).

**Non-goals.**

- **Continuous-presence-required chat surface.** The operator is not expected to be present moment-to-moment. Polyphony is not Copilot Chat.
- **Forced-deep-engagement as default.** Rubber-stamp is a first-class path. Force-read is rare and policy-justified (e.g., destructive operations).
- **Surface-less runs (fire-and-forget).** There *are* surface moments; the operator may rubber-stamp every one, but they cannot be skipped entirely. (Consistent with iii-D being deferred.)

**Deferred — not foreclosed.**

- **Truly operator-initiated dialogue** — pre-run brainstorming, mid-run pause-and-discuss at moments polyphony did not surface. Plausible future expansion; not built for today; not designed against.

**Generates the invariants:**

- Every surfaced moment is **dual-mode**: a 2-second rubber-stamp must be possible *and* a deeper-engagement entry point must be present. Information design at the surface is load-bearing.
- Surfacing behavior is policy-driven; defaults ship safe.
- Polyphony never executes a destructive operation without at least surfacing it, even when policy permits rubber-stamp.

### 5.2 Gate philosophy (locked)

**Clock time is the currency of every surface.** A surfaced moment is a *pause point* — the run cannot continue until the operator answers. The operator is human; they are not necessarily reachable when the surface fires. Therefore every surface costs **wall-clock latency**, regardless of how cheaply the operator answers once they see it.

This makes attention cost (§5.1) and latency cost (§5.2) orthogonal: rubber-stamp lowers attention; only *not surfacing* lowers latency.

**Three reasons polyphony surfaces** — distinguished by what polyphony *lacks*:

| Category | Capability gap | Operator's leverage | Canonical examples |
|---|---|---|---|
| **Inflection** | Direction-setting authority — polyphony has a direction, needs operator buy-in before committing | Redirect cheaply now, or rubber-stamp | Open questions before plan commit; PR direction confirmation before merge |
| **Surrender** | Decision authority — polyphony has alternatives but no basis to rank them | Pick between options polyphony cannot rank | Cap-hit gates, root-fallback, renegotiation |
| **Attestation** | Action capability — polyphony cannot perform the work itself | Do the work (or arrange for it) and vouch for completion | Actionable-facet satisfaction gates: "release this, provide evidence" |

The inflection category corresponds to moments where *the cost of changing course is about to jump* — the plan hasn't been built yet, the PR hasn't merged yet. Course-correction is cheap now, expensive later. Operator input has maximum leverage at inflection moments.

The surrender category corresponds to moments where polyphony has exhausted its own decision authority — a cap fired, recovery is needed, polyphony lacks context only the operator can supply.

The attestation category corresponds to work polyphony *constitutionally cannot do* — release a product, deploy to production, sign a contract, confirm a real-world fact. The actionable facet is defined by this kind of gap.

**Facet ↔ surface category intersection.** The polyphony facet vocabulary determines which surface categories a step can generate. This is load-bearing:

- **plannable** → inflection (pre-commit to plan)
- **implementable** → inflection (pre-publish PR) + surrender (revise/remediation caps)
- **actionable** → attestation (satisfaction gates)
- **decomposable** → inflection (pre-commit to decomposition)

**What does NOT justify a surface:**

- Boundary-crossings in the technical sense (every CLI call crosses some boundary).
- Routine state transitions polyphony is confident in.
- "Important to log" moments — that's observability (§5.6), not a gate.

**Policy-configured posture per surface.** Even within a category, *how* polyphony surfaces is policy:

- **`manual`** — always surface. Reserved for the highest-leverage surfaces where input value justifies the wait unconditionally (e.g., root PR merge).
- **`warning`** — surface only when triggered (quality threshold missed, cap hit, severity met). Default for most decisions: silent in the common case, surfaces when polyphony's own confidence is low.
- **`auto`** — never surface; engine picks a deterministic answer. For unattended modes, fast-track runs, and kinds the operator has judged delegable.

Workflow YAML declares the **decision kind** (approval, PR merge, open-questions, cap-hit, renegotiation, satisfaction). Policy decides which posture applies, scoped by `root` / `type:<Name>` / `defaults`. Workflow authors do not hardcode surface posture.

**Defaults ship cautious.** Root approval & root PR merge default to `manual`; routine decisions default to `warning`; broad `auto` is opt-in via explicit presets (`.polyphony-config/policy-fasttrack.yaml`).

**Attestation surfaces cannot meaningfully `auto`.** Policy can *suppress* the gate (for fast-track / dogfood / CI), but doing so means the actionable facet *isn't satisfied* — not that polyphony satisfied it on the operator's behalf. **The gate is the satisfaction event.** Actionable facets are constitutionally human-in-the-loop today; this sharpens why iii-D (unattended CI runs) is genuinely deferred-not-trivially-buildable, not merely behind a feature.

**Non-goals.**

- **Surface every boundary-crossing action.** Latency cost is real even for "small" interruptions.
- **Surface on low confidence alone.** Confidence is one input to inflection or surrender posture; it does not by itself justify a surface.
- **Treat all gates as `manual` by default.** `manual` is the most expensive posture; reserve it for decisions where input value clearly exceeds the wait.
- **Hardcode latency-vs-value threshold in workflow YAML.** YAML declares decision kind; policy decides posture.
- **Auto-satisfy attestation surfaces on the operator's behalf.** Suppressing an attestation gate means the facet isn't satisfied, not that the engine satisfied it.

**Deferred — not foreclosed.**

- **Batching / digest surfaces** — group several pending decisions into a single operator session to amortize latency. Plausible if surface volume grows.
- **Notification (non-blocking) surfaces** — FYI events that don't pause the run. Likely lives in §5.6 (observability) rather than §5.2.
- **Designated-actor attestation** — a service account, on-call rotation, or trusted automation that can attest on the operator's behalf for specific actionable-facet kinds. Plausible iii-D enabler; not designed against; not built today.

**Generates the invariants** (additions to §7):

- Surfaces exist at **inflection**, **surrender**, or **attestation** moments; never at every state transition.
- Every gate in workflow YAML declares a **decision kind** scopable by policy.
- The `manual` / `warning` / `auto` posture model (plus decision-kind-specific variants — `auto_proceed`, `auto_fail`, `auto_restart`, `skip`) is the load-bearing surface mechanism.
- Default policy ships cautious; broad `auto` is opt-in via explicit presets.
- **Facet vocabulary determines surface-category eligibility:** plannable / decomposable → inflection; implementable → inflection + surrender; actionable → attestation. Steps inside a facet cannot generate surface categories the facet does not own.
- **Attestation surfaces are constitutionally human-in-the-loop.** Policy may suppress them, but suppression means the facet is unsatisfied — never that polyphony satisfied it itself.

**Preview of §5.3 (trust ramp):** Trust grows *by domain*, not as a single global dial. The operator shifts inflection and surrender postures rightward (`manual` → `warning` → `auto`) selectively — faster for low-stakes work item kinds, slower for root-level decisions. Already reflected in the active `.polyphony-config/policy.yaml` (`Task: auto`, `root: manual`). Attestation surfaces are largely immune to trust ramp — the operator is the only entity that can satisfy them, unless a designated-actor pattern (deferred above) is built.

### 5.3 Trust ramp (locked)

**Trust ramp is the operator's trajectory through policy-space — not a polyphony feature.**

As operator confidence grows, the operator shifts decision kinds rightward (`manual` → `warning` → `auto`) selectively, by domain. Polyphony's role is to make the trajectory *legible* — current state visible, alternatives discoverable, consequences clear. Polyphony does **not** auto-suggest, auto-escalate, or auto-claw-back trust. Those decisions remain explicit and operator-owned.

This posture protects three properties of polyphony's identity:

- **Determinism (§3 mission).** A polyphony whose defaults silently shift over time is not deterministic. The operator must be able to reason about behavior without checking "what trust state am I in today?"
- **Accountability (§5.1).** Auto-escalated trust = polyphony deciding on the operator's behalf about what counts as worth their attention. That's the operator's call.
- **Reversibility.** Any policy posture the operator adopts must be trivially reversible. No baked-in defaults that "learn" and resist reversion.

**Trust grows in shapes, not on a dial.** Four orthogonal dimensions already in the model:

1. **Per work-item-type** (`Task: auto`, `root: manual`)
2. **Per decision kind** (approval / pr / open_questions / unattended / etc. — independent postures)
3. **Per scope** (`defaults` / `root` / `by_type` — most-specific-wins)
4. **Per operator** (`~/.polyphony/` overlays `.polyphony-config/`)

The operator's trajectory: start cautious → observe outcomes → tune specific axes rightward as confidence in specific domains grows. Not linear. Not global. Not automatic.

**Two trust axes the current schema collapses** (worth distinguishing, even if today's policy posture is a single value):

- **Engine trust** — confidence that polyphony's deterministic plumbing fires the right decision kind at the right time. Largely domain-independent; grows once if the engine works. Tuned today via **policy** (`.polyphony-config/policy.yaml`).
- **Agent trust** — confidence that the LLM's output on *this kind of work* is reliable enough to delegate. Strongly domain-dependent; may regress when agents change, work types shift, or familiarity with the domain changes. Tuned today via **guidance** (`.polyphony-config/guidance/`, addendums, tool addenda).

A single `manual` / `warning` / `auto` posture collapses both. The operator's mental model is probably two-axis even if the schema is one-axis; future operator-tuning surfaces should respect the distinction (see *deferred* below).

**Attestation surfaces are immune.** Trust ramp grows polyphony's license to decide on the operator's behalf about things polyphony *can do* — not its ability to act on the real world. (Designated-actor attestation, deferred per §5.2, would be the only path to genuine attestation trust-ramp.)

**Locked properties of operator-tuning surfaces** (vs. today's mechanism):

The four properties below describe what every operator-tuning surface — policy, guidance, future intuitive UIs — must remain. Today's YAML/prompt-file mechanism is *one implementation* of these properties, not the only acceptable one:

- **Operator-owned.** Polyphony never mutates an operator's policy or guidance without explicit operator action.
- **Explicit.** No silent shifts. Schema changes are documented; defaults applied to ungiven values are commented in the artifact.
- **Legible.** The operator can always see their current state in plain text.
- **Reversible.** Any posture or guidance change is trivially revertible. No "learned" defaults that resist reversion.

**Mechanism today is in flux.** Policy lives in YAML; guidance lives in prompt files. Both are functional but unsatisfying. The properties above describe what the surfaces *must remain*; the surfaces themselves are anticipated to evolve.

**Non-goals.**

- **Auto-escalating policy postures.** Polyphony never automatically suggests "you should switch to `auto`" without explicit operator request.
- **Trust as a single global dial.** "Trust level 1–10" oversimplifies; trust is per-domain.
- **Hiding operator-tuning surfaces from operators.** Operators must always be able to see and edit their current state. (Mechanism may evolve; visibility must not regress.)
- **Silent schema evolution.** Schema changes are explicit; defaults applied to ungiven values are documented in artifact comments.
- **"Learned" defaults that resist reversion.** Any operator-set posture must be trivially reversible.

**Deferred — not foreclosed.**

- **More intuitive operator-tuning experience.** Policy management today is YAML editing; agent-prompt management today is prompt-file editing in `guidance/`. Both are functional but unsatisfying. A more intuitive surface — UI, guided editor, suggested presets, telemetry-informed dashboards — is anticipated. Open question: which axes (policy / guidance / process config / profile) belong to one unified surface vs. independent surfaces. Not built today; not designed against. (Detailed shape of the agent-prompt surface specifically lives in §4.3.)
- **Trust telemetry that informs (not auto-suggests).** "This kind of surface fired N times last month; you accepted 95%." Data to help operators tune; doesn't tune for them. Plausible enabler for the intuitive-tuning-surface deferred above.
- **Preset libraries beyond fast-track.** Named presets (`cautious`, `balanced`, `experimental`) mapping to common trajectories.
- **`polyphony policy suggest`** — a verb that recommends posture shifts based on observed outcomes. Explicit operator invocation; never automatic.

**Generates the invariants** (additions to §7):

- Operator-tuning surfaces are **operator-owned, explicit, legible, and reversible**. The *mechanism* by which the operator interacts with them may evolve; these four properties must not.
- Trust ramp is per-domain; **no global trust scalar exists in the model**.
- Default policy ships cautious; ramp-up requires explicit operator action.
- **Engine trust and agent trust are distinct axes** even though current policy schema collapses them; future operator-tuning surfaces should respect the distinction.
- Polyphony never mutates an operator's policy or guidance without explicit operator action.

**Connections to other sections:**

- **§5.4 time scale:** Wall-clock budget per run is a function of policy posture × operator availability. Higher trust → fewer surfaces → shorter runs.
- **§5.5 failure & recovery:** Trust ramp is reversible. After a bad outcome under `auto`, the operator may revert to `warning` — the engine must make this trivial.
- **§5.6 observability:** Trust grows via observation. Better observability between surfaces → faster, better-informed trust ramp.

### 5.4 Time scale (locked)

**Polyphony is *time-scale agnostic* by design. Runs scale with work-item complexity, not toward a single target.**

A run's wall-clock time is the sum of three terms:

1. **Intrinsic work latency** — agent work time + platform/CI latency + real-world attestation time.
2. **Operator response latency** — wall-clock waiting for the operator to answer surfaces (§5.2).
3. **Polyphony overhead** — the engine's own decisions, verb dispatches, state reads.

Current dogfood runs span minutes (a single Task) to multiple days (an Epic with deep decomposition and multiple attestations); polyphony's design holds across that range *without changing shape*.

**Guidance — three illustrative tiers (descriptive, not contractual).** These tiers are *not* categories the engine treats differently; they exist to make "agnostic" concrete and to give downstream sections (§5.5, §5.6, §5.7) shared vocabulary when reasoning about operator habits at different scales:

| Tier | Typical scale | Operator habit | What it stresses in the design |
|---|---|---|---|
| **Tiny** | Minutes to ~1 hour. Single Task, no decomposition, no actionable facet. | Operator may sit through it. | Real-time terminal visibility; fast surface turnaround. |
| **Medium** | Hours to a day. Story-scale work, a few surfaces, maybe one attestation. | Operator checks in periodically. | Persistence across operator absence; surfaces survive the operator stepping away. |
| **Large** | Days. Epic-scale work, deep decomposition, multiple attestations. | Asynchronous engagement is the norm. | Notification (§5.6), multi-run dashboards (§5.7), trust ramp (§5.3) all become load-bearing. |

A messy-attestation Task can span days; an all-`auto` Epic can complete in an hour. The tiers describe the *range* polyphony must handle; the engine itself doesn't branch on them.

**Three properties the engine must hold:**

- **Polyphony overhead is negligible** relative to (1) and (2). The engine's own decisions are measured in seconds; agents in minutes; operators in minutes-to-hours-to-days.
- **Time-scale agnosticism.** No design choice may break at either extreme of the range. The same engine, the same workflows, the same policy model serve every tier.
- **No engine-imposed kill timers.** Operator response latency is bounded only by the operator's choices. Runs do not abandon for "taking too long."

**Why agnosticism, not tier-optimization.** Optimizing for a specific tier (e.g., "we are a tool for hours-long runs") would let us bake in assumptions — operator is at the terminal; a surface fires and resolves within minutes; a run that's been open for a day is a bug — that immediately break for the other tiers. Locking agnosticism rejects those assumptions up front. The §5.2 surface model already accommodates the range: clock time is the currency at every tier.

**Why no kill timers.** Operator silence is *slow*, not *failed*. Polyphony already has surrender surfaces (§5.2 — cap-hit gates, remediation caps) that fire when the *engine* has exhausted its decision authority. Adding a second timeout layer that fires when the *operator* has exhausted polyphony's patience would conflate two distinct failure modes and let the engine abandon work the operator hasn't yet decided to abandon. The cap-hit surface already exists; we don't need a *second* timeout.

**iii-D as the asymptote.** The deferred unattended-CI audience (§2 iii-D) is the asymptotic case of operator response latency = infinity. Polyphony already handles "operator takes a long time" because it's time-scale agnostic. iii-D requires answering "what does polyphony decide when the operator *never* responds?" — a *policy* question (auto-decide rules at scale), not a *time-scale* question. Sharpens why iii-D is a separate deferral.

**Non-goals.**

- **Time-bounded runs / kill timeouts.** Polyphony does not abandon a run because it has "taken too long." Long is a feature, not a bug.
- **Reliance on operator presence at the terminal.** Surfaces must work even if the operator is away (consistent with §5.1 non-goal).
- **A single target time scale.** Polyphony is not "designed for X-minute runs." The engine is time-scale agnostic.
- **Pretending operator response latency is bounded.** Operators take arbitrarily long; the engine accommodates.

**Deferred — not foreclosed.**

- **Configurable max-run-duration check-in *surface*.** "This run has been open for N days; want to abandon, pause, or continue?" A surface (operator decides), not a kill timer. Plausible if long-running runs become common and operators want a nudge. Distinct from a kill timer because the operator answers; the engine does not auto-abandon.
- **Time-aware notification cadence** (belongs in §5.6). Tiny runs notify in real-time; medium-run surfaces notify at the surface moment; large-run surfaces may batch into digests. Flagged here so §5.6 picks it up.
- **Run scheduling.** "Don't fire surfaces between midnight and 7am operator local time." Plausible operator preference; not designed today.

**Generates the invariants** (additions to §7):

- Polyphony's wall clock per run = intrinsic work latency + operator response latency + polyphony overhead. **Polyphony overhead is negligible** relative to the other two terms.
- **Polyphony is time-scale agnostic.** Same engine handles minutes-long and weeks-long runs.
- **Operator response latency is bounded only by the operator's choices**, never by engine timeouts.
- **Run termination is via operator action or a §5.2 surrender surface**, never via a timer.

**Connections to other sections:**

- **§5.5 failure & recovery:** Long runs need cheap interruption recovery. A 3-day run dying on day 2 must be trivially resumable.
- **§5.6 observability:** At larger time scales the operator can't watch the run. Observability is how they know what's happening *between* surfaces.
- **§5.7 multi-run concurrency:** Long runs make multi-run inherent. With several roots in flight, "what needs my attention?" becomes a hard requirement.

### 5.5 Failure & recovery (locked)

Recovery is not magic. Polyphony commits to **legible, operator-mediated recovery over the surface it controls**, and is honest about the surface it does not.

**The state surface, partitioned.**

- **Engine-controlled state surface.** PRs, worktrees, branches, the run manifest, the per-root run-started-at watermark. Polyphony owns these; recovery primitives target them.
- **Out-of-reach state surface.** Uncommitted operator edits in worktrees; twig's per-worktree `.twig/config`; manually-curated work-item fields/tags/state; PRs that the platform has already completed or closed. Polyphony cannot or will not silently rewrite these; operator awareness is required.

The split is load-bearing. Reset is total over the first surface and respectful of the second.

**Failure categories — descriptive, not contractual.**

Useful guidance for shaping recovery behavior:

1. **Infrastructure failure.** Transient: network timeouts, ADO rate limits, conductor process crash. The engine retries; chains halt-on-step-failure; idempotent verbs make retry safe. No surface unless retries are exhausted.
2. **State drift.** The engine's view diverges from observable reality: a branch was deleted out-of-band, a PR was completed manually, the manifest references a merge group that no longer corresponds to live branches. Surfaces as a surrender (per §5.2) — polyphony lacks the authority to choose between repair, accept-divergence, or reset.
3. **Work failure.** Agents produced an unsatisfactory result; an evidence check failed; a PR review surfaced a blocking concern. Routes through the normal SDLC pathway (remediation, restack, abandon) — recovery is the same machinery as forward progress, not a separate code path.

**Recovery primitives.**

- **Observable-state re-entry rebuilds the worklist.** Every batch, `polyphony.yaml` calls `polyphony state next-ready` against the EdgeGraph; the next batch is whatever is ready *now*, given current PR/branch/manifest reality. This is what makes long-running runs survivable across interruptions, conductor restarts, and operator absence (per §5.4). It is the worklist that is re-derived — not the whole engine state.
- **The manifest is mixed-role and authoritative for what it owns.** `MergeGroups` + `TopologyHash`, `PlanGenerations`, `RetiredMergeGroupIds`, and `HumanApprovals` are authoritative state the engine cannot reconstruct from git/ADO alone. `Rebases` and `MergedPlanPrs` are log-shaped (audit / idempotency ledger). Recovery presupposes the manifest is intact; corruption is a different failure mode and out of scope.
- **Reset is the universal escape hatch over the engine-controlled surface.** `reset-root@polyphony` is operator-mediated end-to-end: dry-run preview → confirmation gate showing per-leg counts (PRs to abandon, worktrees to remove, branches to delete, manifest action, watermark target) → execute. The chain halts-on-step-failure; verbs are idempotent on retry. Reset is always available; it is never auto-invoked.
- **Surgical fixes happen via the same verbs that move work forward.** A single failed PR is remediated via the normal feature-PR-remediation pathway, not a recovery-specific code path. This keeps recovery cheap when reset would be heavy-handed.
- **Operator-visible artifacts are never silently auto-modified during recovery.** Reset deletes only via operator-confirmed gate; remediation comments on PRs are explicit and attributed; the manifest is mutated only by named verbs the operator can audit.

**The residual gap (honest about what reset does not handle).**

`reset-root` covers the engine-controlled surface cleanly. The operator still must, today, handle the four residual classes themselves: stash dirty worktree edits before reset will proceed; restore `.twig/config` when twig has rewritten it; verify work-item state baseline if they hand-edited tags/fields between runs; accept that platform-completed PRs cannot be un-merged (the run-started-at watermark hides them from observers, which is the design). The `polyphony-dogfood-recovery` skill catalogues these gotchas as a runbook.

This residual operator load is the live UX-maturity gap. Closing it is **deferred, not foreclosed.**

**Deferred — not foreclosed.**

- **Reduce manifest to a log.** Derive topology, plan generations, retirements, and approvals from git refs / PR descriptions / ADO tags so the manifest could be regenerated from observable reality. Would make recovery near-magical but is a material redesign of the authoritative surface; explicitly out of scope today.
- **Expand reset to cover more of the residual surface.** Auto-stash + restore for dirty worktrees; `.twig/config` reconciliation; structured work-item-state diff & restore prompts. Plausible incremental UX wins.
- **Surface-mediated recovery.** When reset hits a residual gotcha (e.g., dirty worktree), surface a structured remediation surface (per §5.2) rather than printing a CLI error the operator has to interpret.
- **Recovery diagnostics surface** (`polyphony run diagnose` or equivalent) — single command that surfaces "what is in this run's state surface, what is anomalous, what would reset do." Sharpens the operator's ability to choose between surgical fix, reset, and abandon.

---

### 5.6–5.8 Open questions

Queued for follow-up grilling sessions, in dependency order:

1. **§5.6 Observability.** How does the operator know what polyphony is doing without watching it? (Distinct from §5.1: §5.1 is about engagement *at* surfaced moments; §5.6 is about awareness *between* surfaced moments. Notification-style non-blocking surfaces likely live here. Trust ramp depends on observability — see §5.3. Time-aware notification cadence lives here — see §5.4. Recovery diagnostics surface from §5.5 also lands here.)
2. **§5.7 Multi-run concurrency.** Operator with several roots in flight — what surface tells them which needs attention? (Made inherent by larger time scales per §5.4.)
3. **§5.8 Success signal.** How do we know polyphony is winning?

---

## 6. Non-goals (consolidated, locked so far)

- **Generalize polyphony to non-SDLC work.** SDLC is the domain. Facet vocabulary is SDLC-shaped.
- **Absorb conductor's domain.** No polyphony-native workflow runtime; no polyphony-native gate execution; no polyphony-native agent process supervision. Grow conductor instead.
- **Absorb twig's domain.** No ADO REST in the engine; no second cache; no bypass writes for work-item state. Grow twig instead.
- **Become a hosted multi-tenant service** (iii-C, deferred, not foreclosed).
- **Run unattended in CI without a live operator** (iii-D, deferred, not foreclosed).
- **Be a continuous-presence-required chat surface.** Operator is not expected to be present moment-to-moment. (See §5.1.)
- **Force deep engagement as the default at surfaced moments.** Rubber-stamp is a first-class path; force-read is rare and policy-justified. (See §5.1.)
- **Surface every boundary-crossing action.** Crossing a process boundary does not by itself justify a surface; latency cost is real even for "small" interruptions. (See §5.2.)
- **Treat all gates as `manual` by default.** `manual` is the most expensive posture; reserve it for the highest-leverage inflection/surrender/attestation surfaces. (See §5.2.)
- **Auto-satisfy attestation surfaces on the operator's behalf.** Suppressing an attestation gate means the actionable facet is unsatisfied, not that polyphony satisfied it. (See §5.2.)
- **Auto-escalate, auto-suggest, or auto-claw-back policy postures.** Trust ramp is the operator's trajectory; polyphony makes it legible but never moves the operator along it without explicit action. (See §5.3.)
- **Treat trust as a single global dial.** Trust is per-domain; no global trust scalar exists in the model. (See §5.3.)
- **Mutate an operator's policy or guidance.** Operator-tuning surfaces are operator-owned. Polyphony never edits them without explicit operator action. (See §5.3.)
- **Allow "learned" defaults that resist reversion.** Any operator-set posture must be trivially reversible. (See §5.3.)
- **Impose kill timers on runs.** Runs do not abandon for "taking too long." Long is a feature, not a bug. Operator silence is slow, not failure. (See §5.4.)
- **Rely on operator presence at the terminal.** Surfaces must work even if the operator is away. (See §5.1, §5.4.)
- **Optimize for a single time-scale tier.** Polyphony is time-scale agnostic; the same engine serves minutes-long and weeks-long runs. (See §5.4.)
- **Pretend reset is total.** Reset covers the engine-controlled state surface (PRs, worktrees, branches, manifest, watermark). Uncommitted operator edits, `.twig/config` drift, manually-curated work-item state, and platform-completed PRs are outside reset's reach by design or by platform limitation. Operator awareness of these residuals is required today; closing that gap is deferred, not foreclosed. (See §5.5.)
- **Auto-invoke recovery.** Reset is never automatic. Surgical fix verbs run under normal operator-mediated flow. Engine-side retries are bounded to transient infrastructure failures; anything requiring a decision surfaces. (See §5.5.)
- **Silently auto-modify operator-visible artifacts during recovery.** Reset gates on confirmation; remediation comments are explicit and attributed; the manifest is mutated only by named, auditable verbs. (See §5.5.)
- **Hold ADO credentials for work-item operations** — twig holds them. *Carve-out: polyphony holds ADO creds for PR operations today; see §4.2.*
- **Preserve engine substitutability** as a feature. Conductor-as-engine is a strategic commitment; engine-agnostic verb vocabulary is hygiene, not portability.

---

## 7. Load-bearing invariants (consolidated, locked so far)

- **Engine is platform-agnostic.** `src/Polyphony/{Routing,Configuration,Policy}/` holds no platform-specific types. Promotable to lint.
- **No work-item-type names anywhere in routing logic.** Type names live only in `process-config.yaml`, loaded at runtime. (Authoritative: [`docs/glossary.md`](glossary.md) "Type-agnosticism rule".)
- **No conductor types in the polyphony engine.** Verbs are callable standalone.
- **No ADO REST in the polyphony engine.** ADO transport lives only in the infrastructure layer, only for bounded carve-outs.
- **Workflow YAML and CLI release together.** Single version stamp; no cross-repo skew.
- **Grow upstream tools (conductor, twig); do not absorb their domains.** Default response when tempted is "file an upstream issue."
- **Polyphony interprets work-item state; it never originates state.** ADO is the system of record.
- **Polyphony decides; twig executes ADO work-item writes.** Verb names reflect this (decide-verbs, not execute-verbs).
- **`.polyphony-config/` is team-shared; `~/.polyphony/` is per-operator.** Boundary supports iii-B (team adoption as pattern) without committing to iii-C (hosted service).
- **Every operator-facing surface is dual-mode.** A 2-second rubber-stamp must be possible *and* a deeper-engagement entry point must be present. Information design at the surface is load-bearing, not cosmetic. (See §5.1.)
- **Surfacing behavior is policy-driven; defaults ship safe.** Operators tune surfacing thresholds, rubber-stamp eligibility, and force-read requirements through `.polyphony-config/` and `~/.polyphony/`; the engine ships defaults that err on the side of surfacing. (See §5.1.)
- **Surfaces are inflection, surrender, or attestation moments — never every state transition.** The three categories cover every justified surface; no other category exists. (See §5.2.)
- **Every gate in workflow YAML declares a decision kind scopable by policy.** The `manual` / `warning` / `auto` posture model (plus kind-specific variants — `auto_proceed`, `auto_fail`, `auto_restart`, `skip`) is the load-bearing surface mechanism. Workflow authors do not hardcode surface posture. (See §5.2.)
- **Facet vocabulary determines surface-category eligibility.** plannable / decomposable → inflection; implementable → inflection + surrender; actionable → attestation. Steps inside a facet cannot generate surface categories the facet does not own. (See §5.2.)
- **Attestation surfaces are constitutionally human-in-the-loop.** Policy may suppress them, but suppression means the actionable facet is unsatisfied — never that polyphony satisfied it itself. (See §5.2.)
- **Operator-tuning surfaces are operator-owned, explicit, legible, and reversible.** Policy, guidance, and any future intuitive-tuning surface must hold all four properties. The mechanism (YAML today; possibly UIs / guided editors later) may evolve; these properties may not. (See §5.3.)
- **Trust ramp is per-domain; no global trust scalar exists in the model.** (See §5.3.)
- **Engine trust and agent trust are distinct axes.** Today's policy posture collapses them into a single value; future operator-tuning surfaces should respect the distinction. (See §5.3.)
- **Polyphony never mutates an operator's policy or guidance without explicit operator action.** (See §5.3.)
- **Polyphony is time-scale agnostic.** Same engine handles minutes-long and weeks-long runs without changing shape. Polyphony overhead is negligible relative to intrinsic work latency and operator response latency. (See §5.4.)
- **Operator response latency is bounded only by the operator's choices, never by engine timeouts.** Run termination is via operator action or a §5.2 surrender surface; never via a timer. (See §5.4.)
- **Observable-state re-entry rebuilds the worklist, not the whole engine state.** Every batch is re-derived from `polyphony state next-ready` + EdgeGraph against current git/ADO/manifest reality. This is the load-bearing recovery primitive that makes long runs survivable across interruptions. (See §5.5.)
- **The manifest is authoritative for topology, plan generations, retirements, and approvals.** These cannot be reconstructed from git/ADO alone. `Rebases` and `MergedPlanPrs` are log-shaped (audit / idempotency); the rest is authoritative state recovery presupposes. (See §5.5.)
- **Reset is always available, always operator-mediated, never auto-invoked.** Preview → confirmation gate → execute is the only path; verbs are idempotent on retry; the chain halts-on-step-failure. (See §5.5.)
- **Surgical fixes use the same verbs that move work forward.** Recovery is not a separate code path from normal SDLC progression — it is the same machinery applied in a remediation direction. (See §5.5.)

---

## 8. Provenance

This document is built incrementally through grilling sessions using the `grill-with-docs` skill. Each section is locked only after explicit question-and-answer agreement. The session that locked each section is recorded in git history; the open-questions list (§4.3, §4.4, §5.6–§5.8) and the deferred-decision list (§4.2 ADO PR direction; §5.1 operator-initiated dialogue; §5.2 batching / notification / designated-actor attestation; §5.3 intuitive operator-tuning experience / trust telemetry / preset libraries / `policy suggest` verb; §5.4 max-run-duration check-in surface / time-aware notification cadence / run scheduling; §5.5 reduce-manifest-to-a-log / expand reset coverage / surface-mediated recovery / recovery diagnostics surface) are the live agenda for subsequent sessions.

When this document and [`docs/glossary.md`](glossary.md) diverge: glossary wins for terminology, north star wins for direction. Update both together.
