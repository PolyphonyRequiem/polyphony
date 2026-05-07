# Polyphony State-Effects Catalog (Living)

> **Status: Bootstrapping.** This catalog is grown incrementally as we
> exercise the apex pipeline. It is **not** a completeness guarantee and
> **not** a substitute for runtime instrumentation (deferred — see
> follow-up issues). It is "Strategy 1" of the four discussed in PR #159's
> wake (mechanical side-effect catalog from source); strategies 2–4
> (instrumentation, property-based testing, formal concurrency model)
> remain future work.

## Why this exists

Bug #5 (the `root declare` DI NPE) and bug #4 (`root declare` never wired
into apex-driver) both showed that a call-site signature audit (PR #159)
is insufficient. Per-step **pre-conditions** and **post-conditions**
matter as much as the call signature. This catalog captures, for each
verb and helper script, what state it expects on entry and what state it
mutates on success — so workflow review can audit *intent* against
*effect*, not just *signature*.

## How to use it

When modifying or adding a workflow agent, find the verb / script it
calls in §Verbs or §Helper scripts. Cross-check:

1. Does the agent honor every pre-condition? (e.g. is upstream state
   guaranteed by an earlier agent?)
2. Does the workflow handle every documented failure mode?
3. After this agent succeeds, does any later agent rely on a side
   effect that this verb does **not** produce? (a missing side effect is
   bug #4's class — verb is correct, workflow forgot to call it.)

When discovering a new verb / script effect during dogfooding, add it
here. PRs touching workflow YAMLs should update this catalog when they
introduce new dependencies.

## Conventions

- **Pre**: must be true before the call succeeds.
- **Post**: guaranteed true after exit code 0.
- **ADO writes**: tag stamps, state transitions, field patches.
- **Git writes**: branch creates/pushes, commits, merges.
- **FS writes**: files under `.polyphony/`, worktree directories.
- **Idempotent**: repeating the call with the same inputs is safe.
- **Exit semantics**: when the verb fails, what does it emit and how does
  it exit. Distinguishes routing-style (exit 0 + JSON envelope) from
  hard-error (non-zero exit + stderr).

---

## Verbs

### `polyphony state preflight --work-item <N>`
- **Purpose**: health-check that ADO + git + twig are reachable for `N`.
- **Pre**: workflow inputs valid; CLI on PATH.
- **Post**: no state mutation. JSON envelope reports `{success, checks: [...]}`.
- **Exit semantics**: routing-style — exit 0 + `success: false` on health failure.
- **Idempotent**: yes (read-only).

### `polyphony branch ensure-feature --branch <name> [--from <ref>]`
- **Purpose**: idempotently create + push `feature/{root}` branch.
- **Pre**: git repo writeable; remote reachable; base ref exists.
- **Post**: branch exists locally **and on remote**; emits
  `{action: "created"|"existed", remote_existed, pushed, created_from}`.
- **Side effects**: git ref create + push.
- **Exit semantics**: hard-error on git failure (non-zero exit).
- **Idempotent**: yes.
- **Gotcha**: pre-PR #159 the workflow passed positional instead of
  `--branch foo` — verb didn't recognize the arg, exited 0, branch was
  never created. Surface bug class: CLI exit-0-on-unknown-arg.

### `polyphony manifest init --root-id <N> --platform-project <s> [--path P] [--force]`
- **Purpose**: create `.polyphony/run.yaml` for the run-root.
- **Pre**: working directory is a git repo; `.polyphony/` writeable; no
  existing manifest at `--path` (unless `--force`).
- **Post**: manifest file exists at `--path`; emits `{success, path,
  platform_project, root_id, action: "created"}`.
- **Side effects**: FS write under `.polyphony/`.
- **Exit semantics**: routing-style success / hard-error on write failure.
- **Idempotent**: only with `--force`. Without force, fails on existing file.

### `polyphony manifest read [--path P]`
- **Purpose**: read + parse the manifest.
- **Pre**: manifest file exists.
- **Post**: emits `{path, manifest: {root_id, platform_project, schema, ...},
  computed_topology_hash, topology_hash_matches}`.
- **Output shape gotcha**: manifest fields are nested under `.manifest`,
  not at top level. Code that reads `result.root_id` (instead of
  `result.manifest.root_id`) gets `null`.
- **Side effects**: none (read-only).
- **Idempotent**: yes.

### `polyphony root declare --work-item <N>`
- **Purpose**: stamp `polyphony:root` tag on work item N. Required for
  every downstream `polyphony root resolve` call to succeed without
  hitting the fallback gate.
- **Pre**: ADO reachable; work item N exists.
- **Post**: ADO `System.Tags` for N includes `polyphony:root`; emits
  `{work_item_id, changed, tags_before, tags_after}`.
- **Side effects**: ADO field patch (System.Tags).
- **Exit semantics**: routing-style on item-not-found / twig-failure
  (exit 1 + JSON envelope with `error`).
- **Idempotent**: yes (`changed: false` if tag already present).
- **History**: had a DI registration bug (`ScopeCommands` not registered)
  that made the verb NPE on every call until PR #160 fixed it.
- **Workflow integration**: documented at `docs/polyphony-tags.md:169`
  as part of tree-walker entry. Wired into apex-driver in PR #161.

### `polyphony root resolve --work-item <N>`
- **Purpose**: walk ancestors of N looking for `polyphony:root` tag.
- **Pre**: ADO reachable; N exists.
- **Post**: emits `{work_item_id, resolved_root_id, ancestors_walked,
  fallback_required}`. `fallback_required: true` when no tagged
  ancestor found within walk budget.
- **Side effects**: none (read-only).
- **Idempotent**: yes.

### `polyphony worklist build --root-id <N> [--manifest-path P] [--json]`
- **Purpose**: walk the work-item tree under N and emit dispatch waves.
- **Pre**: manifest exists with matching `root_id`; ADO reachable.
- **Post**: emits `{root_id, waves: [{wave_index, items: [...]}, ...]}`.
- **Side effects**: none (read-only).
- **Idempotent**: yes.
- **Gotcha**: pre-PR #159 the workflow passed positional instead of
  `--root-id N` — verb errored, emitted no JSON, downstream
  `output.waves` was `Undefined`.

### `polyphony edges check <ApexId> [--render json]`
- **Purpose**: detect dispatch-blocking dependency conflicts within the
  scope of `ApexId`.
- **Pre**: manifest + worklist computable for ApexId.
- **Post**: emits `{has_conflicts, conflicts: [...]}`.
- **Side effects**: none (read-only).
- **Idempotent**: yes.
- **History**: PR #158 made the apex id positional (was `--work-item N`).

### `polyphony validate --work-item <N> --event <E>`
- **Purpose**: validate that event E is allowed on work item N per
  process config.
- **Pre**: process config loaded.
- **Post**: emits `{success, allowed, current_state, target_state, ...}`.
- **Side effects**: none.
- **Idempotent**: yes.

### `polyphony plan load-type --work-item <N>`
- **Purpose**: load type-specific planning context for a work item.
- **Pre**: work item exists in ADO; `.conductor/work-item-types/`
  configured for the project.
- **Post**: emits `{type, definition, decomposition_guidance, template}`
  on success; `{error}` envelope on failure (e.g. type definition
  missing, work item not found).
- **Side effects**: none.
- **Idempotent**: yes.
- **Field-name gotcha**: the success field is `type`, NOT `type_name`.
  Bug #6 (dogfood apex #3043, 2026-05-07) had a workflow reference
  `type_loader.output.type_name` — render returned the literal string
  `type:` at lint time, then exploded with strict_undefined at
  runtime. Pinned by lint check `open-questions-policy-bad-type-field`
  in `lint-plan-level.ps1` as of this PR.

---

## Helper scripts

### `manifest-bootstrap.ps1 -ApexId N -Organization O -Project P [-ManifestPath]`
- **Purpose**: create-or-validate `.polyphony/run.yaml` for the run.
- **Pre**: ADO reachable; polyphony on PATH.
- **Post**: manifest exists with `root_id == N`; emits routing-style
  envelope `{success, action: "created"|"reused", root_id, ...}`.
- **Side effects**: may invoke `polyphony manifest init` (FS write).
- **Idempotent**: yes (validates existing manifest's `root_id` matches;
  refuses on mismatch with `error_code: manifest_root_mismatch`).
- **Limitations**: does **not** validate manifest's `platform_project`
  matches the requested run's project (gap noted in PR #159 smoke).
  Does **not** validate topology-hash drift on resume (deferred).

### `lifecycle-router.ps1 -WorkItemId N -ApexId A`
- **Purpose**: classify item N's next dispatch lifecycle (plan-level /
  actionable / implement-pg / feature-pr).
- **Pre**: ADO reachable.
- **Post**: emits `{success, route: <enum>, ...}` consumed by
  `apex-item-dispatch.yaml`'s branch-on-router.
- **Side effects**: none (read-only — wraps `polyphony state next-ready`).
- **Idempotent**: yes.

### `worktree-manager.ps1 -Operation spawn|teardown -WorkItemId N -BaseBranch B`
- **Purpose**: create or remove a per-item git worktree at
  `..\polyphony-{N}\` based on `B`.
- **Pre**: git repo writeable; for `spawn`, `B` exists on remote.
- **Post**: for `spawn`, sibling worktree directory exists, branch
  checked out; emits `{success, worktree_path, branch}`.
- **Side effects**: git worktree add/remove; sibling FS directory.
- **Idempotent**: spawn is idempotent (re-uses existing worktree);
  teardown is idempotent (no-op if absent).

### `wave-integrator.ps1 -ApexId A -WaveIndex W -ManifestPath P`
- **Purpose**: integrate a completed wave's per-item branches into the
  apex feature branch.
- **Pre**: all impl PRs in wave W have merged into their MG branches;
  edges check reports no conflicts.
- **Post**: MG branches for wave W merged into `feature/{A}`; emits
  `{success, integrated_mg_paths, ...}`.
- **Side effects**: git merge; PR creates / merges (depending on policy).
- **Idempotent**: TBD — exercise during dogfood, document here.
- **History**: PR #159 fixed call-site for `polyphony edges check`
  (was `--work-item`, now positional).

---

## Open questions / gaps

- **`platform_project` drift validation** (manifest reuse): bootstrap
  helper only validates `root_id` match, not `platform_project`.
  Tracked: [#166](https://github.com/PolyphonyRequiem/polyphony/issues/166).
- **Topology-hash drift on resume**: branch-model.md ADR specifies the
  resume contract (same hash → resume; differing hash + materialized
  branches → human gate) but no agent currently enforces this.
  Tracked: [#167](https://github.com/PolyphonyRequiem/polyphony/issues/167).
- **CLI exits 0 on unrecognized args**: structural mitigation shipped
  via the contract test suite in PR #159; the underlying CLI behavior
  is tracked: [#165](https://github.com/PolyphonyRequiem/polyphony/issues/165).
- **Jinja template field references go un-checked at lint time**: only
  surface at runtime via `strict_undefined`. Bug #6 (dogfood apex
  #3043, 2026-05-07) had `plan-level.yaml` reference
  `type_loader.output.type_name` when `polyphony plan load-type` emits
  `type` — slipped past `conductor validate` and PR #157's lint sweep,
  exploded only when the `open_questions_policy` step actually
  executed. Cross-checking template field references against verb
  output schemas would catch this class statically — partially
  addressable under [#163](https://github.com/PolyphonyRequiem/polyphony/issues/163)
  (property-based testing) but really wants its own pass.
- **Wave integration idempotency**: not yet exercised; document after
  first wave-integration smoke.
- **`apex-wave-dispatch.yaml` and per-lifecycle sub-workflows**: not
  yet cataloged. Add as we exercise them.

## Future work tracked separately

- Strategy 2 — runtime trace mode for polyphony CLI (per-verb
  side-effect events).
  Tracked: [#162](https://github.com/PolyphonyRequiem/polyphony/issues/162).
- Strategy 3 — property-based / state-machine workflow testing.
  Tracked: [#163](https://github.com/PolyphonyRequiem/polyphony/issues/163).
- Strategy 4 — formal concurrency model (for_each parallelism, MG
  isolation_scope, run-lock, cross-MG code-dep rebases,
  parent-plan-generation lock).
  Tracked: [#164](https://github.com/PolyphonyRequiem/polyphony/issues/164).
