# Versioning Strategy — Bundled SemVer for CLI + Workflow Registry

> **Status:** Accepted (2026-05).
> **Driver:** Workflow vocabulary cleanup carry-forward; pre-condition for
> external (Microsoft-internal) consumers and for any honest "upgrade
> polyphony" story.
> **Supersedes:** the de-facto status quo where the CLI shipped untagged
> (`0.0.0-alpha.0.66`), workflow YAMLs declared `1.0.0` / `1.1.0`, and the
> registry `index.yaml` claimed `0.1.0` for everything. Three sources, all
> different, none authoritative.

## Context

This repo ships **two artifacts** from a single git history:

1. **Polyphony CLI** — a .NET binary distributed as `polyphony` (`src/Polyphony/`).
2. **Conductor workflow registry** — 9 YAMLs + `index.yaml` under
   `.conductor/registry/` consumed via `conductor registry add polyphony
   PolyphonyRequiem/polyphony`.

The two artifacts share a **contract surface**: workflow YAMLs shell out to
specific CLI verbs, parse specific JSON shapes, and depend on the JSON
schemas (`Models/*Result.cs`) being stable. The contract is enforced at
the JSON output layer (`tests/Polyphony.Tests/Commands/JsonOutputContractTests.cs`)
but there is no way for a workflow YAML to declare the minimum CLI version
it requires, and no way for the CLI to refuse a workflow it doesn't speak
the contract for.

Three failure modes today:

- **Drift.** `index.yaml` says `0.1.0`; YAMLs say `1.0.0`/`1.1.0`; binary
  reports `0.0.0-alpha.0.66`. No single source of truth.
- **Silent misroute.** A user pinning to `polyphony-full@polyphony@1.0.0`
  via `conductor` could in principle get a YAML that calls
  `polyphony branch route --some-new-flag`, against an older CLI that
  silently treats the flag as positional and produces wrong output.
- **No release pipeline.** `publish-local.ps1` is the only install path.

Conductor itself supports `<workflow>@<registry>@<version>` invocation
grammar (`index.yaml`'s `versions: [...]` per-workflow array is consulted),
but `conductor registry add` does **not** accept `--ref` / `--tag` for
github sources, so distributed consumers cannot pin a registry version via
the registry mechanism today. We pin **at the workflow level** instead.

## Decision

### Bundled SemVer

**One git tag drives both artifacts.** Cutting `v1.2.3` ships:

- CLI binary version `1.2.3` (driven by MinVer reading the git tag).
- Every workflow YAML's `workflow.version: "1.2.3"`.
- Every entry in `index.yaml`'s `versions: [...]` list ends with `"1.2.3"`.

Acceptable cost: a workflow-only patch fix (e.g. fix a typo in a prompt)
forces a bumped CLI release with no code changes. Worth it at our scale —
keeps the contract surface to a single number.

### Three-layer version model

| Layer | Source of truth | Shape | Mutability |
|---|---|---|---|
| **Release truth** | Git tag (`v1.2.3`) | Single SemVer | Append-only |
| **Self-description** | `workflow.version:` in each YAML | Single SemVer | Overwritten on each release |
| **Operational manifest** | `index.yaml` `versions: [...]` per workflow | Append-only list | Append-only |

The `index.yaml` `versions` array is the **append-only release list** —
once a version is in there, it stays. New releases append. The workflow
YAML's `workflow.version` is just the file's self-description: this is
what this YAML claims to be at this commit. The git tag is the source of
truth for what was actually released.

For v1: hard-reset `index.yaml` versions arrays to `["1.0.0"]` (the
existing `0.1.0` is junk and nobody has pinned to it). From v1.1.0
onward, the release script appends new entries to each array.

### Min-CLI-version declaration and enforcement

**Declaration.** Every workflow YAML carries:

```yaml
workflow:
  name: polyphony-full
  version: "1.0.0"
  metadata:
    min_polyphony_version: "1.0.0"   # required field
```

The author bumps `min_polyphony_version` whenever the workflow starts
calling a verb / flag / JSON field added in a later CLI release. Pester
lint (added in WI-2/WI-3 of the rollout epic) enforces:

- Every YAML declares `metadata.min_polyphony_version`.
- Every YAML's `min_polyphony_version` is `≤` its `workflow.version` (a
  YAML cannot require a version newer than itself).

**Enforcement.** Lives in `polyphony state preflight` and `polyphony state
preflight-lite` — **not** raw `polyphony health`. Two reasons:

1. The conductor `preflight_gate` already understands the preflight JSON
   schema (the list of `PreflightCheck` records with `passed` / `detail`).
   Wedging the version assertion into `health` would force the gate to
   parse a second JSON shape.
2. Sub-workflows that invoke `preflight-lite` standalone (e.g. for testing
   or for partial re-entry) get the same enforcement automatically — no
   apex-only hole.

The verb gains two flags:

- `--workflow-yaml <path>` — path to the workflow YAML. The verb parses
  `metadata.min_polyphony_version` itself and uses it as the required
  minimum.
- `--required-version <semver>` — explicit override (testing seam). When
  both are supplied, `--required-version` wins.

The workflow YAMLs invoke the verb with
`--workflow-yaml "{{ workflow.file }}"`. `{{ workflow.file }}` is one of
the four template variables conductor exposes in the workflow context
(`workflow.input`, `workflow.dir`, `workflow.file`, `workflow.name`).
**`workflow.metadata.X` is NOT exposed in template context** — that's why
the verb reads the YAML directly rather than receiving a substituted
literal as an arg.

### Mismatch is a hard fail

The preflight check returns `passed: false` when the running CLI's SemVer
is `<` the declared minimum. The existing `preflight_gate` routes a
failed preflight to retry/abort only — there is no "Proceed Anyway"
human gate option. **Silent misroutes are exactly what this guard exists
to prevent.** Bypass would defeat the purpose.

### Mid-run upgrade hole

For v1, document "do not upgrade polyphony mid-run" in the release notes.
Re-checking the version on resume can be a follow-up if the hole bites.

### SemVer comparator strips build-metadata

`AssemblyInformationalVersion` includes `+<git-sha>` build-metadata after
a tagged commit's first commit (e.g. `1.0.0+a1b2c3d`). Per SemVer, build
metadata is ignored for precedence. The comparator strips everything from
`+` onward before comparing.

(Prerelease identifiers like `1.1.0-alpha.5` are NOT stripped — they
compare per SemVer rules, with `1.1.0-alpha.5` < `1.1.0`.)

## Consequences

### Positive

- One number to bump, one tag to cut, one truth.
- Authors get a static guarantee: if the YAML lints clean, the running
  CLI is new enough.
- Operators get a runtime guarantee: preflight fails loudly on mismatch.
- ADR captures the rule in `docs/decisions/` so the next contributor can
  find it.

### Negative

- Workflow-only patch releases require a CLI bump (and vice versa). No
  independent version trains.
- Distributed consumers using `conductor registry add` cannot pin to a
  specific registry version (conductor limitation). They get HEAD of
  whatever branch the registry source resolves to. The min-version check
  is the floor; new YAMLs that require newer CLIs will fail-fast on
  preflight.

### Neutral

- Existing `0.1.0` and `1.1.0` markers in `index.yaml` and YAMLs are
  destroyed in the v1 reset. Nobody has consumed these so the cost is
  zero.

## Related work

- WI-1: this ADR + skill updates.
- WI-2: drift fix (align YAMLs and index.yaml to `1.0.0`) + Pester lint
  + cut `v1.0.0` git tag.
- WI-3: implement `--workflow-yaml` / `--required-version` flags on
  `state preflight` / `preflight-lite`; add `metadata.min_polyphony_version`
  to all 9 YAMLs; wire the preflight agents to pass the flag.
- WI-4: `.github/workflows/release.yml` cuts self-contained single-file
  binaries on `v*` tags; README and onboarding-guide cleanup (remove
  stale AOT claims; add "install from release" path).

## References

- `src/Polyphony/Polyphony.csproj:2-9` — AOT/trim/single-file publish is
  intentionally disabled (YamlDotNet runtime reflection workaround). v1
  release binaries are self-contained NON-AOT; AOT re-enablement is a
  separate future Epic.
- `src/Polyphony/Commands/StateCommands.cs:300-313` — existing pattern
  for reading `AssemblyInformationalVersion`.
- `.github/skills/conductor-mechanics/references/m07-output-map-vs-schema.md`
  — what is and isn't in the conductor template context.
- Conductor invocation grammar: `<workflow>@<registry>@<version>` (the
  version segment looks up the YAML registered under that version in
  `index.yaml`).
