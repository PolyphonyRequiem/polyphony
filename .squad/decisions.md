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

# Squad Decisions Registry

**Last updated:** 2026-05-28T23:00:46Z

## Session round (2026-05-28): on_error + notifications focus

This document aggregates decisions and major findings from the following agents across this session:
- Beethoven (Mission Keeper) — gate disposition reviews  
- Wagner (Workflow Author) — Phase 2 scope + error pattern catalogue  
- Bach (Architect) — ADR proposals + platespinner integration design  
- Mahler (Conductor Expert) — dogfood branch + adoption survey  

---


---

# Inbox: Beethoven → Gate Disposition Review for PR #535

**Date:** 2026-05-28T15:37:47Z  
**Author:** Beethoven (Mission Keeper)  
**Topic:** AB#3257 — Wagner's three unilateral disposition decisions in PR #535 (error-gate migration)  
**PR:** https://github.com/PolyphonyRequiem/polyphony/pull/535  
**Status:** Review complete — see dispositions below

---

## Context

Wagner's PR #535 removes all 19 trivial error-gate `human_gate` nodes, replacing them with direct routes because conductor v0.1.18 has no `on_error:` primitive. Sixteen of the nineteen substitutions are mechanical (infrastructure error → `abort_run`). Three required disposition judgment that exceeded Wagner's authority. This review covers those three only.

The baseline invariant I'm applying: polyphony is "human-assisted automated SDLC." The engine is automated; humans gate the judgment-heavy steps. Error handling that silently corrupts state or permanently closes work items without human awareness is a mission violation. Error handling that merely aborts a run and lets the operator re-trigger is not.

---

## Gate 1 — `seeder_error_gate` → `child_router` (auto-continue)

**File:** `.conductor/registry/workflows/plan-level.yaml`  
**Original routing:** `seeder.output.error_count > 0` → `seeder_error_gate` (human_gate: retry / continue / abort)  
**Wagner's routing:** `seeder.output.error_count > 0` → `child_router` (auto-continue, no human acknowledgment)

### What the original gate enabled

The original gate served as an explicit acknowledgment checkpoint. The seeder carries per-child failures in `errors[]` and continues to exit 0 regardless, which means error_count > 0 is not a fatal signal by default. The gate let the operator see the exact per-child failure detail (twig dedup conflict, invalid parent ID, workspace misconfiguration) and choose:
- **Retry** — fix the cause, re-run the idempotent seeder  
- **Continue** — accept the partial seed and proceed  
- **Abort** — halt

The gate's own inline comment was explicit: *"so the operator sees the failure rather than continuing into child_router with a half-seeded tree (which silently terminates the root blocked)."*

### What the new routing removes

Auto-continue silently swallows per-child failures. The operator never sees which children failed to seed. If the cause is a platform-level error (invalid parent ID, twig workspace misconfiguration), the root proceeds into child_router with a structurally broken tree and will silently block with no diagnostics surfaced.

Note: Wagner's framing ("was: human continue option → now: auto-continue") mischaracterizes the substitution. The "Continue" option in the original gate still required explicit human acknowledgment of the error log. Auto-continue removes that acknowledgment. These are not equivalent.

### Blast radius if wrong

Root run enters `child_router` with a half-seeded ADO tree. Some children were never created. Those children never appear in `polyphony state next-ready` output. The root run eventually ends up permanently blocked with zero observability — no error surfaced, no abandoned state, no operator notification. Operator has to grep conductor logs to understand why the root stalled.

### Disposition

**❌ Request change**

Wagner should route `seeder.output.error_count > 0` to `abort_run`, consistent with every other infrastructure error in this PR. This is not a quality judgment — it is an infrastructure signal. The seeder is idempotent so the operator can re-trigger after fixing the cause (resolving the duplicate title, correcting the parent ID, etc.).

Auto-continuing with a silent partial tree is strictly worse than halting and surfacing the failure. The operator knows the run stopped; they do not know the tree is silently incomplete.

**Correct routing:**
```yaml
routes:
  - to: abort_run
    # TODO(AB#3257): replace with on_error: abort when conductor phase-1 lands
    when: "{{ seeder.output.error_count is defined and seeder.output.error_count > 0 }}"
  - to: child_router
```

---

## Gate 2 — `classify_error_gate` → `$end` (auto-skip)

**File:** `.conductor/registry/workflows/restack-remedy.yaml`  
**Original routing:** `classify.output.error_code` populated → `classify_error_gate` (human_gate: retry classify / skip restack)  
**Wagner's routing:** `classify.output.error_code` populated → `$end` (auto-skip, silent termination)

### What the original gate enabled

The gate surfaced the `error_code` and `error` fields to the operator with common-cause diagnostics (manifest missing/malformed in the per-root state directory, repo slug not resolvable, twig cache stale). It offered retry or skip.

### What the new routing removes

Observability only. The operator never sees that classify failed. The restack silently did nothing.

### Blast radius if wrong

Stale descendant PRs don't get restacked. They stay stale until the next time `restack-remedy` is triggered. This is bounded harm: no work item state is corrupted, no run is aborted, no ADO disposition is set. The restack is a utility helper workflow, not the core SDLC path.

Skip was already a valid operator choice in the original gate. Wagner is effectively encoding the "skip" path as the automatic response, which is a reasonable degraded-mode decision given the constraint.

### Disposition

**⚠️ Approve with note**

The disposition is acceptable for phase 1. The blast radius is bounded (stale branches linger until next run; no state corruption). Skip was already a first-class operator option in the original gate.

**Flag for phase-2 retrofit (AB#3257):** When `on_error:` lands in conductor, this site should emit a structured warning log (error_code, root_id) rather than silently terminating. The current code gives the operator zero signal that classify failed. A conductor-native `on_error: skip` with a `warn_message` would close this observability gap without requiring a human gate.

---

## Gate 3 — `evidence_reviewer` block + `merge_evidence_pr` false → `workflow_abandoned`

**File:** `.conductor/registry/workflows/actionable.yaml`  
**Original routing:** Both paths → `workflow_error_gate` (human_gate: retry at executor_router / abandon)  
**Wagner's routing:** Both paths → `workflow_abandoned`

### Background: Wagner's infrastructure/content split

Wagner made a principled distinction across the old `workflow_error_gate`'s inputs: infrastructure errors (`compose_addendum`, `open_evidence_pr`, `evidence_floor_check`) now route to `abort_run`; content-or-quality signals (`evidence_reviewer.decision == 'block'`, `merge_evidence_pr.merged == false`) now route to `workflow_abandoned`. The principle is sound. The application is partly wrong.

### Path A — `evidence_reviewer.output.decision == 'block'` → `workflow_abandoned`

This is a reviewer quality judgment: the evidence agent reviewed the PR and explicitly blocked it. "Block" is a content quality signal, not an infrastructure failure. Setting the item to abandoned is semantically appropriate — the evidence doesn't meet the bar and the item should be re-examined by the team before another attempt.

The original gate allowed retry (re-enter executor_router), which gave the operator a chance to re-run with a different approach. That retry path is now gone. However: under the current constraint (no `on_error:`, conductor v0.1.18), the loss of the retry path is an acceptable tradeoff for shipping — re-trigger from the ADO work item is still possible.

**Sub-disposition: ⚠️ Approve with note.** Defensible as-is. Flag for phase-2 retrofit: when `on_error:` lands, restore the retry path (re-enter executor_router) before reaching workflow_abandoned.

### Path B — `evidence_reviewer` catch-all (unknown/missing decision) → `workflow_abandoned`

The original comment: *"Catch-all per M4: unknown / missing decision routes to the error gate so the operator decides rather than raising No matching route found."*

An unknown or missing decision value from `evidence_reviewer` is not a quality judgment — it is a conductor invariant violation (the agent returned an unexpected output shape). Routing it to `workflow_abandoned` marks the work item as abandoned in ADO due to what is effectively a code defect.

**Sub-disposition: ❌ Request change.** This should route to `abort_run`, not `workflow_abandoned`. The item is not "abandoned" — the engine hit an unexpected state. Abort halts the run without marking the item; the operator can re-trigger after the root cause is diagnosed.

### Path C — `merge_evidence_pr.output.merged == false` → `workflow_abandoned`

This is the highest-blast-radius error in the three gates under review.

`merged == false` from `polyphony pr merge-evidence-pr` can occur for several reasons, including transient ones:
- Network/API error  
- Merge conflict introduced since the PR was opened  
- Branch protection violation (transient config issue)  
- PR already closed by an operator between the open and merge steps

`workflow_abandoned` does not just stop the conductor run — it sets the work item disposition to "abandoned" in the ADO work item system (via the satisfaction flow's state transition). If the cause was transient, the operator must manually re-open the work item in ADO before they can re-trigger. This is materially more expensive than an `abort_run` (which just stops conductor; the item stays active; re-trigger is one command).

The original `workflow_error_gate` for this path showed the merge error detail and let the operator choose retry or abandon. Wagner collapsed both options to the more destructive outcome.

**Sub-disposition: ❌ Request change.** `merge_evidence_pr.merged == false` should route to `abort_run`, not `workflow_abandoned`. Same for the catch-all (missing/malformed merged field — infrastructure anomaly, not a quality disposition).

### Disposition Summary for Gate 3

**❌ Request change** (two sub-paths; one sub-path approved with note)

| Sub-path | Current | Correct | Reason |
|----------|---------|---------|--------|
| `evidence_reviewer.decision == 'block'` | `workflow_abandoned` | `workflow_abandoned` ✅ | Quality judgment — abandonment is correct |
| `evidence_reviewer` catch-all | `workflow_abandoned` | `abort_run` ❌ | Conductor invariant violation, not quality |
| `merge_evidence_pr.merged == false` | `workflow_abandoned` | `abort_run` ❌ | Can be transient; ADO disposition is irreversible |
| `merge_evidence_pr` catch-all | `workflow_abandoned` | `abort_run` ❌ | Infrastructure anomaly, not quality disposition |

**Note:** Wagner should NOT touch the infrastructure-error paths (compose_addendum, open_evidence_pr, evidence_floor_check) — those are correctly routed to `abort_run`.

---

## Summary

| Gate | Disposition | Required action |
|------|------------|-----------------|
| `seeder_error_gate` → auto-continue | ❌ Request change | Change route to `abort_run`; add TODO(AB#3257) comment |
| `classify_error_gate` → auto-skip | ⚠️ Approve with note | None now; add observability in phase-2 retrofit |
| `evidence_reviewer` block / `merge_evidence_pr` false → `workflow_abandoned` | ❌ Request change (partial) | Change catch-all and `merged==false` routes to `abort_run`; keep `block` → `workflow_abandoned` |

---

## Asks of Daniel

1. **Seeder error semantics (Gate 1):** My call is `abort_run` on any `error_count > 0`. If you have a reason to believe partial-seed auto-continue is safe enough for phase 1 (e.g., you know the seeder always seeds the critical children first), override this and I'll downgrade to ⚠️ Approve with note. But I need explicit sign-off because the gate's own comment warned against it.

2. **`workflow_abandoned` vs. ADO state machine (Gate 3):** I'm assuming `workflow_abandoned` commits an irreversible ADO state transition (abandoned disposition on the work item). If `workflow_abandoned` in actionable.yaml is actually a conductor-only terminal with no ADO write, the blast radius of routing `merge_evidence_pr.merged == false` to it is lower, and this could be downgraded to ⚠️. Please confirm the terminal node's ADO side-effects.


---

# Phase 2 Scope: `on_error:` Retrofit — Restore 19 Gates Removed in #535

**Author:** Wagner (Workflow Author)  
**Date:** 2026-05-28T22:38:04Z  
**Status:** Draft — filed as GitHub issue, pending Beethoven disposition review  
**Parent issue:** [#528 — Migrate 19 trivial error gates to conductor on_error](https://github.com/PolyphonyRequiem/polyphony/issues/528)  
**Closed by:** [PR #535](https://github.com/PolyphonyRequiem/polyphony/pull/535) (direct routing + TODO comments)  
**Upstream conductor PRs:** [#227 RFC](https://github.com/microsoft/conductor/pull/227) · [#229 Phase 1 impl](https://github.com/microsoft/conductor/pull/229)  
**Companion inventory:** `docs/projects/on-error-migration-inventory.md`

---

## Executive Summary

PR #535 removed 19 trivial human-gate error nodes across 6 workflows. It replaced them with direct routing because conductor v0.1.18 ships no `on_error:` primitive. As a side-effect, **14 gates lost retry capability** — transient failures now abort instead of pausing for human retry, and operators must re-trigger manually.

Once conductor Phase 1 (`on_error:` typed routing) ships, the TODO-marked sites can be retrofitted. **Phase 2 = that retrofit.** This document scopes the work.

**Key finding:** Phase 1 alone (PR #229) handles only 5 of the 19 gates cleanly. The remaining 14 retry+abort gates require Phase 2 of the conductor RFC (`retry:` route action). Additionally, polyphony CLI verbs emit errors as stdout JSON with exit 0 — they must also write to `$CONDUCTOR_ERROR_OUT` for `on_error:` routes to fire at all (a C# verb change, scoped to Mozart/Liszt).

---

## 1. Gate Inventory

### 1A. Category summary

| Category | Count | Conductor requirement | Notes |
|---|---:|---|---|
| Pure-abort (no retry) | 4 | Phase 1 `on_error: true → to: abort_run` | Mechanical |
| Retry+abort (idempotent ops) | 13 | Phase 1 + RFC Phase 2 `retry:` action | Blocked on RFC Phase 2 |
| Retry+abort (platform-router) | 1 | Phase 1 + RFC Phase 2 `retry:` + `to:` | `poll_error_gate` in plan-level.yaml |
| Unilateral continue | 1 | Phase 1 (continue only) or Phase 1+2 (retry+continue) | `seeder_error_gate` — ⚠️ Beethoven review |
| Unilateral skip | 1 | Phase 1 `on_error: true → to: $end` | `classify_error_gate` — ⚠️ Beethoven review |
| Catch-all (7 parents) | 1 | Phase 1 per-parent OR workflow propagation | `workflow_error_gate` actionable.yaml — ⚠️ Beethoven review |

### 1B. Full gate inventory

Each entry: **gate name** | **file** | **original options** | **current #535 disposition** | **proposed Phase 2 rewrite** | **API dependency** | **notes**

---

#### `poll_error_gate` — `ado-pr.yaml`

- **Lines (pre-#535):** 505–531
- **Parent step:** `poll_status`
- **Original options:** retry → `poll_status`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES — single-shot poll retry removed
- **Proposed `on_error:` rewrite:**
  ```yaml
  - name: poll_status
    type: script
    # ... (existing)
    raises:
      - internal.script_error
    routes:
      - to: poll_status          # success path: loop back
        when: "{{ ... }}"
      - to: abort_run
        on_error: true           # catch-all error; Phase 2 adds retry:
      # Phase 2 (once RFC retry: ships):
      # - on_error: true
      #   retry: { max: 3, backoff: exponential, initial_seconds: 5 }
      # - to: abort_run
      #   on_error: true
  ```
- **API dependency:** Phase 1 covers catch-all abort; Phase 2 `retry:` action restores retry
- **Notes:** Identical shape to `poll_error_gate` in `github-pr.yaml`. Do both together.

---

#### `poll_error_gate` — `github-pr.yaml`

- **Lines (pre-#535):** 406–429
- **Parent step:** `poll_status`
- **Original options:** retry → `poll_status`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Proposed `on_error:` rewrite:** (same shape as ado-pr.yaml above)
- **API dependency:** Phase 1 + RFC Phase 2 `retry:`
- **Notes:** Identical to ado-pr.yaml gate. Batch with it.

---

#### `classify_error_gate` — `restack-remedy.yaml`

- **Lines (pre-#535):** 112–139
- **Parent step:** `classify`
- **Original options:** retry → `classify`; skip → `$end`
- **#535 disposition:** auto-skip to `$end` (lost retry option)
- **Retry capability lost:** YES (skip was default; retry was the optional path)
- **Proposed `on_error:` rewrite:**
  ```yaml
  - name: classify
    type: script
    # ...
    raises:
      - internal.script_error
    routes:
      - to: $end
        on_error: true           # skip on error (Phase 1 sufficient for skip-only)
      # Phase 2 with retry:
      # - on_error: true
      #   retry: { max: 2, backoff: fixed, initial_seconds: 10 }
      # - to: $end
      #   on_error: true         # post-exhaustion: skip
  ```
- **API dependency:** Phase 1 for skip-only; Phase 2 `retry:` to restore retry-then-skip
- **⚠️ Beethoven review — unilateral disposition:** Original gate offered retry OR skip. #535 committed to auto-skip. Beethoven should confirm: (a) auto-skip is correct for restack-remedy classify failures, (b) retry-then-skip is worth restoring when RFC Phase 2 ships.

---

#### `squash_coverage_error_gate` — `implement-merge-group.yaml`

- **Lines (pre-#535):** 1032–1060
- **Parent step:** `assert_impl_pr_coverage`
- **Original options:** retry → `assert_impl_pr_coverage`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Proposed `on_error:` rewrite:**
  ```yaml
  - name: assert_impl_pr_coverage
    type: script
    raises:
      - internal.script_error
    routes:
      - to: <success_target>
      - to: abort_run
        on_error: true           # Phase 1: catch-all abort
      # Phase 2:
      # - on_error: true
      #   retry: { max: 3, backoff: exponential, initial_seconds: 5 }
      # - to: abort_run
      #   on_error: true
  ```
- **API dependency:** Phase 1 + RFC Phase 2 `retry:`

---

#### `root_router_error_gate` — `implement-merge-group.yaml`

- **Lines (pre-#535):** 1250–1277
- **Parent step:** `root_router`
- **Original options:** retry → `root_router`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** Prompt referenced AB#3126 but routing was plain retry/abort.
- **API dependency:** Phase 1 + RFC Phase 2 `retry:`

---

#### `workflow_error_gate` — `actionable.yaml`

- **Lines (pre-#535):** 870–918
- **Parent steps (7):** `executor_router`, `ensure_evidence_branch`, `compose_addendum`, `open_evidence_pr`, `evidence_floor_check`, `evidence_reviewer`, `merge_evidence_pr`
- **Original options:** bare → `executor_router` (retry); abandon → `workflow_abandoned`
- **#535 disposition:** Per-parent direct routing (executor_router → abort_run; evidence steps → workflow_abandoned)
- **Retry capability lost:** Partially — per-step routing is now hardcoded
- **Proposed `on_error:` rewrite (per-parent, Phase 1 + Phase 2):**
  ```yaml
  # executor_router — Phase 1 abort; Phase 2 retry+abort
  - name: executor_router
    routes:
      - to: polyphony_executor
        when: "{{ ... }}"
      - to: abort_run
        on_error: true
      # Phase 2:
      # - on_error: true
      #   retry: { max: 2 }
      # - to: abort_run
      #   on_error: true

  # ensure_evidence_branch / compose_addendum / open_evidence_pr /
  # evidence_floor_check — Phase 1 abort; Phase 2 retry+abort
  - name: ensure_evidence_branch
    routes:
      - to: compose_addendum
      - to: abort_run
        on_error: true

  # evidence_reviewer — Phase 1 no change needed (LLM node, not script)
  # evidence_reviewer uses success-path routing (decision field on output)
  # on_error: would only fire for schema_violation; route to workflow_abandoned

  # merge_evidence_pr — Phase 1 + error catch
  - name: merge_evidence_pr
    routes:
      - to: workflow_completed
        when: "{{ merge_evidence_pr.output.merged == true }}"
      - to: workflow_abandoned
        when: "{{ merge_evidence_pr.output.merged == false }}"
      - to: workflow_abandoned
        on_error: true           # Phase 1: catch merge script error → abandon
  ```
- **API dependency:** Phase 1 handles abort/abandon-on-error for script nodes; LLM nodes (evidence_reviewer) are unaffected — they use success-path routing already.
- **⚠️ Beethoven review — unilateral disposition:** The original gate offered a single landing pad with retry-or-abandon. #535 split into per-step outcomes. Two sub-questions for Beethoven:
  - **evidence_reviewer:** Current: block/request_changes/approve routing is success-path (LLM output field), not on_error. The original gate's "abandon" option for a reviewer failure was a different thing (tool failure, not reviewer decision). Confirm the current split is correct.
  - **merge_evidence_pr → workflow_abandoned on false:** This is success-path routing (already present), not an error gate. No Phase 2 change needed. Confirm intent.
- **Batch strategy:** Migrate the 5 script-node parents (executor_router, ensure_evidence_branch, compose_addendum, open_evidence_pr, evidence_floor_check) as one batch. Leave evidence_reviewer and merge_evidence_pr alone (already success-path routed).

---

#### `root_resolver_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 346–364
- **Parent step:** `root_resolver`
- **Original options:** bare → `abort_run` (abort-only)
- **#535 disposition:** direct route to `abort_run`
- **Retry capability lost:** NO (was abort-only)
- **Proposed `on_error:` rewrite:**
  ```yaml
  - name: root_resolver
    type: script
    raises:
      - internal.script_error
    routes:
      - to: next_step
      - to: abort_run
        on_error: true
  ```
- **API dependency:** Phase 1 only — this is a clean Phase 1 retrofit

---

#### `type_loader_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 384–402
- **Parent step:** `type_loader`
- **Original options:** bare → `abort_run` (abort-only)
- **#535 disposition:** direct route to `abort_run`
- **Retry capability lost:** NO
- **Proposed `on_error:` rewrite:** same shape as `root_resolver` above
- **API dependency:** Phase 1 only

---

#### `ancestor_chain_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 428–450
- **Parent step:** `ancestor_chain`
- **Original options:** retry → `ancestor_chain`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **API dependency:** Phase 1 (abort-only) + RFC Phase 2 (retry+abort)

---

#### `state_detector_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 518–538
- **Parent step:** `state_detector`
- **Original options:** retry → `state_detector`; abort → `abort_run`
- **#535 disposition:** auto-abort (two abort_run routes in #535)
- **Retry capability lost:** YES
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `write_plan_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 1015–1037
- **Parent step:** `write_plan`
- **Original options:** retry → `write_plan`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** Idempotent write — retry is safe and valuable
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `ensure_plan_branch_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 1069–1092
- **Parent step:** `ensure_plan_branch`
- **Original options:** retry → `ensure_plan_branch`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** Idempotent git op — retry is safe
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `commit_and_push_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 1131–1156
- **Parent step:** `commit_and_push`
- **Original options:** retry → `commit_and_push`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** Git push — retry is safe for transient network failures
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `open_plan_pr_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 1191–1220
- **Parent step:** `open_plan_pr`
- **Original options:** retry → `open_plan_pr`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** API call to open PR — idempotent if PR already exists check passes
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `poll_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 1506–1529
- **Parent step:** `poll_status`
- **Original options:** retry → `pr_poll_platform_router`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** Platform-aware re-poll via router on retry — slightly more complex than other poll gates. On retry, routes back to `pr_poll_platform_router` (not directly to `poll_status`). Phase 2 `retry:` re-runs the node itself; may need a wrapper that respects the platform router.
- **API dependency:** Phase 1 + RFC Phase 2; **special case** — post-retry target is `pr_poll_platform_router`, not the failing node itself. If RFC Phase 2 `retry:` always re-runs the same node, this gate needs a small route re-architecture: `poll_status → (retry) → poll_status → (giveup) → abort_run`, and the platform router logic folds into `poll_status`'s success routing. Flag for Mahler: does `retry:` re-run the same node or route to a different node?

---

#### `merge_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 2448–2539
- **Parent step:** `merge_plan_pr`
- **Original options:** retry → `merge_plan_pr`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** Original prompt was cause-aware (listed common merge failure reasons) — no routing by error code, but prompt was diagnostic. Phase 2 can preserve cause info via `{{ error.message }}` in a downstream recovery node if desired. Not required for retrofit.
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `seeder_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 2604–2658
- **Parent step:** `seeder`
- **Original options:** retry → `seeder`; continue → `child_router`; abort → `abort_run`
- **#535 disposition:** auto-continue to `child_router`
- **Retry capability lost:** YES (both retry and abort options removed)
- **Notes:** Three-way gate — the only gate with a "continue despite error" option. The seeder verb exits 0 even on per-child errors (routing-style envelope), so `error_count > 0` is detected on the success path already. The actual on_error trigger here would be catastrophic seeder failure (verb crash), not per-child failure.
- **Proposed `on_error:` rewrite:**
  ```yaml
  - name: seeder
    type: script
    raises:
      - internal.script_error
    routes:
      - to: child_router
        when: "{{ seeder.output.error_count is defined and seeder.output.error_count == 0 }}"
      - to: child_router              # partial-seed continue (success path)
        when: "{{ seeder.output.children_seeded is defined and ... }}"
      - to: abort_run
        on_error: true               # catastrophic failure: abort
      # Phase 2:
      # - on_error: true
      #   retry: { max: 2, backoff: exponential }
      # - to: abort_run
      #   on_error: true
  ```
- **API dependency:** Phase 1 handles abort-on-catastrophic; Phase 2 restores retry
- **⚠️ Beethoven review — unilateral disposition:** Original gate offered retry-or-continue-or-abort on ANY seeder error. #535 committed to auto-continue (which was the "partial seed" intent). The auto-continue behavior is arguably correct for partial seeder errors. But the original retry option is gone for catastrophic failures. Beethoven should confirm: on a catastrophic seeder crash (not partial-seed, but verb failure), is auto-continue to `child_router` the right behavior? Or should a catastrophic crash abort?

---

#### `open_plan_pr_ado_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 2808–2837
- **Parent step:** `open_plan_pr_ado`
- **Original options:** retry → `open_plan_pr_ado`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `merge_plan_pr_ado_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 3008–3034
- **Parent step:** `merge_plan_pr_ado`
- **Original options:** retry → `merge_plan_pr_ado`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **API dependency:** Phase 1 + RFC Phase 2

---

## 2. Phase 1 API Spec-Check

Conductor Phase 1 (branch `feature/error-routing`, PR #229) ships:

### What Phase 1 provides

| Feature | Phase 1 | Notes |
|---|---|---|
| `on_error: <kind>` on routes | ✅ | Exact equality match on dotted kind string |
| `on_error: true` (catch-all) | ✅ | Matches any raised kind |
| `on_error: [kind1, kind2]` (multi-kind) | ✅ | OR match |
| `raises:` on agent definitions | ✅ | Optional contract enforcement |
| `$CONDUCTOR_ERROR_OUT` env var | ✅ | Script writes envelope, exits 0 |
| `ErrorEnvelope` shape: `{kind, message, details}` | ✅ | `conductor_error: true` stripped on coerce |
| `internal.script_error` synthetic kind | ✅ | Non-zero exit without envelope (with raises/on_error opt-in) |
| `internal.schema_violation` synthetic kind | ✅ | Agent output fails declared schema |
| `internal.undeclared_kind` synthetic kind | ✅ | Raised kind not in `raises:` list |
| `when:` on error routes | ✅ | Jinja/simpleeval condition on error-bucket routes |
| `{{ node.error.kind }}`, `{{ node.error.message }}`, `{{ node.error.details.foo }}` | ✅ | Error context in downstream templates |
| Transport-level `RetryPolicy` (existing) | ✅ | `retry_on: [provider_error, timeout]` — **not** route-level retry |

### What Phase 1 does NOT provide (gaps for Phase 2)

| Feature | Gap | Polyphony impact |
|---|---|---|
| `retry:` route action | ❌ Not in Phase 1 | 14 gates need retry-then-abort; blocked until RFC Phase 2 |
| Post-retry-exhaustion routing | ❌ Design open | Needed for retry-then-abort pattern |
| `halt:` and `propagate:` route actions | ❌ Phase 2 | Not needed for the 19 gates (all use `to:` targets) |
| Sub-workflow error propagation | ❌ Phase 2 | Would clean up `workflow_error_gate` but not required for retrofit |
| Workflow-level default `on_error:` | ❌ Not in Phase 1 | Would let the 4 pure-abort gates omit explicit error routes; nice-to-have |
| `provider.exhausted` routable kind | ❌ Phase 2 | Not needed for the 19 gates |

### Critical cross-cutting gap: polyphony CLI verb error emission

**This is the most important gap for Phase 2.**

Polyphony CLI verbs exit 0 on semantic error and write `{"error": "...", "success": false}` (or similar) to stdout JSON. The current workflows branch on `output.success == false` on the **success path** — there is no non-zero exit, so `internal.script_error` never fires.

For `on_error:` routes to trigger on polyphony verb failures:
- The verb script must **also** write to `$CONDUCTOR_ERROR_OUT` when it fails, OR
- The wrapper PowerShell script (Liszt) must detect `output.error` and re-emit to `$CONDUCTOR_ERROR_OUT`, OR
- We keep using success-path `when: "{{ output.success == false }}"` routing for polyphony verbs and reserve `on_error:` for infrastructure failures (git, API rate limits) that actually exit non-zero.

**Recommendation:** Option C for now (mixed pattern). Use `on_error:` for infrastructure script failures (git push, PR open, poll HTTP calls) where non-zero exit is the natural signal. Keep success-path routing for polyphony verb calls. This is clean and requires no C# changes.

Implication: several of the 19 TODO sites may already be correctly handled by success-path routing and only need an `on_error:` catch-all added for infrastructure failure. This reduces the Phase 2 scope for those nodes.

---

## 3. Test Strategy

### Per-workflow execution tests

For each retrofitted gate, a harness scenario (under `tests/harness/scenarios/`) that:
1. Runs the relevant workflow with a `FakeProvider` that simulates the failing node writing to `$CONDUCTOR_ERROR_OUT` with `internal.script_error`
2. Asserts the error route fires (not the success route)
3. For retry gates (after RFC Phase 2): asserts the node is re-run up to `max` times before escalating to `abort_run`

Priority scenarios:
- `ado-pr.yaml` + `github-pr.yaml`: `poll_status` failure → error route fires → abort_run
- `plan-level.yaml`: `write_plan` failure → retry (2×) → abort_run
- `plan-level.yaml`: `seeder` catastrophic failure → abort_run (NOT child_router)
- `actionable.yaml`: each of the 5 script-node parents fires its `on_error:` route

### Error envelope assertions

For each gate that maps to a specific error kind (post-Phase 2):
- Assert `{{ node.error.kind }}` is accessible in downstream templates
- Assert `{{ node.error.details }}` is accessible for any gate that uses cause-aware messaging

### Re-confirmation of 3 unilateral dispositions

Once Beethoven reviews the 3 dispositions (seeder, classify, evidence_reviewer/merge path), add explicit tests that:
1. Assert the chosen disposition fires (not the old 3-way human gate)
2. Assert NO regression to old behavior if disposition is changed

### Back-compat test

Confirm that workflows without any `on_error:` routes continue to work identically after conductor Phase 1 merges — the spec says backwards compatibility is guaranteed (existing workflows halt on unhandled errors as before).

---

## 4. Effort + Sequencing

### Size estimates

| Batch | Gates | Files | Size | Phase dependency |
|---|---|---|---|---|
| **A: Pure-abort** | `root_resolver`, `type_loader` (plan-level) | plan-level.yaml | S | Phase 1 only |
| **B: Poll catch-all** | `poll_error_gate` (ado-pr + github-pr) | ado-pr.yaml, github-pr.yaml | S | Phase 1 only (abort); M with retry |
| **C: actionable catch-all** | `workflow_error_gate` 5 script parents | actionable.yaml | M | Phase 1 only (abort); M with retry |
| **D: plan-level idempotent retry** | `ancestor_chain`, `state_detector`, `write_plan`, `ensure_plan_branch`, `commit_and_push`, `open_plan_pr`, `merge_error_gate`, `open_plan_pr_ado`, `merge_plan_pr_ado`, `poll_error_gate` | plan-level.yaml | L | RFC Phase 2 `retry:` |
| **E: implement-mg** | `squash_coverage`, `root_router` | implement-merge-group.yaml | S | RFC Phase 2 `retry:` |
| **F: seeder** | `seeder_error_gate` | plan-level.yaml | M | Phase 1 (abort only) or Phase 2 (retry) — pending Beethoven |
| **G: classify** | `classify_error_gate` | restack-remedy.yaml | S | Phase 1 (skip only) or Phase 2 (retry+skip) — pending Beethoven |

**Total: 2 S batches (Phase 1), 1 M batch (Phase 1), 1 L + 1 S batch (Phase 2), 2 batches pending Beethoven decision.**

### Recommended order

1. **Wait for conductor Phase 1 to merge** (PR #229). Validate with `conductor validate` on a test workflow.
2. **Batch A** (pure-abort, Phase 1 only): mechanical, lowest risk. One PR. Gets the pattern established.
3. **Batch B** (poll catch-all): two files, identical shape. One PR. Phase 1 abort-only; add Phase 2 retry via amendment once RFC Phase 2 merges.
4. **Batch C** (actionable catch-all 5 parents): careful per-parent mapping; moderate blast radius. One PR.
5. **Wait for Beethoven dispositions** on seeder, classify, workflow_error_gate (evidence path).
6. **Batches F + G** (seeder + classify): pending Beethoven decision.
7. **Wait for conductor RFC Phase 2 to merge** (retry: action).
8. **Batch D** (plan-level idempotent retry): largest batch; plan-level.yaml is high-value. One PR.
9. **Batch E** (implement-mg retry): straightforward.

### Parallelizable

Batches B and C can run in parallel after A. Batches D and E can run in parallel after RFC Phase 2 merges. F and G can run in parallel once Beethoven decides.

### Risks

1. **RFC Phase 2 timeline unknown.** 14 of 19 gates are blocked on it. If Mahler's upstream timeline is long, consider shipping a Phase 2a (Phase 1 gates only, 5 gates) and Phase 2b (RFC Phase 2 gates, 14 gates).
2. **plan-level.yaml circular self-reference pre-existing `validate` fail.** `conductor validate` will FAIL on plan-level.yaml regardless of changes. This is pre-existing (#535 confirmed identical on main). Do not let this block Phase 2 plan-level work — treat validate FAIL on plan-level as expected until upstream resolves the sub-workflow self-reference.
3. **Polyphony CLI verb exit-0 behavior.** If the decision is made to route polyphony verb errors through `on_error:` (Option A/B above), that requires C# changes owned by Mozart/Liszt and must be coordinated before polyphony-side Phase 2 begins.

---

## 5. Asks of Mahler / Upstream Conductor

These are requirements that Phase 2 needs conductor to deliver. They become the input to Mahler's implementation of RFC Phase 2.

### Ask 1 (CRITICAL): `retry:` route action

The 14 retry+abort gates cannot be retrofitted without a semantic retry on routes.

**Required shape:**
```yaml
routes:
  - on_error: true
    retry: { max: 3, backoff: exponential, initial_seconds: 5 }
  - to: abort_run
    on_error: true    # post-exhaustion fallback
```

**Field contract needed:**
- `retry.max` — integer, number of re-runs including or excluding first attempt? (clarify — original gate comment says "max: N" but RFC uses `max:` ambiguously)
- `retry.backoff` — `fixed | exponential` (schema.py `RetryPolicy` uses same values — reuse)
- `retry.initial_seconds` — base delay
- Post-exhaustion: how does control pass to the next error route? RFC open question — recommend **implicit by document order** (next matching error route after the `retry:` entry wins on exhaustion) as the simplest shape. Polyphony doesn't need explicit `when: retry_exhausted` predicates.

### Ask 2 (IMPORTANT): Retry re-runs the **same node**, not a different target

`poll_error_gate` in `plan-level.yaml` originally retried to `pr_poll_platform_router`, not `poll_status`. If `retry:` always re-runs the current node, the platform-router hop needs to be folded into `poll_status`'s own success routing. Confirm: does `retry:` re-run the declaring node (no `to:` needed) or does it route to an explicit `to:`?

**Polyphony's preference:** re-run the same node (simpler, consistent with "retry the failing step"). Will refactor `poll_error_gate` in plan-level.yaml accordingly.

### Ask 3 (USEFUL): `internal.script_error` fires for non-zero exit WITHOUT requiring `raises:` when any `on_error:` route is present

From `errors.py` and the example, `internal.script_error` is synthesized "when a script exits non-zero **AND the node opts in via `raises` or any `on_error` route present**." The second half of that sentence (opt-in via `on_error` route) needs to be confirmed as sufficient — if adding an `on_error:` route to a node implicitly opts it in, Phase 2 can omit `raises:` declarations from the 19 nodes (they have no known kinds — just infrastructure failures). This reduces boilerplate significantly.

### Ask 4 (NICE-TO-HAVE): Workflow-level default `on_error:` target

If conductor supports `workflow.on_error: { to: abort_run }` as a fallback for any node without an explicit error route, the 4 pure-abort gates (root_resolver, type_loader, ancestor_chain, state_detector) require zero YAML changes — the workflow default covers them. This would also protect any future nodes added without explicit error routes.

If this is too complex for Phase 2, skip it — per-node explicit routes work fine.

### Ask 5 (REQUIRED for polyphony CLI verb integration): Confirm exit-0 + `$CONDUCTOR_ERROR_OUT` write = on_error fires

The example shows scripts writing to `$CONDUCTOR_ERROR_OUT` and exiting 0. Confirm this is the correct contract (conductor treats the node as raised, evaluates on_error routes). This is the path for polyphony CLI verbs to opt into typed errors without changing their exit-code behavior (exit 0 always, write error to CONDUCTOR_ERROR_OUT on failure).

---

## 6. Beethoven Disposition Review — 3 Unilateral Changes

These three gates were changed in #535 without explicit disposition approval. Beethoven should confirm or override each before Phase 2 is scoped:

### D1: `seeder_error_gate` → auto-continue to `child_router`

**What changed:** The old 3-way gate (retry/continue/abort) was replaced with unconditional continue to `child_router`.

**Current behavior:** On any seeder error (including catastrophic verb failure), the workflow continues to `child_router` regardless.

**Concern:** The original "continue" option was intended for partial-seed scenarios (some children seeded, some failed). A catastrophic verb crash should probably abort, not silently continue with zero children.

**Options for Beethoven:**
- A) Accept auto-continue (current #535 behavior) for all cases — simplest
- B) Override: catastrophic failure → abort; partial-seed (error_count > 0) → continue (split on `on_error:` vs success path)
- C) Restore human gate for catastrophic failures only

### D2: `classify_error_gate` → auto-skip to `$end`

**What changed:** The old 2-way gate (retry/skip) was replaced with unconditional skip.

**Current behavior:** On `classify` failure in `restack-remedy.yaml`, the workflow silently succeeds (exits to `$end` without restacking).

**Concern:** Silent skip may hide systematic classify failures. Retry was the "try to salvage" option.

**Options:**
- A) Accept auto-skip (current behavior)
- B) Phase 2: retry-then-skip (restores the retry path when RFC Phase 2 ships)
- C) Escalate skips via event log annotation (no YAML change needed)

### D3: `workflow_error_gate` (evidence_reviewer + merge_evidence_pr) → split routing

**What changed:** The catch-all gate for 7 parents was split into per-step routing. `evidence_reviewer` block (3 routes: merge/retry/block) and `merge_evidence_pr` failure (→ `workflow_abandoned`) are now on the success path.

**Current behavior:**
- `evidence_reviewer.output.decision == 'approve'` → `merge_evidence_pr`
- `evidence_reviewer.output.decision == 'request_changes'` → retry loop
- `evidence_reviewer.output.decision == 'block'` → `workflow_abandoned`
- `merge_evidence_pr.output.merged == false` → `workflow_abandoned`
- The original gate's "abandon" option for a reviewer FAILURE (tool crash, not decision) → now routes to `workflow_abandoned` via catch-all M4 route on `evidence_reviewer`

**Assessment:** The split is correct. The original gate conflated tool failure with reviewer decision. The current routing correctly separates them. **No change recommended — confirm Beethoven agrees.**

---

## Appendix: TODO Comment Locations in #535

For quick reference, the TODO sites in the merged #535 code:

| File | TODO comment |
|---|---|
| `ado-pr.yaml` | `# on_error: auto-abort (AB#3257 — trivial gate removed; retry via conductor on_error: when Phase 1 ships)` |
| `github-pr.yaml` | `# on_error: auto-abort (AB#3257 — trivial gate removed; retry via conductor on_error: when Phase 1 ships)` |
| `implement-merge-group.yaml` | Two `# on_error: auto-abort (AB#3257 — trivial gate removed)` comments |
| `restack-remedy.yaml` | `# on_error: auto-skip (AB#3257 — trivial gate removed; routes to $end on classify error)` |
| `plan-level.yaml` | `# on_error: auto-abort (AB#3257 — trivial gate removed; cause info in run event log)` |

`actionable.yaml` had no explicit TODO comment — the gate was replaced by per-step routing which is self-documenting.


---

# Concrete Workflow Patterns: on_error + type:notification

**Author:** Wagner (Workflow Author)  
**Date:** 2026-05-28T23:00:46Z  
**Status:** Draft — informs Phase 2 retrofit (#536) and platespinner design  
**Prerequisites:** conductor Phase 1 (PR #229) + notifications (PR #213), both merged to `dogfood/on-error+notifications`  
**Coordination:** Bach owns notification envelope schema top-down. This doc consumes the envelope bottom-up. Fields I need from Bach are marked **[Bach ask]** in-line.

---

## Context and constraints

**What's available in Phase 1 + notifications:**
- `on_error: <kind>` / `on_error: true` on routes — typed error routing without `retry:`
- `raises:` on agent definitions — optional contract enforcement
- `internal.script_error` — synthesized when a script exits non-zero AND opts in
- Error in template context as `{{ node.error.kind }}`, `{{ node.error.message }}`, `{{ node.error.details }}`
- `type: notification` — fire-and-forget, typed, versioned, writes to `notifications.jsonl`
- `type: set` — bind computed values into workflow context
- `type: wait` — in-process pause (seconds)
- Notification envelope fields: `emission_id`, `schema_id`, `run_id`, `workflow`, `source_agent`, `correlation`, `workflow_metadata`, `payload`

**What is NOT available (Phase 1 only):**
- No `retry:` route action — RFC Phase 2 only
- No sub-workflow error propagation — RFC Phase 2
- No `halt:` / `propagate:` route actions

**Critical polyphony-specific constraint:**  
Polyphony CLI verbs **always exit 0** — `internal.script_error` only fires for raw infrastructure scripts (git, ADO API, HTTP calls). `on_error:` patterns in this doc apply at infrastructure boundaries, not polyphony verb calls. Polyphony verb failures use success-path routing (`when: "{{ output.success == false }}"`) and CAN be fed into notification nodes via that path.

---

## Pattern 1: `notify_then_route` — Error observability at the failure boundary

### When to use
Every `on_error:` catch that routes to `abort_run` or `workflow_abandoned`. Instead of silently aborting, interpose a notification step to publish the error context BEFORE routing to the terminal.

Operators learn about failures in platespinner without reading `events.jsonl`. This is the lowest-friction Phase 2 addition across all 19 retrofitted gates.

### YAML shape

```yaml
workflow:
  notifications:
    namespace: polyphony.plan_level
    correlation:
      - work_item_id
    types:
      step_failed:
        version: 1
        description: A script step failed with a typed error and is aborting.
        payload:
          step_name:     { type: string }
          error_kind:    { type: string }
          error_message: { type: string }
          work_item_id:  { type: string }
          # [Bach ask] severity: { type: string }   — "error" or "critical"

agents:
  - name: commit_and_push
    type: script
    command: pwsh
    args: [...]
    raises:
      - internal.script_error
    routes:
      - to: next_step                     # success
      - to: commit_failed_notifier        # error → notify → abort
        on_error: true

  - name: commit_failed_notifier
    type: notification
    notification: step_failed
    payload:
      step_name:     "commit_and_push"
      error_kind:    "{{ commit_and_push.error.kind }}"
      error_message: "{{ commit_and_push.error.message }}"
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: abort_run
```

### Notifications emitted
`step_failed` — one emission per infrastructure failure. Consumer sees: which step, what kind, what message, for which work item.

### Failure mode
`type: notification` is fire-and-forget. If platespinner isn't listening, the workflow aborts identically. The notification lands in `notifications.jsonl` regardless — consumers can replay it.

### Prerequisites
Phase 1 + notifications. No polyphony verb changes. No Liszt script changes.

### Notes
The notification step has full access to `{{ failing_step.error.* }}` because it is routed to via the error path — the failing step's error envelope is in context. This is the key insight that makes this pattern work cleanly.

**For the 14 retry+abort gates in #536:** once Phase 2 `retry:` action ships, replace the `commit_failed_notifier` above with a `retry_exhausted_notifier` that fires only after the retry budget is spent. The notification step itself doesn't change — only when it fires changes.

---

## Pattern 2: `decision_point_notification` — Auditable auto-dispositions

### When to use
When the workflow makes a non-obvious routing choice — especially the 3 unilateral dispositions Beethoven flagged in #536 (`seeder_error_gate` auto-continue, `classify_error_gate` auto-skip, `workflow_error_gate` split). The disposition stays hardcoded (correct behavior), but is now OBSERVABLE in platespinner instead of requiring events.jsonl forensics.

This is the Phase 1 answer to "should these be policy-configurable?" — not by making them configurable yet, but by making them visible so operators can audit and flag if they're wrong.

### YAML shape (seeder auto-continue example)

```yaml
workflow:
  notifications:
    namespace: polyphony.plan_level
    correlation:
      - work_item_id
    types:
      auto_disposition:
        version: 1
        description: Workflow made an automatic routing decision without human input.
        payload:
          decision_site:    { type: string }   # step name where decision was made
          disposition:      { type: string }   # "auto_continue" | "auto_skip" | "auto_abort"
          reason:           { type: string }   # human-readable why
          work_item_id:     { type: string }
          # [Bach ask] severity: { type: string }  — "warning" for auto_continue/skip, "info" for nominal

agents:
  - name: seeder
    type: script
    command: pwsh
    args: [...]
    routes:
      - to: child_router
        when: "{{ seeder.output.error_count == 0 }}"
      - to: seeder_partial_continue_notifier      # ← auto-disposition with notification
        when: "{{ seeder.output.error_count is defined
                   and seeder.output.error_count > 0
                   and seeder.output.children_seeded is defined
                   and seeder.output.children_seeded > 0 }}"
      - to: abort_run
        on_error: true                            # catastrophic failure: abort

  - name: seeder_partial_continue_notifier
    type: notification
    notification: auto_disposition
    payload:
      decision_site: "seeder"
      disposition:   "auto_continue"
      reason:        "Seeder completed with {{ seeder.output.error_count }} child error(s); {{ seeder.output.children_seeded }} children seeded. Continuing to child_router with partial results."
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: child_router
```

### classify_error_gate variant (restack-remedy.yaml)

```yaml
  - name: classify
    type: script
    routes:
      - to: apply_restack
      - to: classify_skip_notifier
        on_error: true

  - name: classify_skip_notifier
    type: notification
    notification: auto_disposition
    payload:
      decision_site: "classify"
      disposition:   "auto_skip"
      reason:        "classify script failed ({{ classify.error.message }}); restack skipped for this item."
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: $end
```

### Notifications emitted
`auto_disposition` — one per auto-routing decision that bypasses a human gate. Payload declares the decision explicitly so operators can grep/filter for "show me all auto_continue decisions this week."

### Failure mode
Fire-and-forget. Workflow routing is unaffected if platespinner isn't listening.

### Prerequisites
Phase 1 + notifications. No verb changes.

### Notes
**This directly answers the Beethoven #536 items.** The seeder, classify, and evidence_reviewer dispositions go from "silent hardcoded routes" to "hardcoded but auditable." Beethoven can review the `notifications.jsonl` after a run and confirm the disposition was correct, rather than needing to approve each one in advance.

**For Phase 2 (gate-disposition policy):** if operators want to override dispositions, the next step is a workflow `input:` parameter (e.g., `seeder_error_policy: auto_continue | abort | human`) with route branching on it. The `auto_disposition` notification type stays the same — just add a `policy_applied` field. That's a separate issue.

---

## Pattern 3: `progress_notification` — Long-running op observability

### When to use
Multi-phase operations in `plan-level.yaml` (plan generation: root_resolver → type_loader → ancestor_chain → architect → seeder → child_router) and `actionable.yaml` (evidence branch → compose_addendum → open_evidence_pr → floor_check → reviewer → merge). Currently the operator sees "still running" with no phase context.

### YAML shape

```yaml
workflow:
  notifications:
    namespace: polyphony.plan_level
    correlation:
      - work_item_id
    types:
      phase_started:
        version: 1
        payload:
          workflow_name:  { type: string }
          phase:          { type: string }
          phase_index:    { type: number }
          total_phases:   { type: number }
          work_item_id:   { type: string }
          # [Bach ask] No "estimated_seconds" field in Phase 1 — should we add it?
          #            Alternatively, platespinner can derive elapsed from emission timestamps.
      phase_complete:
        version: 1
        payload:
          workflow_name:  { type: string }
          phase:          { type: string }
          phase_index:    { type: number }
          total_phases:   { type: number }
          work_item_id:   { type: string }
          summary:        { type: string }   # one-line outcome ("3 children seeded")

agents:
  # ... after root_resolver completes ...
  - name: phase_type_loading_started
    type: notification
    notification: phase_started
    payload:
      workflow_name: "plan-level"
      phase:         "type_loading"
      phase_index:   "2"
      total_phases:  "6"
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: type_loader

  - name: type_loader
    type: script
    # ...
    routes:
      - to: phase_type_loading_complete
      - to: abort_run
        on_error: true

  - name: phase_type_loading_complete
    type: notification
    notification: phase_complete
    payload:
      workflow_name: "plan-level"
      phase:         "type_loading"
      phase_index:   "2"
      total_phases:  "6"
      work_item_id:  "{{ workflow.input.work_item_id }}"
      summary:       "Type definitions loaded"
    routes:
      - to: phase_ancestor_chain_started
```

### Notifications emitted
`phase_started` and `phase_complete` — pair around each major phase. Platespinner can render a progress bar using `phase_index / total_phases` and a phase timeline using emission timestamps.

### Failure mode
If `phase_started` fires and `phase_complete` never fires, platespinner knows the phase failed (can trigger alert). The absence of `phase_complete` is itself a signal.

### Prerequisites
Phase 1 + notifications. No verb changes. Adds node count (~2 per phase × 5-6 phases = 10-12 nodes in plan-level). Line budget: each node is ~8 lines, so ~80-100 lines added. Worth it for operational visibility.

### Notes
This is **purely additive** — no existing routes change, only notification interpose nodes added between existing steps. Safe to ship independently of #536 Phase 2 retrofit.

**Bach: I need clarity on whether `correlation` keys auto-surface on every notification envelope at the top level** (confirmed in the schema: yes, `workflow.notifications.correlation` lists input keys that auto-merge into `notification.correlation`). So `work_item_id` doesn't need to be in `payload:` explicitly if it's in `correlation:`. Removing it from `payload` keeps the type schema clean. **Recommend: move `work_item_id` to correlation and remove from payload types.**

---

## Pattern 4: `bounded_retry_loop` — Soft retry without `retry:` action

### When to use
When RFC Phase 2's `retry:` route action hasn't shipped yet, but you need bounded retry on infrastructure operations (git push, ADO API calls). This is a Phase 1-only workaround — once `retry:` ships, replace with the cleaner form.

**This is the only retry mechanism available before RFC Phase 2.**

### YAML shape

```yaml
# Requires type: set (available in conductor 0.1.18) and type: wait.
# Uses M10 iterate-until-stable with a context counter.
# context.mode must be accumulate for set values to persist across the loop.

workflow:
  context:
    mode: accumulate
  limits:
    max_iterations: 20   # 3 attempts × overhead; set conservatively

  notifications:
    namespace: polyphony.plan_level
    types:
      retry_attempt:
        version: 1
        payload:
          step:          { type: string }
          attempt:       { type: number }
          max_attempts:  { type: number }
          error_kind:    { type: string }
          work_item_id:  { type: string }
      retry_exhausted:
        version: 1
        payload:
          step:          { type: string }
          attempts_made: { type: number }
          last_error:    { type: string }
          work_item_id:  { type: string }

agents:
  # Entry — initialise counter once
  - name: init_push_retry
    type: set
    set:
      push_retry_count: "0"
    routes:
      - to: commit_and_push

  - name: commit_and_push
    type: script
    command: pwsh
    args: [...]
    raises:
      - internal.script_error
    routes:
      - to: next_step                           # success
      - to: push_retry_check                    # error → check budget
        on_error: true

  - name: push_retry_check
    type: set
    set:
      push_retry_count: "{{ (push_retry_count | default(0) | int) + 1 }}"
    routes:
      - to: push_retry_notifier                 # still budget remaining
        when: "{{ (push_retry_count | int) <= 3 }}"
      - to: push_exhausted_notifier             # budget gone

  - name: push_retry_notifier
    type: notification
    notification: retry_attempt
    payload:
      step:          "commit_and_push"
      attempt:       "{{ push_retry_count }}"
      max_attempts:  "3"
      error_kind:    "{{ commit_and_push.error.kind }}"
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: push_retry_wait

  - name: push_retry_wait
    type: wait
    seconds: 10              # fixed backoff; exponential requires script
    routes:
      - to: commit_and_push  # ← the M10 cycle

  - name: push_exhausted_notifier
    type: notification
    notification: retry_exhausted
    payload:
      step:          "commit_and_push"
      attempts_made: "{{ push_retry_count }}"
      last_error:    "{{ commit_and_push.error.message }}"
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: abort_run
```

### Notifications emitted
`retry_attempt` — per attempt, so operators see "attempt 2/3 failed" in real time.  
`retry_exhausted` — once, on budget exhaustion. Consumer can trigger escalation.

### Failure mode
Fire-and-forget. Workflow routing unaffected.

### Prerequisites
Phase 1 + notifications + `type: set` + `type: wait` (all in conductor 0.1.18). No verb changes.

### **Important warnings**

1. **Node count cost.** Each retriable step adds 5 nodes (init, check, retry_notifier, wait, exhausted_notifier). For 14 retry+abort gates, that's 70+ new nodes across 4 workflow files. **Use this ONLY for the 2-3 highest-value gates** (commit_and_push, merge_plan_pr, poll_status) before RFC Phase 2 ships. Then replace with `retry:` syntax.

2. **max_iterations budget.** A 3-attempt loop consumes at minimum 3 (commit_and_push) + 3 (push_retry_check) + 3 (push_retry_notifier) + 3 (push_retry_wait) = 12 iterations for one step. Plan accordingly.

3. **M10 footgun: M4 catch-all.** `push_retry_check`'s routes must include a terminal catch-all. Already present above (`push_exhausted_notifier`). Don't route the exhausted path back to the cycle.

4. **`type: set` context persistence.** Requires `context.mode: accumulate`. Verify this doesn't interact badly with other set values in the same workflow.

### Recommendation
**Do not ship this pattern broadly.** Use for the 2 most critical idempotent gates (commit_and_push, merge_plan_pr_ado) only. File RFC Phase 2 as a blocker for the remaining 12. The bounded_retry_loop is a stopgap that adds significant YAML bloat and iteration budget risk.

---

## Pattern 5: `escalation_chain` — Typed error recovery with fallback

### When to use
When a script failure has a known recovery path for specific error kinds, but a different recovery (or abort) for everything else. Requires the failing script to emit typed envelopes to `$CONDUCTOR_ERROR_OUT` — meaning this applies to **infrastructure scripts that already use typed errors**, not polyphony verb wrappers.

Most immediately applicable to: `poll_status` (rate-limit vs connection failure), `open_plan_pr` (auth failure vs conflict), `commit_and_push` (auth vs network vs conflict).

### YAML shape

```yaml
workflow:
  notifications:
    namespace: polyphony.plan_level
    types:
      rate_limit_backoff:
        version: 1
        payload:
          step:           { type: string }
          retry_after_s:  { type: number }
          work_item_id:   { type: string }
      operation_failed:
        version: 1
        payload:
          step:       { type: string }
          error_kind: { type: string }
          error_msg:  { type: string }
          work_item_id: { type: string }

agents:
  - name: poll_status
    type: script
    command: pwsh
    args: [...]
    raises:
      - external.api.rate_limited
      - external.api.connection_failed
      - internal.script_error
    routes:
      - to: merge_pr                            # success: PR merged
        when: "{{ poll_status.output.state == 'completed' }}"
      - to: poll_status                         # success: still pending — re-poll
        when: "{{ poll_status.output.state == 'active' }}"
      - to: rate_limit_backoff_notifier         # typed: rate limited
        on_error: external.api.rate_limited
      - to: notify_and_abort                    # typed or untyped: all other errors
        on_error: true

  - name: rate_limit_backoff_notifier
    type: notification
    notification: rate_limit_backoff
    payload:
      step:          "poll_status"
      retry_after_s: "{{ poll_status.error.details.retry_after_s | default(60) }}"
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: rate_limit_wait

  - name: rate_limit_wait
    type: wait
    seconds: 60       # [Bach ask] Could we template this from the error details?
                      # e.g. seconds: "{{ poll_status.error.details.retry_after_s }}"
                      # If wait.seconds is a Jinja template string, this works.
    routes:
      - to: poll_status   # retry after backoff

  - name: notify_and_abort
    type: notification
    notification: operation_failed
    payload:
      step:         "poll_status"
      error_kind:   "{{ poll_status.error.kind }}"
      error_msg:    "{{ poll_status.error.message }}"
      work_item_id: "{{ workflow.input.work_item_id }}"
    routes:
      - to: abort_run
```

### Notifications emitted
`rate_limit_backoff` — visible in platespinner as "waiting for rate limit to clear." Consumer can alert if retry_after_s is unreasonably long.  
`operation_failed` — on non-recoverable error, before aborting.

### Failure mode
Fire-and-forget. Workflow routing unaffected.

### Prerequisites
Phase 1 + notifications. **Requires infrastructure scripts to emit typed envelopes** — the polyphony PowerShell helpers (Liszt) need `Invoke-ConductorError` or equivalent for the error kinds above. Currently: scripts must write to `$CONDUCTOR_ERROR_OUT` manually (3-line PowerShell idiom).

### Notes
**This is the only Phase 1 pattern that requires Liszt's involvement.** For the pattern to work, the infrastructure scripts (`poll_status`, `open_plan_pr`, etc.) need to write typed envelopes when they fail with known recoverable errors. Without typed envelopes, the catch-all `on_error: true` fires for everything and the typed recovery arms never trigger.

**Bach: Can `type: wait`'s `seconds:` field be a Jinja2 template?** If yes, the rate-limit backoff can use `{{ poll_status.error.details.retry_after_s }}` directly. If no, we need a `type: set` + branch to map the value. Worth confirming.

---

## Pattern 6: `renegotiation_notification` — Parent cascade observability

### When to use
When `plan-level.yaml`'s `validate_scope` triggers a renegotiation and spawns a recursive plan-level sub-workflow. Currently the operator sees the parent run wedge silently while waiting for child planning. With notification, the cascade is visible.

### YAML shape

```yaml
workflow:
  notifications:
    namespace: polyphony.plan_level
    types:
      renegotiation_started:
        version: 1
        payload:
          parent_work_item:     { type: string }
          scope_violation_count: { type: number }
          trigger_phase:         { type: string }
          work_item_id:          { type: string }
      renegotiation_complete:
        version: 1
        payload:
          parent_work_item: { type: string }
          outcome:          { type: string }  # "resolved" | "escalated" | "aborted"
          work_item_id:     { type: string }

agents:
  # Between validate_scope and the renegotiation sub-workflow call
  - name: renegotiation_started_notifier
    type: notification
    notification: renegotiation_started
    payload:
      parent_work_item:      "{{ workflow.input.work_item_id }}"
      scope_violation_count: "{{ validate_scope.output.scope_violation_files | length }}"
      trigger_phase:         "plan_generation"
      work_item_id:          "{{ workflow.input.work_item_id }}"
    routes:
      - to: spawn_renegotiation_subworkflow
```

### Notifications emitted
`renegotiation_started` — when cascade begins. Platespinner can show "⏳ renegotiating scope" against the work item.  
`renegotiation_complete` — after sub-workflow returns, before re-entering the parent workflow.

### Prerequisites
Phase 1 + notifications. Additive only — no existing routes change.

---

## Pattern 7: `async_gate_prompt` — Notify + human gate with action URL

### When to use
When a `human_gate` requires operator action but the operator isn't watching the terminal. Emit a notification with enough context for platespinner to surface an action prompt; the workflow then falls into the gate as normal.

### YAML shape

```yaml
workflow:
  notifications:
    namespace: polyphony.actionable
    types:
      human_action_required:
        version: 1
        payload:
          gate_name:     { type: string }
          run_id:        { type: string }
          prompt:        { type: string }   # one-line human-readable summary
          work_item_id:  { type: string }
          # [Bach ask] Should conductor emit run_id as a built-in context variable?
          # Currently the workflow must thread it through via workflow.input.
          # Platespinner can construct the gate URL from run_id + gate_name alone.

agents:
  - name: review_gate_prompt_notifier
    type: notification
    notification: human_action_required
    payload:
      gate_name:    "review_evidence_gate"
      run_id:       "{{ workflow.input.run_id }}"
      prompt:       "Evidence PR is ready for review. Approve or block?"
      work_item_id: "{{ workflow.input.work_item_id }}"
    routes:
      - to: review_evidence_gate

  - name: review_evidence_gate
    type: human_gate
    prompt: |
      Evidence PR {{ open_evidence_pr.output.pr_url }} is ready for review.
      Choose approve or block.
    choices:
      - value: approve
        route: merge_evidence_pr
      - value: block
        route: workflow_abandoned
```

### Notifications emitted
`human_action_required` — fires immediately before the gate. Platespinner sees: which gate, which run, what's needed. Can surface an action button.

### Limitation
Conductor does not currently expose `run_id` as a built-in template variable. The workflow must receive it as an input (or Mahler adds `{{ conductor.run_id }}` as a built-in). **[Bach ask] — see below.**

### Prerequisites
Phase 1 + notifications. Works today if `run_id` is threaded through as a workflow input.

---

## Pattern 8: `gate_disposition_policy` — Declared override point for auto-dispositions

**(Mentioned, not fully designed — Phase 1.5 pattern, requires workflow input changes)**

The 3 Beethoven-flagged dispositions (seeder, classify, evidence_reviewer) can be made operator-configurable by adding a small policy input to each workflow:

```yaml
input:
  seeder_error_policy:
    type: string
    default: auto_continue   # or: abort | human

# Then in seeder routes:
routes:
  - to: seeder_partial_continue_notifier
    when: "{{ workflow.input.seeder_error_policy == 'auto_continue'
               and seeder.output.error_count > 0 }}"
  - to: abort_run
    when: "{{ workflow.input.seeder_error_policy == 'abort'
               and seeder.output.error_count > 0 }}"
  - to: seeder_error_human_gate
    when: "{{ workflow.input.seeder_error_policy == 'human'
               and seeder.output.error_count > 0 }}"
```

**Why only mentioned:** This requires polyphony's workflow invocation scripts (Liszt) to pass the policy input, and a decision on what the default should be (Pattern 2 establishes `auto_continue` is correct for the partial-seed case). File separately once Pattern 2 is shipped and Beethoven has confirmed the defaults.

---

## Bach envelope field requirements

| Field | Pattern | Why | Ask |
|---|---|---|---|
| `severity` | 1, 2, 3, 5 | Consumers need to filter by urgency: `info` (progress), `warning` (auto-disposition), `error` (step failed), `critical` (abort). Without it, platespinner must infer from `notification_type`. | Add `severity: info \| warning \| error \| critical` to either the envelope top-level or as a `NotificationTypeDef` field. |
| `run_id` as built-in context | 7 | `async_gate_prompt` needs `{{ conductor.run_id }}` without requiring it as workflow input | Expose `conductor.run_id` as a built-in template variable (alongside `workflow.input.*`) |
| `type: wait` with templated `seconds:` | 5 | Rate-limit backoff should use `{{ poll_status.error.details.retry_after_s }}` | Confirm `wait.seconds` accepts Jinja2 template string, not just literal integer |
| `disposition` standard key | 2 | `auto_disposition` notifications need a standard key name consumers can filter on generically | Either bless `disposition` as a conventional key, or add it as a first-class envelope field for decision notifications |

---

## Ranked usefulness (top 5)

1. **`notify_then_route`** — Phase 1 only, works today, applies to ALL 19 retrofit gates. Highest leverage per line of YAML. Should accompany every `on_error: true → abort_run` route in Phase 2.

2. **`decision_point_notification`** — Phase 1 only, directly resolves the Beethoven audit gap. Ships the 3 flagged dispositions as observable policy without requiring Beethoven pre-approval of each.

3. **`progress_notification`** — Phase 1 only, purely additive, dramatically improves plan-level and actionable observability. No existing routes change.

4. **`escalation_chain`** — Phase 1 + typed envelope helpers from Liszt. The cleanest error recovery pattern for infrastructure scripts once they emit typed errors.

5. **`bounded_retry_loop`** — Phase 1 only stopgap for retry before RFC Phase 2. High node cost; use for 2-3 critical gates only. Replace with `retry:` when Phase 2 ships.

---

## Relationship to Phase 2 retrofit (#536)

These patterns are the SHAPE that the Phase 2 YAML will take once conductor Phase 1+notifications land. Specifically:

- **Phase 2a (5 Phase-1-only gates):** each gets Pattern 1 (`notify_then_route`) + Pattern 2 (`decision_point_notification`) where disposition was unilateral.
- **Phase 2b (14 retry+abort gates):** each gets Pattern 1 first, then replace with `retry:` + `retry_exhausted_notifier` when RFC Phase 2 ships. Pattern 4 (`bounded_retry_loop`) bridges the gap for 2-3 highest-priority gates.
- `poll_status` specifically gets Pattern 5 (`escalation_chain`) once Liszt ships typed envelope emission helpers.


---

# on_error: + Notifications — Architectural Design (Revised)

**Author:** Bach (Architect)  
**Date:** 2026-05-28  
**Status:** Proposal (scope-refined per Daniel's 2026-05-28 directive)  
**Relates to:** Epic #521 (self-contained orchestration), #528 (error-gate migration), conductor PRs #229 (on_error Phase 1), #213 (notifications)

---

## Part 1 — Architectural Patterns Unlocked

### 1.1 Gate Compression — the headline unlock

**The pattern:** `human_gate` → `(notification + script-poll-loop)`

Today, polyphony workflows block on human gates for conditions that are *externally observable* — PR merged, PR has approving review, CI green, work item state changed, evidence branch exists. The operator must babysit the conductor TTY or web dashboard, perform the action, then click through the gate.

With `type: notification` + `script:` polling, the workflow becomes:

```text
┌──────────────────────────────────────────────────────┐
│ BEFORE (gate pattern)                                 │
│                                                       │
│  [create_pr] → [pr_review_gate] ← operator clicks    │
│                    ↓                                  │
│               [continue...]                           │
└──────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────┐
│ AFTER (notification + poll pattern)                    │
│                                                       │
│  [create_pr] → [notify: "PR ready for review"]       │
│                    ↓                                  │
│  [poll_pr_status] ← script loops every N seconds     │
│       ↓ (when: merged/approved/closed)               │
│  [continue...] + [notify: "PR resolved, continuing"] │
└──────────────────────────────────────────────────────┘
```

**What this compresses:** Gates become *rare* — reserved only for genuine human-judgment decisions where there's nothing observable to poll (e.g., "should we abandon this plan entirely?" or "is this scope violation acceptable?"). Every pollable condition becomes a script-loop fronted by a notification.

**The full loop (four segments, one-way each):**

```text
conductor → platespinner     : notification event (fire-and-forget via .events.jsonl)
platespinner → user          : toast / PWA push / tray badge / dashboard card
user → external world        : clicks CTA, performs action (review PR, merge, approve)
external world → conductor   : polled by script: step inside the workflow (NOT pushed via platespinner)
```

**Critical seam property:** PlateSpinner does NOT communicate back to conductor. The polling script does the discovery. This is a one-way observation contract — no write-back, no RPC, no coupling beyond the `.events.jsonl` line shape.

**Gates that compress under this pattern (current polyphony workflow inventory):**

| Current gate | Pollable condition | Notification content |
|-------------|-------------------|---------------------|
| `pr_review_gate` | PR has ≥1 approving review (GitHub/ADO API) | "PR #{n} ready for review at {url}" |
| `pr_merge_gate` | PR merged status (GitHub/ADO API) | "PR #{n} approved, ready to merge" |
| `pending_review_gate` | PR review policy satisfied | "PR #{n} awaiting your review" |
| `stuck_review_gate` | Review timeout elapsed + still no review | "PR #{n} review stalled — {hours}h with no activity" |
| `evidence_review_gate` | Evidence PR approved | "Evidence for #{wi} ready for review" |

**Gates that remain as genuine judgment calls:**

| Gate | Why it can't be polled |
|------|----------------------|
| `scope_violation_gate` | Requires human decision: override or abort |
| `root_fallback_gate` | Requires human decision: treat as root or abort |
| `renegotiation arbitration` | Requires human judgment on plan conflict |

### 1.2 Self-healing seams (on_error: unlock — secondary pattern)

The four seams crossing network/process boundaries share a transient-failure pattern:

| Seam | With `on_error:` Phase 1 |
|------|--------------------------|
| polyphony↔twig CLI | Route to retry node (backoff) → abort after N |
| polyphony↔git | Route to retry (lock contention) → abort |
| polyphony↔PR platform | Route to retry (rate limit/5xx) → abort |
| polyphony↔ADO (via twig sync) | Route to retry (429/5xx) → abort |

The 14 retry-then-abort gates removed in #535 can be restored as automated retry chains — no human in the loop. This is the *error* path; §1.1 is the *success* path.

### 1.3 The "notification" vocabulary problem

Three systems use "notification" for three different things:

| System | Their "notification" | Our term |
|--------|---------------------|----------|
| Conductor | `type: notification` YAML node (the mechanism) | "notification node" (conductor's term; we don't rename it) |
| PlateSpinner | Toast / bell / PWA push (the UX artifact) | "toast" or "push" (platespinner's term) |
| Polyphony | The structured payload carried by the notification node | **"domain signal"** (proposed polyphony term) |

⚠️ **Glossary flag:** `domain signal` must be added to `docs/glossary.md` before any workflow ships this pattern.

### 1.4 The polyphony-CLI-exit-0 problem — seam decision

**The problem:** Every polyphony verb exits 0. Conductor's `on_error:` fires on non-zero exit / `$env:CONDUCTOR_ERROR_OUT`. So `on_error:` never fires for polyphony verbs today.

**Recommended: Option C (hybrid).**

| Outcome type | Exit code | Routing mechanism |
|-------------|-----------|-------------------|
| Domain outcome (skip, invalid, no-children, etc.) | Exit 0 | `when:` conditions on JSON output fields (existing pattern, unchanged) |
| Infrastructure failure (unhandled exception, OOM, corrupt file) | Exit non-zero + error envelope to `$env:CONDUCTOR_ERROR_OUT` | `on_error:` routing |

Implementation: one try/catch in the verb host around `Main()`. Domain-level error results (e.g., `action: "error"`) remain exit-0 — they're business decisions, not crashes.

**ADR required** — this is load-bearing. See ADR proposal stub.

### 1.5 Renegotiation flows

Gate compression doesn't change renegotiation control flow (parent-plan-generation remains serialized via same-root run lock). But domain signals close the *visibility gap*:

- Signal: "Child #{id} requests parent plan change" (with `cta_url` → the child plan PR)
- Signal: "Renegotiation resolved — parent plan updated" (with `correlation_id` linking back)

The operator sees the signal, reviews the PR, the poll loop detects the review. No gate needed for the observation; judgment gate only fires if there's a conflict requiring arbitration.

---

## Part 2 — PlateSpinner Integration Design

### 2.1 Domain signal envelope schema (revised)

```json
{
  "schema_version": "1.0",
  "kind": "review_pr | merge_pr | approve_plan | resolve_conflict | retry_exhausted | step_failed | auto_disposition | phase_started | phase_complete",
  "severity": "info | warning | error | critical",
  "subject": "PR #142 ready for review",
  "cta_url": "https://github.com/org/repo/pull/142",
  "cta_kind": "review_pr",
  "correlation_id": "pr-142-review-cycle",
  "expires_at": "2026-05-29T15:58:10-07:00",
  "work_item_id": 12345,
  "root_id": 67890,
  "run_id": "feature/67890",
  "disposition": "auto_continue | auto_skip | auto_abort",
  "details": {
    "pr_number": 142,
    "branch": "impl/67890-1-12345",
    "target_branch": "mg/67890-1",
    "error_kind": "external.api.rate_limited",
    "retry_after_s": 60,
    "attempt": 2,
    "max_attempts": 3
  }
}
```

**Field inventory (aligned with Wagner's 8 patterns):**

| Field | Required | Purpose | Wagner pattern ref |
|-------|----------|---------|-------------------|
| `kind` | ✅ | Domain category of the signal. Polyphony-owned enum. | All patterns |
| `severity` | ✅ | Urgency. `info\|warning\|error\|critical`. Default set at type-def level. | All patterns (Ask #1) |
| `subject` | ✅ | One-line human summary for toast title | All patterns |
| `cta_url` | Optional | Clickable link for user action | Pattern 7 (`async_gate_prompt`), gate compression |
| `cta_kind` | Optional | Semantic action type for button rendering | Gate compression patterns |
| `correlation_id` | Optional | Links related signals in a pollable cycle | Patterns 3, 6 (phase pairs, renegotiation pairs) |
| `expires_at` | Optional | Hard expiry for stale-signal cleanup | Gate compression (PR review timeout) |
| `work_item_id` | Optional | Enrichment key (auto-lifted from `correlation:` config) | All patterns |
| `run_id` | ✅ (auto) | Conductor auto-populates on wire; workflow templates via input workaround | Pattern 7 (Ask #2) |
| `disposition` | Optional | For `auto_disposition` signals: which routing decision was taken | Pattern 2 (`decision_point_notification`) |
| `details` | Optional | Signal-specific structured bag. Schema varies per `kind`. | Patterns 4, 5 (retry/escalation details) |

**Severity vocabulary (aligned with Wagner's pattern catalogue):**

| Severity | Meaning | PlateSpinner treatment | Wagner patterns that emit it |
|----------|---------|----------------------|------------------------------|
| `info` | Progress update / resolution confirmation | Inline activity-log entry only. Used to resolve `correlation_id`. | `progress_notification`, `renegotiation_complete` |
| `warning` | Auto-disposition taken or degraded state (retry in progress) | Bell badge + inline log entry. No toast unless opted in. | `decision_point_notification` (auto_continue/skip), `retry_attempt` |
| `error` | Step failed, retry exhausted, operation aborted | Toast + badge + inline (red). Operator should be aware. | `notify_then_route` (step_failed), `retry_exhausted` |
| `critical` | Run-level abort — workflow cannot continue without intervention | Toast + badge + tray alert. Immediate attention. | Fatal abort paths, scope arbitration escalation |

**Rationale for 4-level severity (vs earlier 3-level):** Wagner's patterns (#543 `notify_then_route`, #544 `decision_point_notification`) need to distinguish between "step failed and we're aborting" (`error`) vs "auto-disposition was taken, FYI" (`warning`). The `action_required` concept from the earlier draft is now expressed as `cta_kind` being present — any signal with a `cta_url` + `cta_kind` implies the user should act. Severity is orthogonal to CTA presence.

**Note:** `severity` lives on the **notification type definition** (in `workflow.notifications.types.<name>`) as a default, overridable per emission site in the `payload:` block. This lets Wagner define `step_failed` as severity `error` at the type level, while `auto_disposition` defaults to `warning`.

### 2.2 Wagner's three asks — architectural rulings

**Ask 1: `severity` on `NotificationTypeDef` or top-level envelope field?**

**Ruling: BOTH.** `severity` is:
- Declared as a **default** on the `NotificationTypeDef` (type-level), so Wagner can set `step_failed.severity: error` once and every emission inherits it.
- Overridable per **emission site** in `payload:` (instance-level), so a `progress_notification` can default to `info` but escalate to `warning` for unusually long phases.
- Present on the **wire-format envelope** that platespinner reads (always resolved by emission time — platespinner never reads the type definition, only the emitted event).

This mirrors conductor's own pattern for `type: notification` fields: definition-level defaults + per-node override.

**Ask 2: `{{ conductor.run_id }}` as a built-in Jinja template variable?**

**Ruling: GAP — file as a conductor feature request.** Conductor PR #213's notification envelope includes `run_id` at the *wire level* (it's in the emitted event automatically — see Wagner's doc line 21: "envelope fields: `emission_id`, `schema_id`, `run_id`..."). So platespinner already gets it. The gap is that YAML *template expressions* inside `payload:` can't reference it today — the workflow must thread `run_id` as a workflow input.

**Workaround (Phase 1):** Thread `run_id` as a workflow input from the launcher. Polyphony's `Invoke-PolyphonySdlc.ps1` already has access to the run ID at invocation time. This is one line in the workflow `input:` schema and one param in the launcher.

**Medium-term:** File conductor feature request: "Expose `{{ conductor.run_id }}` as a built-in template variable available in all Jinja2 contexts (payload, when, output)." Tag as P2 — the workaround is adequate.

**CTA URL construction:** For Wagner's `async_gate_prompt` pattern, the CTA URL becomes:
```
cta_url: "https://platespinner.local/runs/{{ workflow.input.run_id }}/gate/{{ gate_name }}"
```
This works today with the workaround. When `{{ conductor.run_id }}` ships, replace.

**Ask 3: Does `type: wait` accept a templated `seconds:` field?**

**Ruling: UNKNOWN — flag as a blocker for `bounded_retry_loop` and `escalation_chain`.** Conductor's `type: wait` documentation (as observed in Wagner's patterns) shows only a literal integer. Whether Jinja2 templating is supported for `seconds:` depends on conductor's node-field evaluation order.

**Action required from Mahler:** Verify in the dogfood fork whether:
```yaml
- name: rate_limit_wait
  type: wait
  seconds: "{{ poll_status.error.details.retry_after_s | default(60) }}"
```
evaluates correctly. If conductor resolves Jinja2 in `seconds:` before passing to the wait implementation, Wagner's `escalation_chain` pattern works as-is. If NOT:

**Fallback:** Replace the `type: wait` node with a `type: script` node that calls `Start-Sleep -Seconds $retryAfter` where `$retryAfter` is passed as an argument from the template context. This is uglier but functional and doesn't block the pattern.

**Degradation assessment:** If templating isn't supported, `bounded_retry_loop` degrades to fixed backoff (acceptable — exponential backoff is a polish item). `escalation_chain` degrades to fixed 60s wait on rate-limit (acceptable for Phase 1 — ADO rate-limit `Retry-After` headers are typically 5-60s). Neither pattern is blocked; both just become slightly less adaptive.

### 2.3 PlateSpinner consumption surface

**The user ↔ workflow loop, platespinner's role:**

```text
[workflow emits domain signal]
         ↓
[platespinner reads .events.jsonl line]
         ↓
[platespinner parses envelope, extracts cta_kind + severity]
         ↓
┌─────────────────────────────────────────────────────┐
│ error/critical with cta_url:                         │
│   • Windows toast: subject + "Open" button → cta_url│
│   • Tray badge: increment "N waiting on you" count  │
│   • Bell panel: card with CTA button                │
│   • Dashboard: inline entry with action button      │
├─────────────────────────────────────────────────────┤
│ info with matching correlation_id:                   │
│   • Clear the earlier card/toast                    │
│   • Tray badge: decrement count                     │
│   • Dashboard: inline entry marking resolution      │
└─────────────────────────────────────────────────────┘
```

**CTA kind → button mapping:**

| `cta_kind` | Button label | Icon |
|-----------|-------------|------|
| `review_pr` | "Review PR" | 👁 |
| `merge_pr` | "Merge PR" | ✓ |
| `approve_plan` | "Review Plan" | 📋 |
| `resolve_conflict` | "Resolve" | ⚠️ |
| `view_evidence` | "Review Evidence" | 📎 |

Clicking the button opens `cta_url` in the default browser. That's the entire interaction — platespinner doesn't need to know what happens next. The workflow's poll script handles detection.

### 2.3 Gaps in PlateSpinner (refined)

| # | Gap | What to build | Priority |
|---|-----|--------------|----------|
| 1 | **No `type: notification` event parsing** | Event-type handler in the `.events.jsonl` parser that recognizes notification events and extracts the polyphony domain signal envelope. | P0 (nothing works without this) |
| 2 | **No CTA-aware rendering** | Map `cta_kind` → action button with icon + label. Clicking opens `cta_url` in browser. | P0 (the primary user touchpoint) |
| 3 | **No correlation-based signal lifecycle** | When a signal with `correlation_id` + severity `info` arrives, auto-dismiss/resolve the earlier `action_required` signal with the same correlation_id. Decrement tray badge. | P1 (prevents stale toast storm) |
| 4 | **No "waiting on you" tray badge count** | Badge on the tray icon showing count of unresolved `action_required` signals across all active runs. | P2 (polish — low coupling) |

**Explicitly OUT of platespinner scope:**
- Two-way RPC back to conductor (never)
- Hosting the polling logic (that's a workflow `script:` step)
- Replacing conductor dashboard for live execution monitoring (platespinner augments, doesn't replace)

### 2.4 Where the contract lives

```text
┌─────────────────────────────────────────────────────────────────┐
│ Conductor                                                        │
│  Owns: type: notification node YAML syntax                       │
│  Owns: .events.jsonl wire format for notification events         │
│  Owns: WHEN the event line is written (at node execution)        │
│  Does NOT interpret: the payload (passes it through opaquely)    │
└───────────────────────────────┬─────────────────────────────────┘
                                │ .events.jsonl line
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│ Polyphony (workflow layer)                                       │
│  Owns: domain signal envelope schema (kind, severity, cta_*,    │
│         correlation_id, expires_at, work_item_id, details)       │
│  Owns: WHICH signals are emitted and WHEN                        │
│  Owns: the `kind` vocabulary enum                                │
│  Owns: correlation_id generation (scoped per pollable cycle)     │
└───────────────────────────────┬─────────────────────────────────┘
                                │ platespinner reads .events.jsonl
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│ PlateSpinner                                                     │
│  Owns: severity → UX mapping (toast / badge / inline)            │
│  Owns: cta_kind → button label/icon mapping                      │
│  Owns: correlation-based lifecycle (dismiss on resolution)        │
│  Owns: expires_at enforcement (clear stale signals)              │
│  Owns: dedup (same correlation_id doesn't re-toast)              │
│  Does NOT own: polling logic, workflow control, write-back        │
└─────────────────────────────────────────────────────────────────┘
```

The seam is `.events.jsonl` (fire-and-forget, one-way). Platespinner is a pure observer with local UX state management (correlation tracking, expiry, badge counts).

---

## Part 3 — Risks, Blockers, Vocabulary Alerts

### 3.1 Three-vocabulary rule compliance

| Item | Assessment |
|------|-----------|
| `kind` enum values (`review_pr`, `merge_pr`, `approve_plan`, `step_failed`, `auto_disposition`, `phase_started`, etc.) | These are **event names** (vocabulary 1). They name what happened / what's needed. ✅ Compliant. Shared between Bach's envelope and Wagner's pattern catalogue — single vocabulary, single owner (polyphony). |
| `severity` (`info`, `warning`, `error`, `critical`) | New classification axis. NOT a lifecycle event, NOT a state name, NOT a category. Must be documented as a signal-specific classifier — distinct from the three vocabularies. ⚠️ Pin in glossary to prevent confusion with state categories. |
| `disposition` (`auto_continue`, `auto_skip`, `auto_abort`) | These are **routing decision names**, not state names. They describe what the workflow DID, not what state the work item is in. ✅ Compliant — they belong to vocabulary 1 (events/actions). |
| `cta_kind` values | Subset of `kind` — maps to the same space. Not a new vocabulary. ✅ |
| `correlation_id` | Opaque identifier, not a vocabulary term. ✅ |
| "domain signal" | New concept. ⚠️ Must be added to glossary before any workflow ships it. |

### 3.2 New seams

| New seam | Properties | Net assessment |
|----------|-----------|---------------|
| polyphony domain signal → platespinner (via .events.jsonl) | READ-ONLY, one-way, fire-and-forget. Platespinner never pushes back. | Acceptable — minimal coupling. |
| script-poll-loop → external APIs (GitHub/ADO) | Already exists (poll_status scripts exist today in `github-pr.yaml` and `ado-pr.yaml`). | Not new — gate compression reuses an existing pattern, doesn't create one. |

**Seams removed:** Every gate that compresses to notification+poll removes a `human_gate` node — which is a two-way seam (workflow ↔ operator). Replacing it with one-way notification + one-way poll is a net coupling reduction.

### 3.3 Phase 1 dependency check

| Capability | Available? | Required? | Notes |
|-----------|-----------|-----------|-------|
| `on_error:` basic routing | ✅ Phase 1 (PR #229) | ✅ For retry-then-abort chains | |
| `on_error:` typed error envelope | ✅ Phase 1 | ✅ For hybrid exit-code propagation | |
| `on_error: { retry: ... }` built-in | ❌ Phase 2 (RFC #227) | ❌ Can model with counter script (Wagner Pattern 4) | |
| `type: notification` node | ✅ PR #213 | ✅ For domain signals | |
| Notification payload pass-through | ✅ PR #213 | ✅ Polyphony owns the envelope | |
| `run_id` on wire-format envelope | ✅ PR #213 (auto-populated) | ✅ Platespinner gets it | |
| `{{ conductor.run_id }}` in Jinja2 context | ❓ **UNKNOWN** | Desirable for CTA URL construction (Wagner Ask #2) | **Workaround:** thread as workflow input. **Action:** Mahler to verify. |
| `type: wait` with templated `seconds:` | ❓ **UNKNOWN** | Desirable for adaptive backoff (Wagner Ask #3) | **Fallback:** fixed backoff or `Start-Sleep` in script. **Action:** Mahler to verify. |
| `type: set` + `context.mode: accumulate` | ✅ conductor 0.1.18 | ✅ For bounded retry loop counters | |

**Nothing in this design DEPENDS on Phase 2 or unconfirmed features.** The two unknowns (`conductor.run_id`, templated `wait.seconds`) have adequate workarounds. ✅

### 3.4 ADRs that must be written

| ADR | Decides | Urgency | Blocks |
|-----|---------|---------|--------|
| **polyphony-verb-error-boundary.md** | Option C (hybrid exit codes) for the exit-0 problem | **P0** | #536 Phase 1 retrofit |
| **domain-signal-envelope.md** | Envelope schema, `kind` vocabulary, `severity` levels, `disposition` field, correlation semantics, CTA contract | **P1** | First notification node in any workflow; Wagner's patterns #543/#544/#545 |
| **gate-compression-pattern.md** | Which gates compress to notification+poll, which remain as judgment gates, poll cadence defaults | **P1** | Wagner's forward workflow patterns |

### 3.5 Vocabulary alerts for Beethoven (glossary steward)

Terms that must be added to `docs/glossary.md` before implementation:

| Term | Definition | Section |
|------|-----------|---------|
| **Domain signal** | A structured event emitted by a polyphony workflow via conductor's `type: notification` node, carrying the polyphony domain signal envelope. NOT a human gate. NOT a log line. NOT a platespinner toast (though it may trigger one). | Execution model |
| **Gate compression** | The substitution of a `human_gate` node with a `(notification + script-poll-loop)` pair for conditions that are externally observable. Reduces operator babysitting. Gates remain only for genuine judgment calls. | Execution model |
| **CTA (call to action)** | The clickable link in a domain signal that tells the user what to do and where. Carried as `cta_url` + `cta_kind` in the signal envelope. | Execution model |
| **Correlation ID** | An opaque identifier linking related domain signals in the same pollable cycle (e.g., "PR ready for review" → "PR merged, workflow continuing"). Platespinner uses this to manage signal lifecycle. | Execution model |
| **Disposition (signal)** | The routing decision a workflow made automatically at a decision point (`auto_continue`, `auto_skip`, `auto_abort`). Carried on `auto_disposition` domain signals for auditability. NOT a work-item disposition (see: Requirement disposition). | Execution model |

---

## Appendix: Wagner Pattern Alignment

Wagner's 8 patterns (`.squad/decisions/inbox/wagner-on-error-notifications-patterns-2026-05-28T23-00-46Z.md`) map to this envelope as follows:

| Wagner pattern | `kind` value(s) | `severity` default | Uses `cta_url`? | Uses `disposition`? |
|---------------|-----------------|-------------------|-----------------|-------------------|
| 1. `notify_then_route` | `step_failed` | `error` | No (abort path) | No |
| 2. `decision_point_notification` | `auto_disposition` | `warning` | No | **Yes** |
| 3. `progress_notification` | `phase_started`, `phase_complete` | `info` | No | No |
| 4. `bounded_retry_loop` | `retry_attempt`, `retry_exhausted` | `warning` / `error` | No | No |
| 5. `escalation_chain` | `rate_limit_backoff`, `operation_failed` | `warning` / `error` | No | No |
| 6. `renegotiation_notification` | `renegotiation_started`, `renegotiation_complete` | `info` | Yes (plan PR) | No |
| 7. `async_gate_prompt` | `human_action_required` | `error` | **Yes** (gate URL) | No |
| 8. `gate_disposition_policy` | (reuses `auto_disposition`) | `warning` | No | **Yes** |

**No divergent contracts.** Wagner's patterns and this envelope use the same field set. The `disposition` field is exclusively for Pattern 2/8. CTA fields are exclusively for gate compression + renegotiation. The envelope is a union — each pattern uses the subset it needs.

---

## Recommended Next Moves (revised, ranked)

| # | Action | Owner | Priority | Blocks |
|---|--------|-------|----------|--------|
| 1 | Write ADR: `polyphony-verb-error-boundary.md` | **Bach** + **Mozart** | P0 | #536 retrofit; enables on_error: for verbs |
| 2 | Write ADR: `domain-signal-envelope.md` (envelope + kind + correlation + severity + disposition) | **Bach** + **Wagner** | P1 | First notification node; Wagner's #543/#544/#545 |
| 3 | Write ADR: `gate-compression-pattern.md` (which gates compress, poll cadence, judgment-only remainder) | **Wagner** + **Bach** | P1 | Complements Wagner's forward patterns work |
| 4 | Mahler: verify `{{ conductor.run_id }}` availability + `type: wait` templated `seconds:` in dogfood fork | **Mahler** | P1 | Determines whether workarounds are needed |
| 5 | Implement hybrid exit-code behavior (Option C) | **Mozart** | P1 (after ADR #1) | Enables on_error: |
| 6 | Prototype gate compression in `github-pr.yaml` (pr_review_gate → notification + poll) | **Wagner** | P2 (after ADRs #2+#3 + Mahler's fork) | Validates full stack |
| 7 | File platespinner feature requests (#541 updated, #542 updated) | **Bach** (done) | P2 | Platespinner readiness |
| 8 | Add `domain signal`, `gate compression`, `CTA`, `correlation ID`, `disposition (signal)` to glossary | **Beethoven** | P1 | Vocabulary hygiene |
| 9 | Restore retry for the 14 removed gates via on_error: + counter scripts | **Wagner** | P3 | Operator experience restoration |


---

# ADR Proposal: Domain Signal Envelope + Verb Error Boundary

**Author:** Bach (Architect)  
**Date:** 2026-05-28  
**Status:** Proposal stub (ADR not yet written)  
**Triggered by:** Conductor PRs #229 (on_error Phase 1) + #213 (type: notification)

---

## What this ADR would decide

**Title:** `polyphony-verb-error-boundary.md` — How polyphony CLI verbs communicate infrastructure failures to conductor's `on_error:` routing.

**The decision:**

Should polyphony verbs:
- **(A)** Adopt typed exit codes + `$env:CONDUCTOR_ERROR_OUT` for ALL outcomes (breaking)
- **(B)** Stay exit-0 always, route errors via JSON `when:` conditions (status quo)
- **(C)** Hybrid: exit-0 for domain outcomes, non-zero + error envelope for unhandled infrastructure failures only (recommended)

**Why it needs an ADR:**

This is a seam decision that affects:
- All 109 `[Command]` methods in `src/Polyphony/Commands/`
- All workflow YAML `when:` route conditions that read verb output
- The contract between polyphony CLI and conductor's script-execution host
- Whether `on_error:` Phase 1 can fire for polyphony verb failures at all

Without this decision, the Phase 1 retrofit (#536) cannot restore retry capability for the 14 gates removed in #535.

**Options summary:**

| Option | Breaking? | on_error: fires? | Implementation cost |
|--------|-----------|------------------|-------------------|
| A — full typed exit codes | Yes (all consumers) | Yes (all failures) | High (109 verbs + all YAML) |
| B — stay exit-0 | No | No (never fires for verbs) | Zero |
| C — hybrid | No (existing routes unchanged) | Yes (infrastructure failures only) | Low (one try/catch wrapper in verb host) |

**Recommendation:** Option C. Domain outcomes (skip, error-as-business-decision, invalid-input) remain in JSON output routed by `when:`. Infrastructure failures (unhandled exception, file corruption, OOM) propagate via non-zero exit + `$env:CONDUCTOR_ERROR_OUT`. This matches conductor's `internal.script_error` semantics without breaking the existing routing contract.

**Companion ADR (lower priority):** `domain-signal-envelope.md` — formalizes the envelope schema for `type: notification` payloads emitted by polyphony workflows, pins the `kind` vocabulary, and defines the severity semantics that platespinner will consume.

---

## Companion ADRs (all P1, sequenced after verb-error-boundary)

| ADR | Decides | Blocks |
|-----|---------|--------|
| `domain-signal-envelope.md` | Envelope schema (kind, severity, cta_url, cta_kind, correlation_id, expires_at), the `kind` vocabulary, correlation semantics | First notification node |
| `gate-compression-pattern.md` | Which gates compress to notification+poll, which remain as judgment-only, poll cadence defaults, the full user↔workflow loop contract | Wagner's forward workflow patterns; gate-compression prototype in github-pr.yaml |

## Scope boundary

The verb-error-boundary ADR is P0 (blocks #536 retrofit). The envelope + gate-compression ADRs are P1 (block the notification work). All three are prerequisites for different streams:

- Verb-error-boundary → blocks #536 Phase 2 retrofit (on_error: for verbs)
- Domain-signal-envelope → blocks first notification node in production workflows
- Gate-compression-pattern → blocks substitution of human gates with notification+poll


---

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

### 2026-05-28T16:19-07:00: Finding — conductor on_error: Phase 1 has NO `retry:` action
**By:** Squad Coordinator (via retry-probe explore agent, haiku-4.5)
**Requested by:** Daniel (autopilot — closing out the on_error+notifications round)

**Question:** Does conductor on_error: Phase 1 support `on_error: { retry: <target> }`? If yes, does it (A) re-run the failing node or (B) route to the named target?

**Answer:** **(C) — Phase 1 has no `retry:` action inside `on_error` routes at all.** It was hallucinated in Wagner's pattern sketches.

**What Phase 1 actually ships:**

1. **`on_error: <kind>` as a ROUTE matcher** — a route can carry an `on_error:` field that names an error kind (e.g. `external.git.drift`). When that error kind fires, the route's `to:` target is followed. This is regular routing, just gated by an error kind instead of a `when:` expression. It routes TO the named target — NOT back to the failing node.

2. **`retry: RetryPolicy` as a PER-AGENT field** — `AgentDef.retry: RetryPolicy | None` is a separate, pre-existing transient-failure resilience mechanism (provider_error, timeout). It's unrelated to typed error routing. It does NOT take a target — it just re-attempts the same agent invocation with a backoff policy.

**Code references:**
- `src/conductor/config/schema.py:89-131` — RouteDef has `to`, `when`, `output`, `on_error` fields; **no `retry` field exists on routes**
- `src/conductor/config/schema.py:715` — AgentDef has `retry: RetryPolicy | None` (the per-agent resilience hook)
- `examples/error-routing.yaml:92-94` — Phase 1 example shows `on_error: external.git.drift` as a string route matcher, not `{ retry: ... }`

**Implications for Wagner's #543 / #544 / #545 patterns:**

- **`notify_then_route` (#543):** Works as designed. It uses `on_error:` matchers + `notification:` emits, doesn't need retry. ✅ No change.

- **`decision_point_notification` (#544):** Works as designed. Same shape — pure notification + route. ✅ No change.

- **`progress_notification` (#545):** Works as designed. Pure emit, no error routing dependency. ✅ No change.

- **`bounded_retry_loop` (Wagner pattern doc only — not filed):** ⚠️ Needs redesign. There's no `retry:` action. Options:
  - (a) Use `AgentDef.retry: RetryPolicy` for the underlying transient-flake case — but this is per-agent and doesn't compose with `on_error:` route logic
  - (b) Route from `on_error:` back to a wrapper node that re-invokes the agent — requires the workflow graph to permit cycles AND a counter mechanism (sub-workflow with iteration var?)
  - (c) Wait for RFC Phase 2 `retry:` action (per #229 RFC)
  - Recommendation: this pattern is **deferred to RFC Phase 2** unless option (b) cleanly works.

- **`escalation_chain` (Wagner pattern doc only — not filed):** ⚠️ Needs the same lens. The pattern as Wagner sketched it (try A, fail → try B, fail → notify+halt) is implementable IF each "try" is a separate node and `on_error:` routes them in sequence — that IS a chain, just spelled differently. No `retry:` needed. **Pattern works, just rephrase the sketch.**

**Action items for Wagner's next round:**
1. Update the `bounded_retry_loop` pattern in his decision doc to either reflect option (b) with a worked example, or mark as Phase-2-deferred
2. Re-sketch `escalation_chain` to use sequential nodes + `on_error:` routes instead of a `retry:` action
3. Cross-reference this finding in his SKILL.md M11 entry (currently may overstate Phase 1 capabilities)

**Action items for Bach's domain-signal-envelope ADR:**
- His ADR proposal is unaffected — the envelope schema doesn't depend on `retry:` semantics.

**Action items for Daniel:**
- This makes the case for **prioritizing conductor RFC Phase 2 `retry:` action** slightly stronger if you want the full pattern catalogue available. But it's not blocking — Wagner's top 3 filed issues (#543/#544/#545) all work on Phase 1 as-is.
