# Squad Decisions

## Active Decisions

### 2026-05-28 — Initial team cast: Classical Composers (10 specialists)

- **Decision:** Cast 10 named specialists from the Classical Composers universe (custom — added to `.squad/casting/policy.json`), plus Scribe and Ralph from the standard scaffold. Total: 12 members.
- **Rationale:** Polyphony's surface area decomposes into ~10 distinct seams (architecture, mission, testability, conductor mechanics, agent prompts, .NET/C#, PowerShell, workflow YAML, twig/ADO, git/worktrees). One specialist per seam keeps reviews clean, ownership unambiguous, and routing trivial.
- **Owner mapping:** Bach (Architect) · Beethoven (Mission) · Brahms (Testability) · Mahler (Conductor Expert) · Stravinsky (AI Agents) · Mozart (.NET/C#) · Liszt (PowerShell) · Wagner (Workflow Author) · Sibelius (Twig/ADO) · Reich (Git/Worktrees).
- **Universe rule:** Names are persistent identifiers, not role-play. Charters carry professional Voice leaning into archetypal traits (rigor, vision, meticulousness); no composer-speech patterns.
- **Approved by:** Daniel Green ("Yes, hire this team").

### 2026-05-28: Squad-wide initial concerns review (10-agent fan-out)

### 2026-05-28: Bach — top architectural concerns

**Top concerns (focus on seams, load-bearing contracts):**

1. **Verb output schema registry deferred — contract gap between CLI and workflow YAML.**
   - **Why it matters:** Workflow YAML references verb outputs by Jinja path (`{{ agent.output.foo.bar }}`), but no compile-time check ensures the path exists, is nullable-safe, or matches what the CLI actually emits. Bugs #6 (field-name drift) and #8 (silent null-elision via `DefaultIgnoreCondition = WhenWritingNull`) surface only at runtime, typically after 30+ minute dogfood cycles. This seam crosses `src/Polyphony/PolyphonyJsonContext.cs` (source of truth for wire shapes) ↔ `.conductor/registry/workflows/` (consumers).
   - **Evidence:** `docs/decisions/verb-output-schema-registry.md` lines 1–28 describe proposed backfill of `[VerbResult(typeof(X))]` attributes on ~50 Command methods. ADR is "Proposed" (line 3), not shipped. No lint yet corresponds to ADR #175 (line 27).

2. **Process-config state-name specificity vs guidance mismatch — template-agnostic code references template-specific names.**
   - **Why it matters:** `process-config.yaml`'s `transitions:` map uses ADO state NAMES (right side), not categories — e.g., `scope_removed: Removed`. But `Removed` is invalid in ADO Basic (`twig2/tests/Twig.TestKit/ProcessConfigBuilder.cs:80-84` shows Basic = {To Do, Doing, Done} only). The canonical config hardcodes `Removed` everywhere (`.polyphony-config/process-config.yaml:25, 30, 34`). When a workflow calls `polyphony validate --event scope_removed` and gets `target_state: "Removed"`, then `twig state Removed` against a Basic project, it fails silently at runtime. ConfigValidator.cs warns on file existence but doesn't validate state names against template constraints. This seam crosses `src/Polyphony/Configuration/ProcessConfig.cs` (type/facet/transition loading) ↔ `.polyphony-config/work-item-types/` (guidance — free-form text, no schema) ↔ ADO (template-specific state sets).
   - **Evidence:** `docs/polyphony-architecture.md` lines 233–263 (§The footgun); `docs/polyphony-process-config-schema.md` lines 244–263. V-21 validation rule mentioned (line 36 of architecture.md) but not enforced by ConfigValidator — no equivalent code path in `ConfigValidator.cs` line 1.

3. **Twig CLI response parsing fragility — seeder depends on undocumented response contract.**
   - **Why it matters:** `PlanCommands.SeedChildren.cs` lines 36–53 parse `twig new` JSON response via three fallback paths (direct `id` field → `url` field → `message` text) because responses have failed to carry structured IDs in dogfood. The seeder lacks a contract defining what twig guarantees. If twig changes its response shape, the seeder fails with no warning. This seam crosses `Polyphony.Commands` (CLI shell-out + JSON parsing) ↔ `twig` library + CLI (execution boundary, owned by Sibelius).
   - **Evidence:** `src/Polyphony/Commands/PlanCommands.SeedChildren.cs:36-53` shows regex fallbacks and comments citing observed dogfood race (AB#3075, 2026-05-11). The state-effects catalog (`docs/polyphony-state-effects-catalog.md`) documents twig verb pre/post conditions but not response shape guarantees.

**Short-term actionable wins (≤1 week):**

1. **Backfill `[VerbResult(typeof(X))]` attributes on all ~50 Command methods.**
   - **Scope:** Mechanical annotation pass. One-time ~4h work, contained in one PR. Unblocks verb-output-schema-registry ADR #173 shipping.
   - **Owner suggestion:** Mozart (.NET/C# expert).
   - **Expected value:** Schema registry can be generated; Jinja path lint (#175) becomes unblocked; eliminates field-name drift bugs from the 30-min-dogfood cycle.

2. **Document twig CLI response shape contract in state-effects catalog.**
   - **Scope:** Extend `docs/polyphony-state-effects-catalog.md` § Verbs with explicit response schema for `twig new`, `twig set`, `twig state` (guaranteed fields, nullable semantics, error cases). Cross-reference the code that parses each response.
   - **Owner suggestion:** Sibelius (Twig/ADO seam expert).
   - **Expected value:** Seeder response parsing becomes auditable; future twig changes can be checked against contract; if response shape is unstable, opens conversation with Sibelius about stabilization.

**Medium/long-term investments (weeks to months, high confidence):**

1. **Implement Jinja template output-path resolver lint (ADR #175 companion).**
   - **Scope:** Once verb-output-schema-registry ships, build a lint that parses all workflow YAMLs, extracts every `{{ agent.output.X.Y.Z }}` path, and checks it against the registered schema. Flag undefined paths and unsafe null-dereference patterns. Integrate into CI.
   - **Why confident:** Schema registry contract is locked in (verb-output-schema-registry.md). The lint is a straightforward path-walk over the generated artifact.
   - **Risk if skipped:** Keeps the current "ship YAML, dogfood for 30 min, hope the Jinja paths work" cycle alive. Every workflow change is high-friction.

2. **Upgrade process-config validation to catch template-specific state-name errors at load time.**
   - **Scope:** Extend ConfigValidator to accept `process_template` + `states:` table and validate that every state name in `transitions:` exists in the template's state set. Ship per-template config templates in `.polyphony-config/scaffold/`. Document the template-specificity rule prominently in process-config-schema.md.
   - **Why confident:** Twig already has the state-set definitions (Twig.TestKit:48-96). ConfigValidator has the model (ProcessConfig.cs). One model class + one validation rule.
   - **Risk if skipped:** Latent bug class — valid-looking configs that fail at runtime only when seeder or workflow calls `twig state`. Hits operators mid-run, not at onboarding time.

**Cross-seam dependencies / overlaps with other squad members:**

- **Twig contract (concern #3)** overlaps with **Sibelius** (git/worktrees/twig seam). twig is the execution boundary; any response-shape change needs coordination.
- **Process-config template specificity (concern #2)** overlaps with **Wagner** (workflow YAML author) and **Beethoven** (mission/onboarding). Workflows trust the config; new repos need clear guidance on per-template setup.
- **Verb schema registry (concern #1)** involves **Mozart** (CLI/C#) and **Mahler** (conductor mechanics + output routing). The schema registry is generated from C#; Mahler owns the Jinja template that consumes the outputs.


### 2026-05-28: Beethoven — top mission concerns

The mission is "human-assisted automated SDLC" — deterministic routing for everything except judgment-heavy steps. I've reviewed the workflow YAMLs (`.conductor/registry/workflows/`), the phase roadmap, and the active plan surface. Below are findings and recommendations from the Mission Keeper's vantage.

---

## Top concerns (2-3, from my seam):

1. **Deterministic error handling is wasting human attention.** The `on-error-migration-inventory.md` documents 19 `*_error_gate` nodes across 6 workflows, describes all 19 as "trivial" (retry/abort logic), yet they're surfaced as human gates where operators must choose. These gates contain zero judgment-calls — they're deterministic retry/abort decisions that belong in conductor's `on_error:` routing, not in a workflow step that asks a human. Current: `ado-pr.yaml` lines 505-531 (`poll_error_gate`), `actionable.yaml` line 870-918 (`workflow_error_gate`), et al. Impact: humans are clicking through deterministic re-routing prompts that could be handled automatically. Fix: migrate all 19 gates to conductor on_error declarations per the AB#3257 brief, closing the gap between mission ("human gates for judgment") and reality ("human gates for logistics").

2. **Phase 5 (DU preview adoption) is misdirected.** DU adoption is nice-to-have type safety for the Polyphony internal model (routing decision outcomes), but the mission does not require it — the workflows deliver correct behavior today. Meanwhile, "polyphony-self-contained-orchestration" (the actual mission outcome: making polyphony a consumable github-registry WITHOUT external script dependencies) is buried in a draft user-plan. Current: Phase 5 marked "in progress" but self-contained orchestration is not on the roadmap. Impact: effort is flowing to internal hygiene instead of external consumability. The architecture still requires consumers to fetch external scripts (`polyphony/scripts/` and `.conductor/registry/scripts/`) — this blocks the "true github registry" outcome. Fix: Elevate "self-contained orchestration" to an explicit post-Phase-4 phase, and defer or de-prioritize DU adoption until after that mission outcome is delivered.

3. **The policy layer is incomplete and its scope is ambiguous.** The workflows reference `pending_review_gate_policy_router` which reads some policy (e.g., `policy.unattended.review_wait_mode`), but the full policy surface is not documented, and it's unclear whether consumers can configure it or whether it's hardcoded per workflow. The "polyphony-self-contained-orchestration" user-plan proposes a full `policy.yaml` config layer with scoped settings (by root, by type, defaults), but that's a future vision. Current: human gates exist (`pending_review_gate`, `scope_violation_gate`, `stuck_review_gate`), but their behavior is not fully configurable by consumers — polyphony tells you WHEN a gate fires, but not HOW you can customize its thresholds. Impact: consumers cannot drift policy (approval caps, PR review retry limits, concurrency) from polyphony's defaults without forking workflows. Fix: Document the current policy surface fully and decide: either (a) defer policy customization to v0.2 with clear roadmap messaging, or (b) implement the minimal scope-driven policy layer (`defaults` + `by_root`) now so that self-contained orchestration can deliver consumability.

---

## Short-term actionable wins (1-2, ≤1 week):

1. **Migrate trivial error gates to conductor on_error declarations (AB#3257).** All 19 gates are pure retry/abort routing with no human judgment. Move them out of human_gate nodes and into `on_error:` declarations on the steps that can fail. This is a straight migration with no design risk — conductor already supports it, and the inventory already defines the target state. Unblocks mission alignment: humans will only see gates where they actually make a decision. File: `.conductor/registry/workflows/{ado-pr, github-pr, actionable, implement-merge-group, remedy-stale-descendant}.yaml`.

2. **Resolve scope and sequencing of "self-contained orchestration" phase.** The user-plan is well-reasoned but needs to be elevated to roadmap status with clear sequencing vs Phase 5. Decision: Is self-contained orchestration Phase 5 (DU adoption deferred) or Phase 6 (DU adoption in parallel)? Once sequenced, document it in `docs/projects/polyphony-self-contained-orchestration.plan.md` (promoting it from user-plan → official plan) with clear phase gates (what must be true before this starts). Files: `.squad/decisions.md`, `docs/projects/polyphony-self-contained-orchestration.plan.md`.

---

## Medium/long-term investments (1-2, weeks to months):

1. **Deliver self-contained orchestration (polyphony as a true github registry).** After Phase 4 (validation/testing) completes, the next mission outcome is consumability — operators should be able to `conductor registry add polyphony --source github://PolyphonyRequiem/polyphony`, install the polyphony binary, and invoke `conductor run polyphony@polyphony` without copying scripts into their repo. This requires: (a) migrating all `.conductor/registry/scripts/` logic into polyphony CLI verbs (new commands or enhancements to existing ones), (b) the policy layer design (from concern #3 above), (c) testing across ≥2 external repos. Blocks: Phase 5 sequencing decision (above). Files: `polyphony-self-contained-orchestration.plan.md`.

2. **Establish policy surface and consumer customization story.** Once self-contained orchestration is sequenced, design the minimal policy config that lets consumers customize gate behavior per root or type (approval caps, retry limits, concurrency, gate suppression). The conductor workflows already have partial policy routing (`pending_review_gate_policy_router`); this investment completes that story and documents the contract. This is a mission enabler: "human-assisted SDLC" means humans can customize WHEN and HOW gates fire. Blocks: self-contained orchestration (requires policy surface to be first-class, not ad-hoc). Files: `.conductor/registry/`, `.polyphony-config/policy.yaml` schema, documentation.

---

## Cross-seam dependencies / overlaps:

- **Stravinsky (AI Agents)** owns the architect + reviewer agents that power planning and PR review gates. If human gates are configurable per-type or per-policy, those agents need to understand the policy context (e.g., "this root has auto_approve=true"). Coordination: Share the policy schema with Stravinsky early so agent prompts can be policy-aware.
  
- **Wagner (Workflow Author)** owns the YAML refactoring and script logic. The error-gate migration (trivial win above) and the script-to-verb migration (self-contained orchestration) both touch Wagner's domain. Coordination: Wagner should review the on_error migration inventory and the self-contained orchestration design together to avoid rework.

- **Bach (Architect)** owns the overall type-agnostic hierarchy model. Phase 5 (DU adoption) is a Bach→Mozart collaboration, but Phase 5 should be sequenced after self-contained orchestration is clear. Coordination: Confirm sequencing decision with Bach before Phase 5 lands.

---

## Summary

The workflows are solid and human gates are in place where judgment matters. However, three gaps block the mission from being fully realized: (1) deterministic error-handling is asking humans for re-routing decisions that belong in the engine, (2) Phase 5 is pursuing internal type-safety instead of external consumability, and (3) policy customization is not yet a first-class feature. The top priority is migrating trivial error gates out of human attention, then sequencing self-contained orchestration before pursuing further internal refactoring.


### 2026-05-28: Brahms — top testability concerns

**Top concerns (2-3):**
1. **Fixture lifecycle drift**: `tests/lint/fixtures/verb-output-schemas.json` is locked, but the live `artifacts/verb-output-schemas.json` is NOT regenerated on every build. If a verb result-field changes but the lock fixture isn't refreshed, `lint-jinja-resolver.Tests.ps1` will pass (using stale schema) while real workflows fail. The RefreshPolicy is manual—Brahms owns refreshing it, but there's no automated CI gate preventing a verb change from shipping without a fixture refresh. Build `src/Polyphony.SchemaExporter/Polyphony.SchemaExporter.csproj` first, then copy to `tests/lint/fixtures/verb-output-schemas.json` — but this step is not wired into the build pipeline.

2. **Harness scenario coverage has silent gaps**: The harness README explicitly defers "non-default human gate routing" and "sub-workflow path coverage" (section "What this harness does not yet cover"). These are **architectural paths that could regress after the MVP and never be caught**. The `--skip-gates` auto-pick behavior is a developer convenience, not a gate boundary. There's also no validation that new agent paths added to a workflow are covered by *any* scenario; a new agent invocation in a workflow can ship untested on the CI gate.

3. **`.conductor/registry/tests/*.Tests.ps1` discovery is fragile**: There are 40+ test files in that directory. The CI job `lint-ci-discovery.Tests.ps1` checks for orphans, but it only validates "invoked by name OR deferred in the script's `$DeferredTests` map." If a `.Tests.ps1` file is added but the CI step that invokes it isn't also added, the discovery lint will catch it — BUT only if someone runs discovery checks. If that check itself fails silently (e.g., Pester module import fails earlier in the job), orphaned tests can rot without warning.

**Short-term actionable wins (1-2):**
1. **Wire verb-output-schema regeneration into the build**: Add a post-build step to `src/Polyphony.SchemaExporter` that overwrites `tests/lint/fixtures/verb-output-schemas.json` when schemas change (MD5 hash comparison). Fail the build if the fixture is stale. This converts a manual refresh into an automatic, testable invariant. Affects: xUnit test fixture lifecycle, lint-jinja-resolver.Tests.ps1, all verb changes.

2. **Add a harness scenario validator to CI**: Before running harness scenarios, walk `.conductor/registry/workflows/` and assert every `agents: { <name>: ... }` appears in at least one `tests/harness/scenarios/*/scenario.yaml`. This surfaces untested agent paths immediately. Lightweight Pester check; can live as a separate `lint-harness-agent-coverage.Tests.ps1` under `.conductor/registry/tests/`.

**Medium/long-term investments (1-2):**
1. **Deferred harness paths (PR #X tbd)**: Sub-workflows and non-default gate routing are design-for-testability blockers. Before any workflow lands with a sub-workflow node or a custom gate selection path (not `--skip-gates`), the harness must have the infrastructure to exercise both. This is a conductor-engine boundary seam, not a polyphony verb problem — but it's a load-bearing gap in the test surface. Conductor PR scope tbd; polyphony harness driver updates tbd.

2. **Lint fixture versioning & schema stability**: The `artifacts/verb-output-schemas.json` and locked fixtures should live in version control with a clear refresh policy. Consider: (a) Bake the fixture into the binary release as an embedded resource so offline lint is possible, (b) Add a `--schema-version` flag to lint tools so lint can detect and warn about schema skew, (c) Document the refresh cadence in `SKILL.md` so team collaborators don't regress this after Brahms's initial work.

**Cross-seam dependencies / overlaps:**
- **Mozart** (C# implementation) owns verb result-types and schema exporter; Brahms owns fixture refresh automation.
- **Wagner** (workflow author) owns workflow YAML definitions; Brahms owns scenario coverage validation.
- **Mahler** (conductor expert) owns the engine; Brahms will need to collaborate once conductor PR lands for gate/sub-workflow DI.
- **CI pipeline** (.github/workflows/ci.yml, Mozart + Brahms + Liszt): Currently the discovery check passes (all known .Tests.ps1 files are invoked), but the fixture refresh and harness scenario validator are not yet gated.


### 2026-05-28: Liszt — top PowerShell concerns

**Top concerns (2-3):**

1. **`git -C` in the production launcher (Invoke-PolyphonySdlc.ps1:833).**
   `$remoteUrl = & git -C $mainWorktree remote get-url origin 2>&1` is a direct violation of the cardinal rule. On Windows, `git -C` with a path that contains spaces produces unreliable results and can silently fail to detect the remote URL — which causes the platform auto-detection to fall back to `'ado'` without warning, or emit a multi-line error string into `$remoteUrl` that is then treated as a URL. The fix is `Push-Location $mainWorktree; $remoteUrl = & git remote get-url origin 2>&1; Pop-Location`. Every other git call in the launcher avoids `git -C`; line 833 is the one survivor. Also note: the `$resetPlatform` detection block (line 322) calls `& git remote get-url origin 2>&1` from the operator's cwd — that one is fine, since the operator's cwd is already verified as a worktree (Phase 1). The asymmetry between the two code paths should be documented.

2. **Heredoc injection surface in `$childCommand` and `$resetChild` (lines 1044–1070 and 429–456).**
   Both blocks build a PowerShell script fragment as a `@"..."@` here-string and pass it to `pwsh -Command`. `$conductorCmd` is embedded verbatim (line 1055 / 440) after being assembled by a single-quote quoting loop (lines 1033–1036 / 424–427). If any conductor argument — most dangerously `$Comment` from `-Intent reset`, or a path segment containing a backtick — survives quoting and touches the here-string boundary, the child window opens with a syntax error and `$exit` is never assigned. The exit sidecar then records `"exit_code": null` with no operator-visible diagnostic. The `-File` pattern (write child script to a `.ps1` scratch file, pass `-File $scriptPath`) eliminates this entire class. The `$Comment` parameter is the most reachable attack surface today: it is passed straight to `"comment=$Comment"` at line 357 with no newline or backtick sanitization.

3. **`git -C` in test scaffolding (`*.Tests.ps1`) cements a bad pattern.**
   `Bootstrap-BareRepo.Tests.ps1:53–56`, `Migrate-ToBareRepo.Tests.ps1:43–46`, and `Sync-BareRepo.Tests.ps1:44–45` all use `git -C $seed` and `git -C $op` for fixture setup. These are test-only, so they don't gate production runs, but they normalize the pattern inside this repo. When a future contributor adds a production helper and glances at the test files for idioms, they see `git -C` used freely and assume it is acceptable. `Migrate-ToBareRepo.Tests.ps1:282` explicitly calls out that `git -C <bare>` would fail — but doesn't extend the concern to ordinary non-bare paths. All `*.Tests.ps1` fixture setups should use `Push-Location`/`Pop-Location` to be consistent with the production contract.

---

**Short-term actionable wins (1-2):**

1. **Fix `Invoke-PolyphonySdlc.ps1:833`.** Replace:
   ```powershell
   $remoteUrl = & git -C $mainWorktree remote get-url origin 2>&1
   ```
   with:
   ```powershell
   Push-Location $mainWorktree
   try { $remoteUrl = & git remote get-url origin 2>&1 }
   finally { Pop-Location }
   ```
   This is a one-liner fix, carries no test surface change, and closes the only surviving `git -C` in production code. Zero seam dependencies — it runs after init-root has already set `$mainWorktree`, and the failure mode it prevents (silent wrong-platform detection) is invisible today but guaranteed to trigger on any Windows path with spaces.

2. **Sanitize `$Comment` before heredoc embedding.** In the reset path (lines 357, 424–456), reject or strip newlines and backticks from `$Comment` before it is interpolated into `$resetCmd` and then into `$resetChild`. A one-liner guard at parameter-bind time suffices: `if ($Comment -match '[\r\n`]') { throw "[polyphony-sdlc] -Comment must not contain newlines or backticks." }`. This closes the most reachable injection surface without requiring the larger `-File` refactor.

---

**Medium/long-term investments (1-2):**

1. **Refactor `$childCommand` / `$resetChild` from `-Command` string injection to `-File` script delivery.**
   Write the child script block to a named `.ps1` file in `$logDir` (alongside the transcript and exit sidecar), then spawn `pwsh -NoExit -NoProfile -File $scriptFile`. This eliminates the entire heredoc-injection class for any input: paths with apostrophes, backticks, dollar signs, parentheses. The transcript and sidecar paths are already absolute and known before spawn — they can simply be passed as named parameters to the `-File` script rather than baked into a string. This work is non-trivial (the child script depends on eight+ interpolated values) but the correctness gain is structural, not cosmetic.

2. **Audit + convert `*.Tests.ps1` fixture setup to `Push-Location`/`Pop-Location`.**
   Replace all `git -C $seed`, `git -C $op`, and `git -C $Worktree` calls in the four test files with `Push-Location`/`Pop-Location` wrappers. Primary motivation: eliminate the pattern from the codebase entirely so there is no ambiguity about what is acceptable. Secondary: the tests currently pass on the dev machine but could fail in CI if the Windows git version or repo path contains non-ASCII characters or spaces. Low risk to existing tests; moderate effort (~30 call sites).

---

**Cross-seam dependencies / overlaps:**

- **Reich (Git/Worktrees):** The heredoc → `-File` refactor (medium-term #1) touches the `$logDir` location, transcript naming, and exit-sidecar schema. Reich owns the per-run worktree layout doc (`docs/per-run-worktree-layout.md`) and should review any changes to the child-process spawn contract to ensure the layout doc stays in sync.
- **Mahler (Conductor Expert):** The `$Comment` injection risk (concern #2, short-term win #2) originates from conductor workflow inputs reaching the launcher's reset path. Mahler should confirm whether `reset-root@polyphony` can ever produce a `comment` value with embedded newlines from a human gate or default template — if so, sanitization is needed upstream at the workflow boundary, not just in the launcher parameter.
- **Wagner (Workflow Author):** The `Sync-BareRepo.ps1` dry-run display strings (lines 427, 430, 476, 482) show `git -C <worktreePath>` as the *displayed* command, even though the script itself uses the `Invoke-Git` helper. These display strings could mislead operators into running the displayed command directly and hitting the reliability issue. Wagner should decide whether the display strings should show the `Push-Location`-equivalent form instead.
- **Bootstrap-BareRepo.ps1 and Sync-BareRepo.ps1 are deprecated** (both carry top-of-file DEPRECATED warnings). Test coverage maintenance cost (concern #3) should be weighed against eventual removal rather than retrofitting. Coordinate with Beethoven (Mission) before investing in those test files.


### 2026-05-28: Mahler — top conductor-mechanics concerns

**Top concerns (2-3):**

1. **Output-schema contracts are undersized on cross-leg boundaries.**
   Root-item-dispatch bubbles multiple schemas across the batch dispatcher (root-batch-dispatch) which then rolls them up to polyphony. The guards are defensive (`is defined`), but the *shapes themselves* mismatch where they shouldn't:
   - root-item-dispatch.output emits `lifecycle_workflow` as a string and renegotiation fields (request/verdict/scope_violation_files) with mixed null-coalescing (some use `| default()`, others raw). When plan-level doesn't run, renegotiation_request emits an *empty string* (`{%- else -%}{%- endif -%}`), not null or false. This works because root-batch-dispatch re-guards it, but it's fragile.
   - root-batch-dispatch.output.renegotiation_items arrives as a JSON string (line 127: `aggregate_renegotiation.output.renegotiation_items | default('[]')`), but polyphony must parse it as a string-encoded JSON array to iterate. The contract is "JSON as a string field," not "structured array" — this cascades cognitive burden to every consumer.
   - Cite: root-item-dispatch.yaml lines 157–168; root-batch-dispatch.yaml lines 117–156; polyphony.yaml lines 246–248.

2. **Route conditions are doing work that scripts should own.**
   The branch-on-router pattern (root-item-dispatch.yaml lines 212–265) is clean for dispatch, BUT the aggregator script in root-batch-dispatch (lines 300–417) is manually parsing conductor's `for_each` error shape to emit `items_failed_count` and `failed_items`. This parsing logic should be a *route condition on root-batch-dispatch* (routing based on whether ANY errors exist), not embedded in PowerShell. Similarly, polyphony's outer_loop_evaluator (lines ~1300+) is doing multi-batch aggregation in a script; that's a strong signal that conductor's sub-workflow output chaining needs structural help (e.g., a *shared aggregator node* that polyphony calls, rather than inline scripts).
   - Cite: root-batch-dispatch.yaml lines 300–417 (aggregate_renegotiation script); polyphony.yaml lines 1300–1400 (outer_loop_evaluator script).

3. **Re-entry stability is sound, but node-ID pinning is brittle in the face of refactor.**
   All three lifecycle workflow nodes in root-item-dispatch (plan_level, actionable, implement_merge_group) must be wired and named correctly — *any* typo or refactor breaks the branch-on-router pattern. There's no schema validation at the workflow-load level that ensures the `when:` clauses match the node names. A minor example: line 255 routes `to: plan_level` when `classify_lifecycle.output.lifecycle_workflow == 'plan-level'` — if the node or the output string diverges, the route silently fails and the item hits the catch-all error terminal. Node IDs are stable (PR #136 design), but *discovering* the mismatch requires either test execution or manual review.
   - Cite: root-item-dispatch.yaml lines 250–265 (branch-on-router routes).

**Short-term actionable wins (1-2):**

1. **Normalize renegotiation output schema: null instead of empty string, emit structured JSON, not JSON-as-string.**
   In root-item-dispatch.output (lines 157–168), change the `else` clauses to emit `null` (or omit the field entirely via Jinja) instead of empty string for renegotiation_request. In root-batch-dispatch.output (line 127), emit renegotiation_items as a structured array, not a JSON string — this lets polyphony iterate natively without string→JSON parse, and makes the YAML schema more readable.
   - **Effort:** ~1 hour. Impacts: root-item-dispatch, root-batch-dispatch, polyphony route conditions (update `when:` clauses to handle null/array shapes).
   - **Blocker for:** polyphony's renegotiation gate (currently blind to the true JSON shape).

2. **Add a dedicated conductor node for error aggregation in root-batch-dispatch.**
   Extract the for_each error parsing from aggregate_renegotiation (lines 355–408) into a separate `aggregate_errors` script step that routes BEFORE `aggregate_renegotiation`. Let `record_batch_failure_flag` (line 444) route on `aggregate_errors.output.any_failures`, not on a hardcoded check of `items_failed_count`. This decouples error surfacing from renegotiation aggregation and makes the dispatch failure path explicit in the YAML topology.
   - **Effort:** ~45 min. Impacts: root-batch-dispatch output schema (add explicit `errors_detected` field).
   - **Blocker for:** clarity on the fail-fast-across-waves pattern.

**Medium/long-term investments (1-2):**

1. **Refactor sub-workflow dispatch to use a shared registry + route-factory pattern.**
   The branch-on-router mechanism (route-on-a-string-enum) works but doesn't scale to 6+ lifecycles or dynamic addition. Consider a conductor *extension* (if supported by the engine) or a separate polyphony-level dispatch table that maps classifications to sub-workflow paths. This would move the node-name-pinning risk upstream (to a config file, not the YAML) and let lifecycle workflows be added without editing root-item-dispatch's routes.
   - **Rationale:** Reduces coupling between the classifier and the workflow topology; enables lifecycle plugin architecture.
   - **Effort:** ~2–3 days (requires conductor engine inspection for extensibility; may defer if engine doesn't support dynamic workflow loading).

2. **Build a conductor YAML linter that validates branch-on-router routes against node names and output schemas.**
   A pre-flight tool (run before `conductor deploy`) that:
     - Parses the YAML, extracts all `when:` clauses on routes, and checks that the node names and output field names exist.
     - Validates that every sub-workflow's `input_mapping` keys match the sub-workflow's declared `input:` schema.
     - Warns on empty `else` fields in output Jinja templates (signal to use null).
   This would catch re-entry bugs before runtime.
   - **Rationale:** Branch-on-router + multi-stage sub-workflow dispatch are now the canonical pattern; the risk of typos-to-silent-failures is material.
   - **Effort:** ~1–2 days (write linter in Python or C#, integrate with the conductor scaffold).

**Cross-seam dependencies / overlaps:**

- **Wagner (Workflow Author)** owns the branch-on-router pattern write-up (skill: polyphony-workflow-author). The renegotiation schema normalization (short-term win #1) touches that pattern; coordinate with Wagner before implementing.
- **Mozart (.NET/C#)** owns the lifecycle classification logic (`lifecycle-router.ps1` and its underlying `polyphony state next-ready` call). The branch-on-router pattern depends on the *stability* of the output keys (lifecycle_workflow, error_code, etc.). If Mozart refactors the classifier output, all routes in root-item-dispatch must be re-validated.
- **Liszt (PowerShell)** owns the aggregator scripts (aggregate_renegotiation, outer_loop_evaluator). Short-term win #2 extracts error aggregation from his code; coordinate to ensure the new node schema plays well with the rest of his envelope logic.
- **Polyphony overall:** The three-file dispatch split (polyphony → root-batch-dispatch → root-item-dispatch) is load-bearing (forced by conductor's `for_each` constraint — one thing per iteration). The medium-term sub-workflow registry pattern would require Polyphony CLI changes to surface lifecycle URLs or a new `dispatch_table` field in `polyphony state next-ready` output. Coordinate with Mozart on the CLI side.


### 2026-05-28: Mozart — top engine/CLI concerns

**Top concerns (2-3):**

1. **Schema artifact staleness is a silent trap.**
   `VerbSchemaGenerator` is an incremental Roslyn source generator that bakes
   `VerbOutputSchemaCatalog.Json` as a compile-time constant (`VerbSchemaGenerator.cs`,
   `EmitCatalog` path). The `SchemaExporter` writes this constant to
   `artifacts/verb-output-schemas.json` only during `AfterBuild`. The `artifacts/`
   directory does not exist in the current workspace — meaning any test run that
   skips the exporter build (e.g. `dotnet test --no-build` against a clean clone)
   either fails or reads a stale artifact. `VerbCatalogSanityTests` depend on this
   file being current. There is no CI freshness guard; if a developer adds a verb,
   forgets to rebuild SchemaExporter, and commits, the artifact goes stale silently.

2. **`required: false` (schema) vs `HaltIfMissing` (runtime) — undocumented contract gap.**
   `VerbSchemaGenerator.ExtractInputs()` (`VerbSchemaGenerator.cs` line 336) sets
   `Required: !param.HasExplicitDefaultValue`. Every sentinel parameter
   (`int workItem = RequiredInput.MissingInt`, `string organization = ""`) has an
   explicit default, so it correctly emits `required: false`. The comment at lines
   317–321 acknowledges this as intentional. However, no field in the schema output
   records which `false`-required inputs are *runtime-required* via `HaltIfMissing`.
   Downstream tooling — Brahms's `JsonOutputContractTests`, Liszt's PowerShell
   call-site linter, Mahler's conductor workflows — can only see "optional" and must
   infer runtime-required status from documentation rather than machine-readable
   signal. This is a latent correctness risk as the verb catalog grows past ~100 verbs.

3. **`ManifestCommands.cs` is a 57 KB god-file accumulating all manifest mutations.**
   Unlike `PrCommands.*.cs` (which correctly splits each verb into its own partial
   file), `ManifestCommands.cs` (57,225 bytes) holds all manifest verbs plus the
   shared `ResolveManifestPathAsync` and `ValidateManifestRoot` helpers in one
   file. Any behavioural change to those shared helpers silently affects all 10+
   verbs. The `default_legacy` fallback path (`ManifestCommands.cs` ~line 127) is
   a Stage-8 todo that is easy to miss because it's buried in a large file; Stage 8
   cleanup is likely to generate merge conflicts if multiple agents touch the same
   region.

**Short-term actionable wins (1-2):**

1. **Add a schema-artifact freshness assertion to `VerbCatalogSanityTests`.**
   After each build, compute a SHA-256 over `VerbOutputSchemaCatalog.Json` (the
   in-memory constant, accessible from the test assembly via an internal accessor)
   and compare it to a SHA-256 over `artifacts/verb-output-schemas.json`. A mismatch
   fails the test with a clear message: "verb-output-schemas.json is stale — rebuild
   Polyphony.SchemaExporter." This catches the stale-artifact race in both CI and
   local dev without adding any new tooling.

2. **Tombstone the dead `planRoot` parameter in `StateCommands.NextReady`.**
   `StateCommands.NextReady.cs` line 47: `string planRoot = "docs/projects"` is
   immediately discarded (`_ = planRoot;`). The parameter appears as an `optional`
   input with a non-sentinel default in the emitted schema, making it look live to
   workflow authors and lint tools. Add `[Obsolete("Reserved for backward-compat;
   ignored by the verb body since PlanObserver took over plan_authored. Will be
   removed in Stage 8.")]` to the parameter (or at minimum the summary XML tag) so
   downstream tooling can flag workflow callers that still pass `--plan-root`.

**Medium/long-term investments (1-2):**

1. **Introduce a `[RuntimeRequired]` annotation to close the schema contract gap.**
   A custom `[RuntimeRequired]` source-generator attribute applied to sentinel
   parameters would let `VerbSchemaGenerator.ExtractInputs()` emit an additional
   `runtime_required: true` field alongside `required: false`. This gives Brahms's
   contract tests and Liszt's workflow linter a machine-readable signal that the
   verb will `HaltIfMissing` on the parameter at runtime, without breaking CAF's
   expectation of `required: false`. Requires changes to `InputInfo`, `Models.cs`,
   and the `EmitCatalog` JSON template — medium effort, high signal value.

2. **Split `ManifestCommands.cs` into per-verb partial files.**
   Apply the `PrCommands.*.cs` pattern: one partial file per verb (e.g.
   `ManifestCommands.Init.cs`, `ManifestCommands.Append.cs`, etc.), with
   `ManifestCommands.cs` reduced to the primary constructor, shared helpers
   (`ResolveManifestPathAsync`, `ValidateManifestRoot`), and the `[VerbGroup]`
   declaration. This is a pure refactor with no behaviour change; it makes per-verb
   blame and review trivial, and it isolates the Stage-8 `default_legacy` dead-code
   removal to a single file without merge-conflict risk.

**Cross-seam dependencies / overlaps:**

- **Brahms (testability):** `VerbCatalogSanityTests` and `JsonOutputContractTests`
  are the primary consumers of the stale-artifact risk (concern #1) and the
  `required: false` gap (concern #2). Any new `[RuntimeRequired]` attribute must be
  co-designed with Brahms so the contract-test scaffolding (`CommandTestBase`) can
  assert both sides.

- **Liszt (PowerShell):** PowerShell call sites for `polyphony state next-ready`
  currently pass `--plan-root` in some workflow scripts. Removing the tombstoned
  parameter in Stage 8 requires Liszt to audit and clean up those call sites before
  the parameter is dropped. Brahms's `JsonOutputContractTests` will be the
  regression guard.

- **Mahler (Conductor):** `StateCommands.ValidateInputs` (`StateCommands.ValidateInputs.cs`
  line 58) reads `required: true` from conductor workflow YAML input schemas — this
  is the *workflow*-level required flag, not the verb's schema `required`. The two
  concepts must not be conflated: Mahler owns the YAML `required: true` declaration;
  Mozart owns the verb body's `HaltIfMissing`. The `[RuntimeRequired]` annotation
  investment (medium-term) would make this boundary explicit.

- **Sibelius (twig/ADO):** `PrCommands.*Ado.cs` use `IAdoClient`
  (`Polyphony.Infrastructure.AzureDevOps`) as a direct ADO write channel separate
  from twig CLI. This is a deliberate, annotated exception to the polyphony-never-
  writes-to-ADO invariant (PR merge/create/vote operations are out of twig's scope).
  If Sibelius updates the ADO REST API version or auth model in twig, `IAdoClient`
  is an independent surface that must be kept in sync. Worth a joint review at each
  twig major release.


### 2026-05-28: Reich — top git/worktree concerns

**Top concerns (2-3):**

1. **Promotion-gate enforcement gap in phase 6** — The ADR `docs/decisions/branch-model.md` § Promotion gating mandates driver-enforced ordering: impl PRs merge before nested MG PRs, nested MGs before top MGs, top MGs before feature PR. The compose chain in `src/Polyphony/Commands/StateCommands.NextReady.cs` correctly gates based on requirement dispositions, but there is **no explicit audit** that merged branches actually follow the topology order in git ancestry (squash merges at the impl-PR layer mean `git merge-base --is-ancestor` can lie). This is by design per the ADR, but the lack of observability into "did we actually promote in order?" is a blind spot for debugging failed runs. **Action:** Add a lightweight validation in `polyphony state next-ready` that cross-checks the PR merge order against the manifest's merge_groups topology when verbose logging is enabled.

2. **Zombie worktree accumulation under concurrent abort/reset cycles** — `worktree-manager.ps1 -Operation teardown` calls `git worktree remove --force` (line 262), which is idempotent on success, but `git worktree remove` can fail transiently on Windows when a nested .git process still holds a file lock. The retry logic in `GitWorktreeDeleter.cs` (line 37–64, four retries with delays) handles this for the C# reset flow, but the **ad-hoc teardown from the conductor teardown_worktree node** (root-item-dispatch.yaml line 368) uses the plain ps1 script with no retry loop — it exits 0 on failure (line 267) so the conductor never knows the worktree is still there. A human reset via `Invoke-PolyphonySdlc.ps1 -Intent reset` will clean up the orphan, but a failed run followed by immediate retry leaves the sibling-worktree cleanup to the next reset. **Action:** Thread the same retry-with-backoff logic into `worktree-manager.ps1` teardown so transient lock contention doesn't leave behind orphaned trees.

3. **State-location amendment (Rev 4.2) not fully integrated into manifest validation** — The branch-model ADR `docs/decisions/branch-model.md` § State location amendment (Rev 4.2) moves the run manifest from `feature/{root}:.polyphony/run.yaml` to `<git-common-dir>/polyphony/{root_id}/run.yaml` to fix stale-state-bleed across worktrees. `src/Polyphony/Manifest/RunManifestStore.cs` loads from the right path, but **there is no migration code and no deprecation path** for existing runs that have the manifest in the old location. A dogfood run that was started pre-Rev-4.2 and then resumed post-4.2 will create a second manifest at the new path while the old one grows stale on the feature branch, causing topology-hash divergence. **Action:** Add a migration check in `RunManifestStore.Load` that detects old-location manifests, emits a diagnostic (not an error), and prefers new-location if both exist. For fresh runs, stamp the creation timestamp in the manifest so old-location readers can detect which is current.

**Short-term actionable wins (1-2):**

1. **Document the promotion-gate observability gap** — Add a section to `docs/glossary.md` explaining that "promotion order" is enforced by the driver's requirement-disposition gating, not git ancestry (because squash merges break ancestry), and add a twig-command to the polyphony CLI to dump the canonical merge order from the manifest for manual audit. This closes the current documentation gap and gives operators a way to verify runs are progressing correctly without adding code complexity. (1–2 hours)

2. **Add retry logic to worktree-manager.ps1 teardown** — Backport the `RemoveWithRetryAsync` delay-and-backoff pattern from `GitWorktreeDeleter.cs` into the PowerShell teardown path. This eliminates the most common cause of orphaned worktrees on Windows. (30 min)

**Medium/long-term investments (1-2):**

1. **Automatic zombie-worktree GC on every reset + weekly background cleanup** — Extend `Invoke-PolyphonySdlc.ps1 -Intent reset` to call `polyphony worktree gc` at the end (which already exists but is not wired into the reset flow). Add an optional nightly cron job in Invoke-PolyphonySdlc that runs `gc` on any `polyphony-runs/` subtree older than 24 hours (keyed by feature/{root} branch existence). This prevents stale trees from accumulating across multiple reset cycles. (3–5 hours)

2. **Topology hash cross-check on every manifest load** — Add an optional `--audit` flag to `polyphony state` that re-hashes the manifest's MergeGroups and compares to the stored TopologyHash. Wire this into the CI harness so every test-run's manifest round-trips cleanly. This catches corruption early and prevents subtle divergence between the manifest and git state from going undetected. (4–6 hours)

**Cross-seam dependencies / overlaps:**

- **Mozart** (C# impl of RunManifest) owns the manifest schema and serialization; **Reich** owns the git topology that the manifest reflects. When Mozart bumps the schema version or adds audit fields, the migration path falls on both. Proposal: add a `.squad/decisions/inbox/mozart-manifest-changelog.md` that Mozart updates whenever schema changes; Reich reviews for git-state implications.

- **Wagner** (Workflow Author) owns the teardown_worktree node definition and PowerShell wiring. When Wagner changes the workflow's `teardown_worktree` node contract (e.g., adding a `force_gc` parameter), that change must flow through to `worktree-manager.ps1`. Proposal: add a workflow-parameter audit comment in `worktree-manager.ps1` marking which parameters are driven by the conductor, so Wagner knows the blast radius of schema changes.

- **Sibelius** (Twig/ADO) owns the run-started-at watermark tag that's written by the reset flow. The tag's presence/absence is part of git state (even though it's stored as a twig tag). When Sibelius changes tag-reading logic or the watermark format, Reich needs to know so the reset flow and manifest don't drift. Proposal: Sibelius documents the run-started-at tag contract in a `docs/glossary.md` § Tags section, with examples of the wire format.

- **Per-run worktree model** depends on `polyphony worktree init-root` returning the correct `runs_root` path. If that path resolution gets out of sync with where the launcher actually places worktrees, teams will see "worktree not found" errors at scale. Proposal: add a diagnostic verb `polyphony worktree verify-paths` that walks the tree and reports any branches/manifests that are orphaned or in unexpected locations.

---

**References:**
- `docs/decisions/branch-model.md` (Rev 4.2): Promotion gating, MG identity, state location amendment
- `docs/decisions/per-run-worktree-model.md`: Filesystem isolation, same-root lock, worktree lifecycle
- `docs/decisions/run-reset.md`: Watermark, observer filter, proactive cleanup
- `src/Polyphony/Manifest/RunManifest.cs`: TopologyHash, MergeGroups, RebaseRecord, manifest schema
- `src/Polyphony/Journal/Reset/Deleters/GitWorktreeDeleter.cs:37–64`: Retry-with-backoff pattern
- `.conductor/registry/scripts/worktree-manager.ps1:254–272`: Teardown logic (no retry)
- `.conductor/registry/workflows/root-item-dispatch.yaml:368`: Teardown node definition
- `.squad/agents/reich/charter.md`: Charter and ownership boundaries


### 2026-05-28: Sibelius — top twig/ADO concerns

**Top concerns (2-3):**

1. **State transition durability gap — no post-state `twig sync` in dispatch workflows**
   
   The `polyphony.yaml` and per-item dispatch workflows (e.g., `root-item-dispatch.yaml`, `implement-merge-group.yaml`) shell out to `twig state <target>` to transition work-item state, but **do not follow with a `twig sync`** to flush the staged change back to ADO. This creates a window where:
   - The local twig cache sees the new state.
   - ADO has NOT been updated yet (the change sits in twig's local store).
   - A parallel dispatch in another worktree reads from stale ADO state, not aware of the in-flight transition.
   
   **Cite:** `.conductor/registry/workflows/root-item-dispatch.yaml` shell-out blocks; `implement-merge-group.yaml` lines where `twig state` is called without a trailing `twig sync`. The durability pattern is documented in `polyphony.yaml` comments (lines ~345–370) but not enforced in child workflows.
   
   **Fix:** Every `twig state` + `twig sync` sequence needs a post-state `twig sync` to guarantee ADO sees the change before the next step.

2. **Per-worktree `.twig/config` area-path staleness — no validation at worktree clone**
   
   Per-worktree `.twig/config` is inherited from origin/main (as documented in `.squad/agents/sibelius/history.md`). If the origin `.twig/config` has outdated area paths, all worktrees clone that stale config. Later corrections committed to the feature branch don't automatically propagate back to the origin branch.
   
   Common symptom: `declare_root` fails with ADO TF51011 because the worktree's area path is stale or invalid. The operator then has to manually correct the worktree's `.twig/config`, commit, and retry.
   
   **Cite:** `.squad/agents/sibelius/history.md` ("Per-worktree `.twig/config` is inherited from origin/main at worktree creation… edits do NOT propagate"). No pre-flight validation in the current workflows checks that the active workspace's area paths are correct before dispatching.
   
   **Fix:** Add a preflight check (e.g., `twig workspace list` + validate against `.polyphony-config/process-config.yaml` or a known-good area-path list) to catch stale configs before work items are created.

3. **IAdoClient used for PR operations only — but boundary is undocumented**
   
   `IAdoClient` (e.g., `src/Polyphony/Infrastructure/AzureDevOps/AdoClient.cs`) is wired for PR mutations (vote, comment, close) and PR reads. This is **not** used for work-item writes (those go via `twig state` + sync). However, the boundary between "what IAdoClient owns" and "what goes via twig" is not explicit in the code, ADRs, or inline documentation.
   
   **Risk:** A future contributor might assume IAdoClient can also mutate work items and bypass twig for a "quick" state change or tag update. This violates the twig-only-writes invariant.
   
   **Cite:** `PolyphonyServiceRegistration.cs` registers both `AddTwigCoreServices()` (line 42) and `services.AddSingleton<IAdoClient, AdoClient>()` (line 115) with no comment distinguishing their boundaries. `IAdoClient` contract shows only PR operations (ListPullRequests, CreatePullRequest, SetPullRequestVote, etc.), but this pattern is not enforced by the type system.
   
   **Fix:** (1) Document the boundary in `PolyphonyServiceRegistration.cs` or an ADR. (2) Rename `AdoClient` to `AdoPullRequestClient` or add a suppressible analyzer rule to reject work-item mutations via HttpClient outside twig.

**Short-term actionable wins (1-2):**

1. **Audit all `twig state` calls in workflows — add post-state `twig sync`**
   
   Grep for `twig state` in all `.conductor/registry/workflows/` files. For every match, verify the next step is `twig sync`. If not, insert it. This fixes concern #1 and closes the durability gap immediately.
   
   **Cite:** `.conductor/registry/workflows/` — specifically `root-item-dispatch.yaml`, `implement-merge-group.yaml`, and any agent prompts that advise `twig state` without sync.

2. **Harden preflight sync — add a retry loop and catch-log for transient ADO failures**
   
   The `preflight_sync` step in `polyphony.yaml` (line 268–278) runs `twig sync` but catches all errors with a silent route-continue. If ADO is rate-limited or temporarily 5xx, the preflight cache is stale for the entire dispatch.
   
   **Fix:** Wrap `twig sync` in a retry loop (exponential backoff, max 3 attempts) before the silent catch. Log failures so the operator sees warnings like "ADO was slow; proceeding with up-to-60s-stale cache."
   
   **Cite:** `.conductor/registry/workflows/polyphony.yaml` lines 268–278.

**Medium/long-term investments (1-2):**

1. **Formalize the twig ↔ ADO boundary as an ADR and interface contract**
   
   Create `docs/decisions/twig-ado-boundary.md` that explicitly states:
   - Twig CLI owns work-item state mutations (state, note, link, seed-publish).
   - IAdoClient owns PR/PR-comment mutations only (no work-item writes).
   - All ADO reads go via twig's library (Twig.Infrastructure) or are cached with explicit sync discipline.
   - Enforcement: Code review checklist + lint rule to reject direct `HttpClient` POST/PATCH to `/workitems/` endpoints.
   
   **Cite:** Current charter is in `.squad/agents/sibelius/charter.md` but is not cross-referenced from design docs or enforced in code.

2. **Add per-worktree area-path validation middleware to the dispatch loop**
   
   Before each worktree's first work-item create, call a pre-flight validator that runs `twig workspace` and confirms the active area paths exist in ADO and match a known-good schema. If mismatch, emit a clear error ("Worktree area path is stale; run `twig workspace add` on the feature branch and retry") and block dispatch.
   
   **Cite:** The common TF51011 footgun is documented in `.squad/agents/sibelius/history.md` but no automation prevents it yet.

**Cross-seam dependencies / overlaps:**

- **Wagner (Workflow Author):** The `twig state` + post-state `twig sync` pattern needs to be baked into the conductor-workflow best-practices or enforced via a schema linter. Wagner owns workflow authorship; I own the twig invariant. We need aligned guidance on which workflows need to inject sync and where.
  
- **Mozart (.NET/C#):** The `IAdoClient` boundary is architectural. If Mozart is adding new work-item mutations, they must go via twig CLI shell-out, not IAdoClient. This needs a review gate in code review.
  
- **Reich (Git/Worktrees):** Per-worktree `.twig/config` inheritance is a git+twig seam. When a worktree is cloned, its `.twig/config` is inherited from origin/main. Reich owns the worktree lifecycle; I own the twig config discipline. We should align on when/how to validate or regenerate config at clone time.

- **Conductor (Mahler):** The workflows orchestrate twig CLI calls but don't have explicit retry/rate-limit policies for ADO transients. Mahler owns the conductor execution engine; I own the twig reliability spec. We need aligned guidance on retry-on-ADO-5xx vs. fail-loud semantics for `twig sync`.


### 2026-05-28: Stravinsky — top agent-design concerns

**Top concerns (2-3):**

1. **Missing agent-guidance files for non-architect/coder/reviewer roles.** The failure-modes document mentions "architect, planner, implementer, reviewer, merger" roles, but only 3 guidance files exist (`architect.md`, `coder.md`, `reviewer.md`). The conductor workflows (e.g., `plan-level.yaml` line 356) invoke `architect_agent`, and `actionable.yaml` line 325 invokes `actionable_agent`, but there's no explicit role-name mapping in the guidance system. If new roles are added (e.g., `implementer`, `merger`, `planner`-as-distinct-from-architect), they will have no guidance files unless we explicitly create them. The **lookup contract** (`.polyphony-config/agent-guidance/README.md` lines 31-46) describes the fallback gracefully to missing files, but this is silent degradation—agents will run without role-specific tuning.

2. **Facet-profile facet-to-type distribution may be too restrictive.** Current sizing: Epic→`[plannable]`, Issue→`[plannable, implementable]`, Task→`[implementable, actionable]`. The `actionable` facet exists only at Task level, but workflows route *any* item carrying the facet through `actionable.yaml`. If an Issue is ever marked actionable (or if operatorsintend Epics to be actionable), the facet profile will not compose correctly. The process-config does not enforce this constraint; it's a latent design assumption. Recommendation: clarify whether facets should be assignable at any type level (type-agnostic) or strictly reserved for their declared types.

3. **Agent prompts lack explicit stopping conditions and error-handling contracts.** The `actionable_agent` prompt (actionable.yaml lines 340–420) declares `output: { summary: string }` but does not specify what happens if the agent:
   - Produces invalid JSON
   - Returns a JSON object without a `summary` key
   - Exceeds the evidence-branch token budget
   - Fails to commit artifacts
   The prompt says "Return a JSON object" but has no error enum, no fallback schema, and no routing that inspects whether the output validates. Compare to the verb-output contracts in C# (e.g., `BranchEnsureEvidenceResult.Action`), where routes explicitly check for `action == 'error'`. Agent output is not validated the same way—it's consumed as-is by the PR renderer.

**Short-term actionable wins (1-2):**

1. **Create per-type guidance refinements for architect and coder roles.** The guidance system supports `architect/<typeslug>.md` (README line 20) to add type-specific tuning. For Epic planning, Issue decomposition, and Task implementation, type-specific guidance would immediately improve prompt conditioning without breaking existing guidance. Example: `.polyphony-config/agent-guidance/architect/task.md` could emphasize "fit one PR group (~2000 LoC)" per the existing `architect.md` but add task-specific constraints from process-config (e.g., "Tasks are implementable+actionable; no planning children"). This is a low-risk fill that surfaces existing constraints buried in YAML.

2. **Add explicit stopping conditions and error contracts to `actionable_agent`.** The prompt should state:
   - Output must be valid JSON with required `summary` field (string, non-empty)
   - If evidence branch is empty after work, summarize what was NOT produced and why
   - If work fails (e.g., branch checkout error caught downstream), the prompt's closing instruction should be "Summarize the error you encountered and the evidence of the attempted work."
   - Add a JSON-schema note: "Invalid JSON will cause workflow failure; double-check your output before returning."
   This is a rephrase-and-validate cycle; no code changes yet.

**Medium/long-term investments (1-2):**

1. **Decouple guidance loading from prompt-text injection and establish a structured addendum surface.** Currently, the composer outputs skills/MCPs/guidance as strings, and the workflow injects them via Jinja2 into prompt text (`compose_addendum` → `actionable.yaml` line 361–394). This works, but:
   - Guidance files can break Jinja2 rendering if they contain `{%` or `{{` unescaped
   - No type-safe validation of guidance before injection
   - Conductor's agent-step schema may eventually support structured addendum (per `actionable.yaml` line 52–57 comment: "TODO: `skills:` / `mcps:` / `prompt_addendum:` fields")
   - Action: Design a validated guidance-composition layer that produces a structured envelope before Jinja2 injection, and write harness tests to verify the envelope survives Jinja2 rendering and prompt injection.

2. **Build a prompt-contract test harness for agent roles.** Each agent role (architect, coder, reviewer, actionable, etc.) should have:
   - An input schema (what the workflow provides to the prompt)
   - An output schema (what the prompt must return)
   - A set of harness tests using FakeProvider (like the polyphony harness does for workflows) that verify:
     - The agent accepts the full input schema without error
     - The agent's output validates against the output schema
     - Stopping conditions are respected (agent does not loop indefinitely)
     - Guidance injection does not break the prompt
   - Action: Brahms should own the test-harness scaffolding (`tests/harness/scenarios/agent-contracts/`); Stravinsky should author the per-role test cases.

**Cross-seam dependencies / overlaps:**

- **Wagner (Workflow Author) ↔ Stravinsky (Agents):** The `guidance_loader` and `compose_addendum` script steps route to agent nodes, but Wagner owns the YAML wiring. Changes to the addendum envelope (e.g., adding a new field like `warnings`) require Wagner to update the Jinja2 injection sites in both `actionable.yaml` and `plan-level.yaml`.

- **Mozart (C# Expert) ↔ Stravinsky:** The `FacetProfileComposer.Compose` implementation is Mozart's domain, but Stravinsky owns the semantics of skills/MCPs deduping and facet-to-role mapping. When deduping fails silently (identical-value collisions dedupe, but conflicting values are not caught), which agent owns the test? Recommend: Mozart owns the deduping logic; Stravinsky owns the test that verifies deduped skills/MCPs actually compose correctly for a real work item.

- **Brahms (Testability) ↔ Stravinsky:** Per my charter, "When I'm unsure, I write a test prompt against the harness's FakeProvider." But Brahms owns the harness scaffolding. Need explicit agreement: Stravinsky writes the test *scenario* (FakeProvider mocking, expected output), Brahms reviews and integrates into the CI harness.

- **Bach (Architect) ↔ Stravinsky:** The three-vocabulary rule (events / state names / categories) is enforced by Bach, but agents are allowed to emit guidance that references state names (e.g., "transition to Done"). If guidance violates the vocabulary, whose job is it to catch it? Recommend: guidance files belong to Bach's vocabulary stewardship; Stravinsky owns guidance-injection **testing** to ensure no architectural invariants are violated by injected text.

- **Role-naming tension:** The squad team has "architect, coder, reviewer" specialists. Polyphony workflows invoke "architect_agent" (in plan-level), "coder_agent" (?—not found), "actionable_agent" (in actionable), and "evidence_reviewer" (in actionable). Are these the same roles? If "architect" is doing planning AND implementation planning, the term is overloaded. Recommend: Bach clarifies the vocabulary—either the agent roles are architecture-neutral (just "planner, implementer, reviewer") or we align the squad roles to the workflow roles explicitly.

---

**Rationale for concerns:**

The agent-prompt system is early-stage (facet-profile composition landed in Phase 6 PR #5 ~2 weeks ago). The three major gaps—missing guidance files, restrictive facet sizing, and prompts without error contracts—are *risks* not yet regressions. Documenting them now prevents silently broken guidance from reaching production, facet-profile misconfigurations from surprising operators, and agent-output schema mismatches from passing validation.

The testing infrastructure (prompt contracts, guidance-injection harness) is the load-bearing investment that will keep these concerns from drifting into issues.


### 2026-05-28: Wagner — top workflow-YAML concerns

**Top concerns (2-3):**

1. **`github-pr.yaml` output map is missing the `already_merged_emitter` branch — silent false-merge on operator-merged PRs.**
   The `merged` output template at `.conductor/registry/workflows/github-pr.yaml:100–103` covers only `pr_merger` and `closed_unmerged_emitter`. But `already_merged_emitter` (line 437) is a fully reachable terminal — reached when `poll_status` returns `already_merged` (the operator merged the PR through the GitHub UI between polls). When that path fires, none of the `merged` branches match and the template falls to `{%- else -%}false` — reporting `merged=false` to `feature-pr.yaml` despite the PR being merged. `feature-pr.yaml` then routes into the remediation cycle (line 484–485) for a PR that no longer exists. The `pr_url` branch at lines 104–107 similarly omits `already_merged_emitter`, so the URL is also silently lost. The `ado-pr.yaml` counterpart correctly handles both `already_merged_emitter` and `treat_as_merged_emitter` at lines 141–142. This is a live schema drift between the two platform legs.

2. **`close-out.yaml` uses `| json` in the workflow-level output map — a non-standard Jinja2 filter that will throw a `TemplateError` at runtime.**
   `.conductor/registry/workflows/close-out.yaml:43`:
   ```
   observations: "{{ close_out.output.observations | json }}"
   ```
   Conductor's Jinja2 environment exposes `tojson`, not `json` (consistent with the rest of the library — every other serialization in the registry uses `| tojson` or `| string | lower`). If `json` is not registered as an alias, the workflow-level output render fails the moment `close-out` is invoked as a sub-workflow and the caller tries to read `observations`. The `observation_count` on line 45 references the same array via `| length` (valid), but the `observations` key itself would be broken.

3. **Partial bubble-up: `validate_scope_verdict` and `scope_violation_files` are declared outputs of `plan-level.yaml` but are silently dropped at `root-item-dispatch.yaml`.**
   `plan-level.yaml:163–183` exports four renegotiation-related fields: `renegotiation_pending`, `renegotiation_request`, `validate_scope_verdict`, `scope_violation_files`. `root-item-dispatch.yaml:157–161` only bubbles up the first two. The scope-violation data is structurally discarded before it reaches `root-batch-dispatch` or `polyphony.yaml`. If the renegotiation gate in `polyphony.yaml` ever needs to surface which files triggered the scope violation, the chain is broken — and the contract mismatch between `plan-level.yaml`'s declared output and what `root-item-dispatch.yaml` actually exposes is undocumented. The emitter scripts at lines 418, 444, 519, 550, 577, 604, 632 each hard-code `renegotiation_pending = $false; renegotiation_request = ''` but omit `validate_scope_verdict` and `scope_violation_files` entirely — consistent with the gap but never explicitly documented as intentional.

---

**Short-term actionable wins (1-2):**

1. **Patch `github-pr.yaml:100–107` to add the `already_merged_emitter` branch.**
   Mirror the `ado-pr.yaml:139–148` pattern:
   ```yaml
   merged: >-
     {%- if pr_merger is defined -%}{{ (pr_merger.output.merged | default(false)) | string | lower }}
     {%- elif already_merged_emitter is defined -%}true
     {%- elif closed_unmerged_emitter is defined -%}false
     {%- else -%}false{%- endif -%}
   pr_url: >-
     {%- if pr_merger is defined -%}{{ pr_merger.output.pr_url | default('') }}
     {%- elif already_merged_emitter is defined -%}{{ already_merged_emitter.output.pr_url | default('') }}
     {%- elif closed_unmerged_emitter is defined -%}{{ closed_unmerged_emitter.output.pr_url | default('') }}
     {%- else -%}{%- endif -%}
   ```
   Low-risk (two extra `elif` clauses), high-impact (prevents false remediation cycles on operator-merged GitHub PRs). `already_merged_emitter.output.pr_url` is set at line 447 from `poll_status.output.pr_url` so the value is always present.

2. **Fix `close-out.yaml:43` `| json` → `| tojson`.**
   One-character change, prevents a TemplateError at the workflow-output-map render step. Confirm at the same time whether `observation_count: "{{ close_out.output.observations | length }}"` (line 45) behaves correctly when `observations` is an array — it should, but if `close_out` is an LLM agent and the array comes back as a JSON string rather than a parsed array, `length` returns string length not element count. If that's the case, consider `| fromjson | length` as a guard.

---

**Medium/long-term investments (1-2):**

1. **Formally document — or close — the `validate_scope_verdict`/`scope_violation_files` bubble-up gap.**
   Either:
   - Thread those two fields up through `root-item-dispatch.yaml:148+` and the emitter scripts at lines 418/444/519/550/577/604/632 to match `plan-level.yaml`'s declared output contract, OR
   - Explicitly annotate `plan-level.yaml:174–183` with `# terminal at root-item-dispatch — not propagated further` and remove the fields from the declared output contract if the renegotiation gate in `polyphony.yaml` genuinely does not need them.
   Either path closes the silent contract mismatch. As-is, the gap looks like an accidental omission rather than a design decision, which means the next person to add a renegotiation feature will re-discover it expensively.

2. **Audit the utility-workflow cohort (`remedy-stale-descendant.yaml`, `restack-remedy.yaml`, `reset-root.yaml`, `root-fallback-gate.yaml`, `research.yaml`) for `output:` completeness and schema-declaration hygiene as a batch.**
   All five have `output:` sections (confirmed by grep), but unlike the core PR-lifecycle workflows they haven't been under the same cross-seam pressure that forced `github-pr.yaml`/`ado-pr.yaml` alignment. A one-pass audit against the M7 output-map rules (guard `is defined`, `| string | lower` on booleans, `| tojson` not `| json`) before any of these are wired into the main dispatch as sub-workflows would catch the same class of bugs found in concern 1 and 2 above before they ship.

---

**Cross-seam dependencies / overlaps:**

- **Mozart (C#):** The `already_merged_emitter` gap (concern 1) originates in `PrPollTerminalRoute.Classify` — Mozart owns the C# classifier that maps GitHub poll state to the `route` field value `already_merged`. The fix is YAML-only, but Mozart should confirm that the `already_merged` route value is being emitted correctly and consistently between the GitHub and ADO poll implementations before the YAML patch ships.
- **Mahler (Conductor runtime):** The `| json` issue (concern 2) is a question of whether the conductor engine registers `json` as a Jinja2 filter alias. Mahler should confirm the exact filter registry before the fix is landed — if `| json` is actually supported, concern 2 is moot (but should still be standardized for readability).
- **Liszt (PowerShell):** The partial bubble-up of renegotiation scope data (concern 3) implicates the emitter scripts in `root-item-dispatch.yaml` (lines 418, 444, 519, 550, 577, 604, 632 — inline pwsh blocks). Threading `validate_scope_verdict` and `scope_violation_files` up requires Liszt to update the emitter output objects.
- **Stravinsky (Agent prompts):** `close-out.yaml`'s `close_out` agent output schema declares `observations[].category` as a free-form string with enumerated examples (`"improvement" | "bug" | "debt" | "process"` — line 100). If the agent prompt diverges from that enum, the observation_count and any downstream filtering will see unanticipated values. Stravinsky should lock the agent schema to a `type: string, enum:` constraint or accept that category values are unvalidated.



### 2026-05-28: Implementation round 1 (short-term wins #527-#530)

#### 2026-05-28T14:08-07:00: User directive — north-star ordering + in-situ hygiene

**By:** Daniel Green (via Copilot)

**What:** Polyphony-self-contained-orchestration (the "true github registry" outcome — consumers run `conductor registry add polyphony` and get the full SDLC without copying scripts) is the mission outcome and takes priority over Phase 5 DU preview adoption. DU adoption and code/process hygiene are NOT deprioritized — they continue "in situ" within whatever work the squad is doing (i.e., opportunistically applied alongside scoped tasks), but they do not warrant a dedicated thrust competing with self-contained orchestration.

**Why:** Daniel resolved the open question surfaced by the 2026-05-28 squad-wide initial concerns fan-out. Beethoven argued (and Daniel agreed) that self-contained orchestration is the consumability outcome the engine exists for; Bach's schema-registry concerns and Stravinsky's prompt-contract concerns implicitly align — all three point at "make the engine usable from outside." DU + hygiene are real quality goods but stay woven into scoped work rather than crowding the headline.

**Implications for routing:**
- Create an umbrella epic "Polyphony self-contained orchestration" framing the 4 medium/long investments as supporting work.
- Every implementation spawn prompt carries a standing in-situ clause: *"Apply DU patterns and hygiene fixes opportunistically where natural in your scope. Don't make these the primary focus, but don't avoid them either."*
- When a feature proposal lands, the routing question is "does this serve self-contained orchestration?" before "does this match Phase 5?".

---

#### Beethoven — Epic Structure Summary

**Created:** 2026-05-28T14:08-07:00  
**Created by:** Beethoven (Mission Keeper)

**Epic 0 — Mission North Star:**
- #521 | Polyphony self-contained orchestration (mission north star) | `squad:epic`, `squad:long-term`

**Epic A — Short-term wins umbrella:**
- #522 | Squad short-term wins (2026-05-28 fan-out) | `squad:epic`, `squad:short-term`

**Child issues (Short-term):**
- #527 | Backfill [VerbResult] attributes + SHA-based schema freshness check | Primary owner: `squad:mozart`
- #528 | Migrate 19 trivial error gates to conductor on_error: (AB#3257) | Primary owner: `squad:wagner`
- #529 | Surgical YAML patches: github-pr.yaml, close-out.yaml, plan-level.yaml + twig sync audit | Primary owner: `squad:sibelius`
- #530 | PowerShell hygiene burndown: kill git -C, sanitize $Comment, backport retry-with-backoff | Primary owner: `squad:liszt`

**Epics B–E — Supporting investments for mission north star:**
- #523 | Workflow-YAML pre-flight linter | `squad:epic`, `squad:medium-term`
- #524 | Twig↔ADO seam: formalize the boundary | `squad:epic`, `squad:medium-term`
- #525 | Agent prompt contracts + harness tests | `squad:epic`, `squad:medium-term`
- #526 | Process-config template-specificity enforcement (V-21) | `squad:epic`, `squad:long-term`

---

#### Mozart — Implementation Delivery: Issue #527 (short-term win)

**PR:** #534 (draft)  
**Branch:** `squad/527-verbresult-attrs`

**Summary:** All 109 `[Command]` methods in `src/Polyphony/Commands/` already carried `[VerbResult(typeof(X))]` attributes — no annotation gap existed at time of implementation. Real work was SDK bump (preview.3→.4), artifact regen, and in-situ DU hygiene (CS0433 IUnion ambiguity fix in 2 test files).

**Files touched:**
- `artifacts/verb-output-schemas.json` — Created (force-added; `artifacts/` is in .gitignore). 276848 bytes.
- `global.json` — SDK pin preview.3 → preview.4
- `tests/Polyphony.Tests/Routing/TransitionValidatorTests.cs` — In-situ DU hygiene: replaced `((IUnion)outcome).Value.ShouldBeOfType<T>()` with `is` pattern matching.
- `tests/Polyphony.Tests/Routing/CrossProcessTransitionValidatorTests.cs` — Same IUnion hygiene; also converted `AssertAccepted` from block to expression body.

**In-situ DU hygiene applied:** `IUnion` identifier was ambiguous (CS0433) between `System.Runtime.IUnion` (new in .NET 11 preview.4) and `Twig.Domain`'s bundled polyfill copy compiled against preview.3. Fixed by switching to native `is` pattern matching, which is the idiomatic .NET 11 union access pattern.

**Blocker note:** Brahms's freshness-check PR (#532) is the second half. Both PRs must merge to close #527. Artifact `artifacts/verb-output-schemas.json` is in `.gitignore` — was force-added.

---

#### Brahms — Implementation Delivery: Issue #527 freshness test

**PR:** #532 (draft)  
**Branch:** `squad/527-schema-freshness-test`

**Summary:** Added `VerbOutputSchemas_ArtifactIsFresh` to `VerbCatalogSanityTests` using in-process SHA-256 normalized comparison. Test is intentionally RED until Mozart's `squad/527-verbresult-attrs` merges (the artifact does not exist yet).

**Approach:** In-process SHA-based comparison. `VerbSchemaGenerator` is a Roslyn source generator (compile-time only) — therefore "in-process" means reading the embedded compile-time constant `VerbOutputSchemaCatalog.Json` and comparing it against the on-disk `artifacts/verb-output-schemas.json`. Both sides are canonicalized (recursive object-key sort + `WriteIndented = true`) before SHA-256 hashing to avoid whitespace-only false positives.

**Environment note:** Local build blocked by SDK mismatch (preview.3 vs preview.4 installed). CI on the PR is the authoritative verification gate.

---

#### Wagner — Implementation Delivery: Issue #528 (short-term win)

**PR:** #535 (draft)  
**Branch:** `squad/528-error-gate-migration`

**Summary:** Removed all 19 trivial `human_gate` error-interrupt nodes across 6 workflow files. Each gate was a pure deterministic error handler with no judgment required. After this PR, every such error routes automatically (16 gates → `abort_run`, 1 gate → `child_router`, 1 gate → `$end`, 1 gate → split routing in actionable.yaml).

**🚨 CRITICAL DISCOVERY: conductor v0.1.18 does NOT ship `on_error:`.** The `on_error:` syntax referenced in the issue is **not implemented** in conductor v0.1.18. Testing confirmed: `agents.0.routes.1.on_error: Extra inputs are not permitted`. The migration is therefore implemented as **direct routing** today, with `# on_error: auto-abort (AB#3257 — ...)` TODO comments at each affected route. When conductor Phase 1 ships `on_error:`, these sites can be retrofitted.

**Note:** Conductor would only see a crash exit as an error; polyphony CLI verbs exit 0 and write `{error: "..."}` to stdout JSON. True `on_error:` support would require verbs to also write to `$env:CONDUCTOR_ERROR_OUT` on failure.

**Behavioral changes:**
- **Retry capability removed** from 14 retry+abort gates until AB#3257 lands. Operators must re-trigger workflows manually on transient failures.
- `seeder_error_gate` was prompt-to-continue; now auto-continues.
- `classify_error_gate` (restack-remedy) was prompt-to-skip-or-retry; now auto-skips to `$end`.

**Coordination required:** This PR touches `github-pr.yaml` and `plan-level.yaml` — both also modified in Sibelius PR #529 (`squad/529-yaml-patches-twig-sync`). These must be merged sequentially with conflict resolution.

**⚠️ MISSION DECISION FOR DANIEL/BEETHOVEN REVIEW:** Wagner's findings (on_error: unavailable, retry removal from 14 gates, manual re-trigger now required on transient failures) require explicit decision on operator experience impact and whether direct routing + TODO comments is acceptable as a interim measure. See Beethoven's mission concerns (#528) and Mahler's conductor-mechanics findings (medium-term investment: "refactor sub-workflow dispatch to use a shared registry + route-factory pattern").

---

#### Sibelius — Implementation Delivery: Issue #529 (short-term win)

**PR:** #533 (draft)  
**Branch:** `squad/529-yaml-patches-twig-sync`

**Summary:** Patch 1+2 applied and lint clean. Patch 3 was already complete (partial-bubble-up concern was stale). Twig sync audit found 0 missing post-state syncs (all 3 sites correct).

**Patches applied:**
1. **`github-pr.yaml` output:** Added `{%- elif already_merged_emitter is defined -%}true` to the `merged` output guard and replaced the `closed_unmerged_emitter.output.pr_url` fallback in `pr_url` with `poll_status.output.pr_url`, matching `ado-pr.yaml:139-148`. Without this, re-entry when the operator had already merged the PR via the GitHub UI produced `merged=false` and an empty `pr_url`.
2. **`close-out.yaml:43`:** Changed `| json` → `| tojson`. (`| json` is not a Jinja2 filter; silent incorrect output depending on conductor version.)
3. **`plan-level.yaml` / `root-item-dispatch.yaml`:** No change needed. Both `validate_scope_verdict` and `scope_violation_files` were already correctly wired in both output blocks.

**Twig sync audit:** All 3 actual `twig state` call sites in `.conductor/registry/workflows/*.yaml` already have a post-state `twig sync` (implement-merge-group.yaml:1218→1219, polyphony.yaml:1425→1426, root-item-dispatch.yaml:498→499). No follow-up issues required.

**Lint:** `lint-github-pr.ps1`, `lint-strict-undefined.ps1`, `lint-plan-level.ps1`, `tests/lint-sync-after-mutation.ps1` — all PASS.

---

#### Liszt — Implementation Delivery: Issue #530 (short-term win)

**PR:** #531 (draft)  
**Branch:** `squad/530-ps-hygiene`

**Summary:** All 3 fixes shipped (git -C kill, $Comment sanitize, retry-backoff backport). AST parse clean, 29/29 Pester tests pass.

**Fixes:**
1. **Fix 1 — `git -C` removal (Invoke-PolyphonySdlc.ps1:833):** Replaced `& git -C $mainWorktree remote get-url origin` with a `Push-Location`/`Pop-Location`-scoped block. This is the last surviving `git -C` in the production launcher (highest-priority item from the 2026-05-28 fan-out).
2. **Fix 2 — `$Comment` sanitization (Invoke-PolyphonySdlc.ps1):** Added `Get-SanitizedComment` helper function that strips `\r` and `\n` characters and escapes backtick characters. Applied at the reset-path entry boundary so all downstream uses receive sanitized input. The live injection surface was line 440 where `$resetCmd` was interpolated into an expandable here-string passed to `pwsh -Command`.
3. **Fix 3 — Retry-with-backoff in teardown (worktree-manager.ps1:254-272):** Backported delay schedule from `GitWorktreeDeleter.cs:RemoveWithRetryAsync` (attempts at 0 ms, 200 ms, 500 ms, 1000 ms). Same "directory already gone = success" short-circuit as the .NET original.

**Patterns established:**
- `Get-SanitizedComment` is the canonical sanitization boundary for `$Comment` before shell embedding.
- Worktree teardown retry shape is now consistent between C# and PowerShell.

---

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
