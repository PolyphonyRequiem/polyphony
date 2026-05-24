---
doc_type: discussion
status: exploratory
synopsis: Grumpy-operator critique of compiler-lite — verdict QUALIFY; approve narrow slice only with explicit kill criteria.
---

# D1 — Grumpy Operator Critique: Compiler-Lite Through the "It Burned Me Again" Lens

**Verdict: QUALIFY** — approve only the narrow compiler-lite slice with explicit kill criteria and no permission to pretend it fixed the engine.

---

## Pain audit: does compiler-lite actually touch the bruises?

Here is the part I do not want hand-waved: most of the pain that triggered this whole "maybe a compiler saves us" arc was not "field name typo in YAML." It was operational coupling, split surfaces, dead config, CI blind spots, runtime effect lies, and the workflow graph becoming too stupidly large to hold in a human head.

| Recent pain | Compiler-lite helps? | Grumpy read |
|---|---:|---|
| Reset residue: nested branches, per-item branches, worktrees race, success=true despite gaps | Mostly no | Typed contracts do not delete branches. They do not prove reset effect completeness. Unless compiler-lite grows an effect/journal enforcement story, this is untouched. |
| `--apex-id` vs `--apex` drift | Maybe, if CLI bindings are truly generated from one source | This is one of the few real wins. But only if launcher scripts, docs, workflow args, and CLI command descriptors all consume the same contract. If only YAML routes are linted, nope. |
| Lint drift / tests not auto-discovered | Hurts unless CI discovery is fixed first | Adding Roslyn analyzers and golden tests gives us more things to silently rot. The synthesis acknowledges gates but does not answer the discovery failure class hard enough. |
| M4/M10 routing gotchas: catch-all ordering and cycle-back infinite loop avoidance | Partially, but not by "shape" alone | Exhaustiveness catches missing cases. It does not automatically catch "unconditional catch-all not last" unless route ordering is explicitly modeled. It does not prove liveness/no infinite loop. |
| CI module install hazards: missing `if: always()` | No | Not a workflow-contract problem. A compiler for conductor YAML does not lint GitHub Actions unless you broaden the compiler into CI policy lint. Don't claim this. |
| Version bumps: 15 YAMLs + index together | Maybe, if version/index generation is in scope | A generator could centralize versions. Compiler-lite as described does not obviously own release-cut semantics. This needs a concrete "workflow family version manifest" feature, not vibes. |
| `Console.SetOut` races | No | Test concurrency hygiene. Compiler irrelevant. |
| `dotnet build src/Polyphony` not compiling tests | No, maybe worsens | Compiler-lite adds build-time machinery, making "I built the wrong thing" more expensive. Unless CI/local commands are fixed, this is another footgun. |
| ADO 4000-char PR description limit | No, unless contract includes platform constraints | Shape says "string." Operator pain is remote platform legality. The synthesis explicitly says C# cannot prove remote ADO legality. Correct. |
| `-Platform` means PR platform, not tracker | Maybe weakly | A typed enum `PrPlatform` helps at call sites, but not semantic confusion unless names/docs are fixed and launcher args share the same contract. |
| Policy half-wired: fields exist but runtime ignores them | No, unless effect reachability is enforced | This is the warning label for the whole proposal. Typed shape can exist and still be dead. |
| Drift/integration coupling: force-with-lease SHA before rebase | No | This is git-effect sequencing. Compiler-lite does not understand background fetch lease weakening. |
| Workflow naming drift: prefixes renamed, lints stale | Helps if lints resolve actual graph names | Yes, if node IDs and route references are checked from one graph model. But if lint files remain separately authored, we replay the same rot. |
| Too many workflows/nodes/topology coupling | Somewhat | Contract lint helps edges. It does not reduce workflow count, node count, or topology coupling unless paired with matrix collapse / script-to-verb / workflow simplification. |

So: compiler-lite helps the **stringly seam drift** class. It does not fix the production burns that were really about **effects**, **tooling drift**, **CI gaps**, **release coupling**, and **operator ambiguity**.

## The synthesis overclaims unless "lint" means more than field existence

The deck is honest in places: C# proves shape, not truth. Good. But then it still risks selling "route validation" as if that would have saved several recent incidents.

Example: the M4/M10 catch-all bug.

```yaml
routes:
  - when: "true"
    to: some_generic_handler
  - when: "{{ node.output.action == 'specific_case' }}"
    to: specific_handler
  - to: defensive_catch_all
```

A field-existence lint says all fields exist. An enum-exhaustiveness lint may even say all variants are covered. The bug is **ordering semantics**: a bare unconditional route shadows the rest. That requires a route-table semantic analyzer with rules like:

```text
WF_ROUTE_007:
  Unconditional route must be last unless explicitly marked terminal-shadow.
```

And for M10 cycle-back, the problem is not just "missing catch-all." It is "this loop needs a defensive escape route or we can spin forever." That is liveness-ish. The compiler can enforce a local convention; it cannot prove the workflow terminates.

Same with `if: always()` in `ci.yml`. If the synthesis implies compiler-lite would catch that class, no. That is GitHub Actions policy lint, not conductor workflow lint. Add it if you want, but be honest: now you are building a broader repo governance tool.

## New pain: generated YAML is another damn operational surface

The deck says generated artifacts must remain first-class. Correct. But generated artifacts are not magically legible just because we check them in.

New pain introduced:

```text
C# contract changed
→ source generator emits YAML
→ pretty-printer changes ordering
→ golden diff explodes
→ reviewer cannot tell semantic change from formatting churn
→ operator at 11pm edits YAML anyway
→ next build overwrites the fix
```

That is not hypothetical. That is how generated artifacts behave unless the formatter is brutally deterministic and the source-map story is boringly excellent.

If Daniel is firefighting a stuck run at 11pm, he needs one of these to be true:

1. the generated YAML is safe to hand-edit and rerun as an emergency override, with a clear "dirty artifact" marker; or
2. there is a command that patches the C# source and regenerates the exact YAML; or
3. the compiler is not on the critical path for emergency workflow repair.

The synthesis says "build-time, not JIT" and "checked-in YAML during migration." Good start. But it needs an explicit incident-mode rule:

```text
Emergency rule:
  Checked-in emitted YAML may be hand-patched for a single run.
  The run records artifact SHA + dirty flag.
  Follow-up PR must reconcile source contract or delete patch.
```

Without that, compiler-lite makes the operator ask, "Am I allowed to touch the file that conductor actually runs?" That is unacceptable during a live incident.

## The rot test: the three gates are only as real as discovery

The deck mandates three pilot gates:

1. C# unit tests on builder/analyzers
2. golden-diff tests on emitted YAML
3. harness execution against real conductor YAML

That is the right bar. It is also exactly the kind of thing Polyphony has already proven it can forget to wire.

The recent lint drift was not "we had no lint." It was worse: there were lint tests, and CI did not auto-discover them. Then names changed, lints rotted, and production confidence was fake.

So the question is not "will the compiler have tests?" The question is:

```text
Can a new compiler lint/test be added in a path CI does not discover?
Can a workflow name/route rename happen without updating the typed registry?
Can generated contracts exist but not be consumed by the harness?
Can a golden file be stale but blessed because nobody ran the right target?
```

If the answer to any of those is yes, we are just adding a more sophisticated rot layer.

Minimum non-negotiable fix: compiler-lite must ship with a **test discovery sentinel**. Something like:

```text
CI must fail if:
  - any *.Tests.ps1 under .conductor/registry/tests is not executed
  - any workflow YAML is not covered by envelope lint
  - any generated contract is not referenced by at least one lint/harness fixture
  - any analyzer package version differs from workflow generator version
```

Otherwise the typed contract registry will rot exactly like the old lint files, except now it will rot wearing a nicer jacket.

## The "I just need to ship" test

This is where the deck is closest to right and still not done.

Build-time emission is the only sane option. JIT generation would be a crime against postmortems. Keeping conductor as runtime is correct. Keeping YAML visible is correct.

But source generators and analyzers are build-time dependencies. That means compiler-lite can turn "fix one route" into:

```text
edit contract
restore packages
build generator
run analyzer
regenerate YAML
golden diff
run harness
then maybe dispatch
```

That might be acceptable for planned work. It is poison for firefighting unless there is an explicit bypass.

Daniel's current operator loop is ugly but direct: grep YAML, patch YAML, run conductor/polyphony, observe. Compiler-lite must preserve that loop. If it replaces it with "open Visual Studio and debug the Roslyn generator," reject it on operational grounds.

The right split:

- normal path: C# contract → generated/linted YAML → harness
- incident path: YAML hotfix allowed, recorded, reconciled later
- release path: generated and handwritten artifacts cannot diverge silently

If the synthesis cannot say that plainly, it is still thinking like an engine author, not an operator.

## The half-wired policy parallel: typed shape can still be dead

The policy failure is the perfect stress test. `mode`, `quality_threshold`, and `max_fix_loops` exist in `PolicyConfig`, but runtime ignores them. Shape exists. Effect absent. Operator gets burned.

Compiler-lite has the same failure mode:

```csharp
[MutatesResource(ResourceKind.Branch)]
[RequiresApproval("merge")]
public sealed record MergeFeatureBranch;
```

Looks great. Does anything enforce it? Does conductor care? Does the harness assert the branch mutation happened through the journal? Does reset consume the same resource declaration? Does the runtime block mutation outside the declared resource set?

If not, this is policy theater.

Same for agent contracts:

```csharp
public sealed record PlannerOutput
{
    public required IReadOnlyList<PlannedChild> Children { get; init; }
}
```

Okay. The agent returned children. Are they stable across replans? Are IDs unique? Are dependencies acyclic? Are child work item branches cleaned up by reset? Are oversized PR descriptions truncated before ADO rejects them? Shape does not answer any of that.

The synthesis correctly says "effect is proven by harness/postconditions/journal." But the proposed D14 scope mostly proves shape. That is fine if we price it as shape. It is not fine if we sell it as solving the burns that were effect failures.

## New versioning and lockstep pain

Today, the operator already has the "all 15 workflow YAMLs + index.yaml must bump together" problem. Compiler-lite adds more lockstep surfaces:

- contract DTO package version
- generated enum overlay version
- analyzer version
- YAML emitter version
- conductor schema compatibility version
- golden file baseline version
- harness fixture version
- workflow index version

If those do not move together, you get a new class of "green but wrong." For example:

```text
Workflow YAML generated by Compiler 1.4
Analyzer package still at 1.3
Harness fixture assumes Conductor schema 2.2
index.yaml says workflow version 2.5.0
runtime dispatches old checked-in YAML
```

That is the same family as `--apex` versus `--apex-id`: two surfaces drift because nobody made one of them authoritative.

Compiler-lite needs a manifest:

```yaml
compiler:
  version: 1.4.0
conductor_schema: 2.2.0
workflow_catalog: 2.5.0
generated_at_commit: abc123
contracts_hash: ...
analyzers_hash: ...
```

And CI must reject mixed provenance. Otherwise we will invent a fancier release-cut footgun.

## Is this busywork for the engine?

Partly, yes.

If compiler-lite is "field existence, route enum, child input completeness," that is useful but not transformational. It catches dumb drift. Good. Dumb drift is real.

But Daniel's recent pain was not mostly dumb drift. It was:

- reset says success while leaving landmines
- CLI and launcher disagree
- CI silently skips tests
- route semantics have edge-case traps
- policies exist but do not execute
- platform args mean one thing to humans and another to code
- git lease safety depends on sequencing nobody can infer from shape
- releases require synchronized bumps across too many files

A typed wrapper around YAML does not fix the engine's operational trust problem. It may reduce one source of noise while leaving the real wound open.

So the question is not "is compiler-lite good?" It is "is this the highest-leverage next 5–9 PRs compared to hardening reset/journal/effects/CI discovery/release manifests?" I am not convinced from this deck alone. The deck's own cost model says the provable wins are contract wins. Fine. Then do not sell it as the answer to the week that just happened.

## Where the synthesis is actually right

Credit where due: the synthesis avoids the worst version of the idea.

It rejects JIT generation. Good. JIT would make on-call worse.

It keeps conductor as runtime. Good. Replacing conductor would be a platform rewrite wearing a compiler hat.

It targets agent contracts before full workflow authorship. Good. The `architect → write-plan → seed-children` chain is exactly the kind of duplicated contract mess that should be collapsed.

It insists prompts stay human-authored with generated contract fragments. Good. Prompt prose is not a type problem.

It names the limits: no proof of agent honesty, no concrete resource ownership closure, no remote ADO legality. Good. That honesty needs to survive the roadmap.

It includes tripwires for wrapper-tax, diff noise, source-generator hazards, schema fabrication, and Trojan-horse DSL creep. Good. But tripwires only matter if someone is willing to stop when they fire.

## What I would require before approving D14

D14 is close, but I would qualify it with operator-grade conditions:

1. **No generated-only execution path.** The exact YAML conductor runs must be inspectable and archived per run.
2. **Emergency YAML patch path.** Hand-editing emitted YAML must be allowed for incident response, marked dirty, and reconciled later.
3. **CI discovery sentinel.** CI must prove all lint/analyzer/harness test files are discovered, not merely present.
4. **One provenance manifest.** Compiler, analyzer, conductor schema, workflow index, generated artifacts, and contract hashes move together.
5. **No effect claims in v1.** `[MutatesResource]`, capability metadata, and policy fields are documentation unless runtime/harness enforcement exists. Label them accordingly.
6. **M4/M10 route semantics explicitly modeled.** Catch-all ordering and loop defensive routes are first-class lint rules, not assumed covered by exhaustiveness.
7. **CLI/launcher binding included.** If `--apex` versus `--apex-id` remains possible, the contract registry is not reaching enough surfaces.
8. **Stop-go based on real incidents caught.** Not "we wrote analyzers." Show defects caught in live corpus that would otherwise have escaped.

## Verdict: QUALIFY

Compiler-lite is not bullshit, but it is also not salvation; it catches route/field/contract drift while leaving reset residue, CI discovery rot, half-wired policy, git-effect sequencing, and platform limits mostly untouched. The synthesis is right to reject JIT, preserve conductor, keep YAML visible, and focus on agent contracts, but it needs harder operator guarantees around emergency edits, provenance, test discovery, and "shape is not effect." **QUALIFY** — approve only the narrow compiler-lite slice with explicit kill criteria and no permission to pretend it fixed the engine.
