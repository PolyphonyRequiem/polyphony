# D2 — Hostile Architect Critique: The Case Against Compiler-Lite

**Verdict: DO-NOT-BUILD** — compiler-lite is misdiagnosing a lint/schema/CI maturity problem as an authoring-surface problem, and would add a second source of truth, generator maintenance, source-map/debugging obligations, and platform-vendor complexity before the existing YAML+Conductor contract has been fully exercised.

**Counter-proposal:** Do not build a C# workflow authoring surface. Instead, finish AB#3255/#3256/#3258, publish/validate the Conductor workflow schema, add contract-aware YAML lint in CI, and measure whether real runtime contract failures remain before reopening the compiler question.

---

## 1. The "YAML is fine, the lints aren't" argument

Read the past month of incidents and ask which class they belong to:

- M4/M10 catch-all ordering and cycle-back bugs → **lint rule that didn't exist**
- `ado_pending_poll_counter` → `pending_poll_counter` rename rot → **lint expected old name, was not updated**
- `.conductor/registry/tests/*.Tests.ps1` silently uninvoked → **CI discovery gap**
- `if: always()` missing on Install steps → **GitHub Actions policy lint missing**
- 15 workflow YAML lockstep bumps → **release-cut tooling missing**
- `--apex` vs `--apex-id` drift → **two authors of the same contract, neither generated from the other**
- Policy fields `mode`/`quality_threshold`/`max_fix_loops` dead → **runtime reachability not enforced**
- Reset residue (AB#3245/#3246) → **enumeration gap in the verb implementation, plus race condition**

Compiler-lite addresses approximately zero of these directly. It addresses some of them aspirationally — "if the typed contract registry covered launcher args AND CLI args AND workflow args, the `--apex` drift would be caught." But that's a 12-month project, not a compiler-lite Phase 1.

The actual highest-leverage fixes for the past month's pain are:
1. CI discovery sentinel (any `*.Tests.ps1` not invoked = CI failure)
2. Conductor workflow schema published + JSON-Schema-validated in CI
3. Lint over `node.output.field` references against verb result schemas (uses **existing** `VerbOutputSchemaCatalog`)
4. Release-cut manifest tool that bumps all 15 workflows together
5. Policy reachability test (every PolicyConfig field is read by at least one workflow step)

None of those require a C# DSL. None require Roslyn analyzers. None require generated YAML. All of them ship in 1-2 PRs each. Total estimated effort: ~3 sprints.

Compiler-lite's estimated effort is 3-4 sprints **for Phase 1 alone**, with no production benefit until Phase 2-4. The opportunity cost is fixing real bugs versus building a layer that may eventually catch some lint drift.

## 2. The "two sources of truth" argument

Every code-gen story in the prior-art investigation comes with the same anti-pattern: "I fought my own generator." Bazel users edit BUILD files alongside Starlark macros and accept the drift. Pulumi users hand-edit generated stacks during incidents and accept the recovery work. CDK users debug synthesized CloudFormation, not their TypeScript.

Polyphony is proposing to add the same authoring split:

```
Layer 1: C# DSL (authored)
Layer 2: Generated YAML (executed)
Layer 3: Conductor runtime (consumes Layer 2)
Layer 4: Operator (reads Layers 1 + 2 + runtime output)
```

Today the operator reads Layer 2 and runtime output. That's 2 surfaces. Compiler-lite makes it 4. The synthesis claims source maps will compress this back to "essentially 2" — but source maps are themselves a maintenance burden, and they decay first under any refactor.

For a team Polyphony's size, two sources of truth means the senior engineer becomes the only person who can debug production. That's worse than the current YAML-is-ugly-but-debuggable state.

## 3. The "compiler infinite regress" argument

Once you have compiler-lite, the next 8 quarters look like:

- Q1: compiler-lite ships. Catches some drift. Team excited.
- Q2: "we should add Roslyn analyzers for the route-condition DSL." Diagnostic IDs invented. Analyzer release schedule starts.
- Q3: "the analyzer false-positives on legitimate hand-edited workflows during incidents. Add suppression mechanism."
- Q4: "we need a language server so VS Code can show inline diagnostics on the C# workflow DSL."
- Q5: "the language server needs a debugger that maps runtime YAML errors back to source DSL spans."
- Q6: "the source-map format itself needs to be versioned, because we changed how we emit `route_when` conditions."
- Q7: "Conductor upstream changed schema. We need a Conductor-schema-version → DSL-version compatibility matrix."
- Q8: "the team velocity has dropped 40% because every workflow change now requires updating the DSL, the analyzer, the language server, the source map, and the harness."

That's not a parody. That's how every typed-host-language → emitted-runtime-artifact project actually evolves. The synthesis under-counts maintenance cost by ~3x because it priced Phase 1 only and assumed Phases 2-4 are "optional follow-ons." They are not optional. Once Phase 1 ships, Phase 2 is the only logical extension. Phase 3 follows from Phase 2. By Phase 4, you have a platform.

## 4. The "Conductor is the type system you wish you had" argument

Conductor already validates workflow YAML. It has a schema. The schema isn't published as a JSON-Schema artifact today, but it could be in 1-2 PRs. Once published:

- All 15 workflows pass through schema validation in CI before any harness run.
- The schema catches malformed nodes, missing required fields, unknown route conditions.
- Conductor itself enforces the schema at runtime, which means the schema can't drift from execution semantics.

The synthesis treats Conductor as a black-box runtime. It is not. It is a typed system that Polyphony is choosing not to lean on. Investing in the Conductor schema (publishing it, validating against it, contributing route-condition exhaustiveness lint upstream) gives 70% of compiler-lite's safety with 0% of the new authoring surface.

If Conductor's schema is insufficiently expressive, the right move is to contribute upstream — not to wrap it in a parallel C# type system that has to be kept in sync forever.

## 5. The "polyphony is not a platform vendor" argument

Bazel: ~500 engineers, ~10M+ users, decade of investment.
Pulumi: ~100 engineers, ~100K users.
CDK: ~50 engineers, ~1M users.
Polyphony: Daniel + a small team, 1 production repo (cloudvault-service-api), ~6 months of investment.

Bringing the typed-host-language-with-emitter pattern to a one-team project is over-engineering. The synthesis estimates "3-4 sprints" for Phase 1. Bazel's Starlark interpreter was 10+ engineer-years. Pulumi's resource model was 5+ engineer-years. Polyphony cannot amortize this cost over millions of users.

Daniel's actual constraint is engineering hours. The 3-4 sprints compiler-lite would consume are the same 3-4 sprints that could:
- Finish AB#3258 (script→verb migration) — eliminates an entire category of fragility
- Land AB#3256 (PR/branch matrix collapse) — reduces workflow count, which is the actual "mess" complaint
- Ship Phase 4 journal drift verb — turns half-wired policy into enforced policy
- Fix AB#3245/#3246 reset residue — operator pain that recurs weekly
- Wire mode/quality_threshold/max_fix_loops policy fields — closes the half-wired policy hole
- Publish Conductor schema + JSON-Schema CI gate — 70% of compiler-lite's lint value

Each of those has users today. Compiler-lite has users in Phase 4 of a multi-quarter roadmap.

## 6. The "agent contract is YAML's natural home" argument

Agent contracts live closest to the YAML that invokes the agent. The contracts are themselves prose-shaped (descriptions, schemas, examples) and the unit of debugging is the prose. Today, debugging a misbehaving agent means:

```
1. open the workflow YAML
2. read the agent block: model, prompt, output schema, routes
3. compare against the run log
4. edit prompt or routes
5. rerun
```

That's a 5-step loop with one artifact open.

Under compiler-lite:

```
1. open the run log
2. find the failing node in generated YAML
3. follow source map to C# contract
4. read the C# record
5. follow attribute to prompt-fragment generator
6. read the human-authored prompt markdown
7. cross-reference fragment composition
8. edit C# contract (or prompt, or both)
9. rebuild solution
10. regenerate YAML
11. golden-diff test
12. rerun harness
13. rerun real workflow
```

That's a 13-step loop with five artifacts open. The synthesis says "source maps make this OK." Source maps decay first. And even with perfect source maps, the human-readable artifact is still the YAML, not the C# — because debugging is about reading what's actually executing.

The synthesis's strongest defense — "the C# contract becomes the place where field renames are mechanical" — is true. But field renames are not the operator's pain. Agent behavior is the operator's pain. Compiler-lite optimizes for the rare case (rename) at the cost of the common case (debug).

## 7. The "compiler-lite is sunk-cost rationalization" argument

Reread the session history:

- Daniel was annoyed at the workflow mess
- Daniel asked "should we declare everything in C# and generate workflow JIT?"
- 5 investigators were sampled from agents Daniel trusts
- All 5 returned "yes, qualified"
- 3 reviewers were sampled
- All 3 returned "qualify, don't endorse"
- Synthesizer produced an HTML deck recommending compiler-lite

That's not independent verification. That's a beautifully-staged confirmation cascade. The 8 agents that agreed all started from the same prior (compiler-lite is plausible) and arrived at compatible conclusions. None of them asked the meta-question: "is the original framing wrong?"

The right answer to "should we declare everything in C# and generate workflow JIT?" is: **no, you should fix the lints**.

The framing assumed authoring surface was the problem. Evidence says lint discovery, CI gaps, policy reachability, and verb-effect contracts are the problem. Those are addressed by:
- AB#3255 typed contract surface (already planned, no compiler-lite needed)
- AB#3256 matrix collapse (already planned, no compiler-lite needed)
- AB#3258 script→verb migration (already planned, no compiler-lite needed)
- Conductor schema publishing (1-2 PRs)
- CI discovery sentinel (1 PR)
- Policy reachability tests (1-2 PRs)

The synthesis is a beautiful HTML version of the sunk-cost reasoning that "since we already asked the question, we should answer it positively."

I'm the last guardrail. Compiler-lite is a solution to a problem Polyphony doesn't have yet. Build it only after the smaller fixes prove insufficient.

## 8. The "what does this stop you from doing" argument

Every hour spent on compiler-lite is an hour not spent on:

- **Journal drift verb (Phase 4)** — turns half-wired policy into enforced policy. Real operator value.
- **Reset bugs (AB#3245/#3246)** — recurs weekly. Manual cleanup tax.
- **on_error migration (AB#3257)** — upstream-blocked but tractable once Conductor unblocks.
- **Script→verb migration (AB#3258)** — eliminates a fragility class.
- **Policy half-wiring (AB#3217)** — fields exist but are dead. Embarrassing.
- **Verb matrix collapse (AB#3256)** — actually reduces workflow count.
- **Typed contract surface (AB#3255)** — does 70% of compiler-lite's lint value with no new platform.

Opportunity cost is not abstract. It's measurable. If Daniel ships compiler-lite Phase 1 in 3-4 sprints, he ships **none** of the above in the same period. If he ships the 7 items above in 3-4 sprints, he closes 7 real wounds and *then* can ask "do we still need compiler-lite?" The answer at that point may be: "no, the wounds are closed, the lints are sufficient, ship something else."

The synthesis does not consider this counterfactual. It compares "compiler-lite vs current chaos" instead of "compiler-lite vs finishing the 7 already-planned fixes."

## 9. Steelmanning the synthesis's strongest defenses

The synthesis's best arguments:

> "Compiler-lite is reversible. If it doesn't work, throw it away."

Code-gen pipelines are notoriously hard to throw away. Generated YAML accumulates muscle memory. The team learns to "just edit the C#." Throwing away a code-gen pipeline after 6 months of adoption is a year of rework. "Reversible" is true on paper, false in practice.

> "Source maps + checked-in YAML keep the operator surface unchanged."

Empirically, projects that ship generated artifacts and source maps see the source-map fidelity decay first. Within 6 months, half the source-map ranges point to the wrong line. The operator stops trusting them. The benefit promised by checked-in YAML survives; the benefit promised by source maps doesn't.

> "All three reviewers agreed."

Three reviewers sampled by the synthesizer from the same agent pool, primed with the same investigation reports, arrived at compatible conclusions. That's expected, not validation. The reviewers were not asked "is the framing wrong?"; they were asked "given this proposal, what's your critique?" The critique was sympathetic by construction.

> "The agent contract boundary is the highest-value target."

R2 already softened this. D4 (LLM skeptic) demolished it. The agent contract is not amenable to compile-time typing because the agent is a stochastic process. Compiler-lite cannot help. The best compiler-lite can do is type the *projection* of agent output after parsing — which is route lint, not "agent contract." Once the framing collapses to "route lint," compiler-lite is wildly overscoped for the problem.

## 10. The counter-proposal

Instead of compiler-lite, do this — in this order:

**Sprint 1: Discovery + Schema**
- CI discovery sentinel: fail if any `*.Tests.ps1` is not invoked
- Publish Conductor workflow schema as JSON-Schema artifact
- CI gate: all 15 workflows pass schema validation
- Policy reachability test: every PolicyConfig field is read by ≥1 workflow step

**Sprint 2: Existing-Investment Wins**
- Land AB#3255 typed contract surface (the existing planned work)
- Use `VerbOutputSchemaCatalog` to lint workflow `node.output.field` references in PowerShell
- Add mutation tests proving the lint catches typos

**Sprint 3: Matrix Collapse + Script→Verb**
- Ship AB#3256 (PR/branch matrix collapse) — reduces workflow count
- Ship AB#3258 (script→verb migration) — eliminates script fragility class

**Sprint 4: Operational Fixes**
- Fix AB#3245/#3246 (reset enumeration + worktree race)
- Wire mode/quality_threshold/max_fix_loops in policy (AB#3217)
- Release-cut manifest tool: bump all 15 workflows + index in one command

**Sprint 5: Measure**
- Look at the bug ledger after 4 sprints
- Categorize remaining bugs: are they "shape" bugs that compiler-lite would catch, or "effect"/"semantic"/"operational" bugs that it wouldn't?
- If shape bugs dominate the remaining ledger, reopen the compiler-lite question with evidence
- If they don't, declare the lint maturity sufficient and move on

That's 4 sprints of fixes, 1 sprint of measurement. Same total cost as compiler-lite Phase 1, with **closes 7 real wounds today** instead of **opens 1 new platform that might catch some drift in Q4**.

## Final verdict: DO-NOT-BUILD

Compiler-lite is intellectually beautiful and operationally premature. The synthesis is right that polyphony has duplicated string contracts and lint drift, but wrong that the fix is a typed authoring surface. The fix is publishing the schema we already have, lint maturity over the catalog we already have, and finishing the sibling tracks that are already planned. Reopen the compiler-lite question when those are done — at which point the answer will likely be "we don't need it anymore."
