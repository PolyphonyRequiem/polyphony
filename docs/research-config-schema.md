# `.polyphony-config/profile.yaml` — `research:` Block Schema Reference

Schema and validation rules for the `research:` block within
`.polyphony-config/profile.yaml`. This block configures polyphony's
sibling-repository research storage abstraction.

> **Status:** Parsed but not consumed. No workflow or command reads the
> `research:` block at runtime today. The schema ships ahead of consumption
> so it stabilises under tests before wiring.

The schema is the C# class `ResearchConfig` in
`src/Polyphony/Configuration/ResearchConfig.cs`. Validation rules are in
`src/Polyphony/Configuration/ResearchConfigValidator.cs`. The loader is
`src/Polyphony/Configuration/ResearchConfigLoader.cs`.

---

## Structure

```yaml
research:
  repo: owner/name                         # str, REQUIRED — sibling research repo
  branch: main                             # str, default "main"
  platform: github                         # str, "github" | "ado", default "github"
  auth:
    env_var: RESEARCH_PAT                  # str, REQUIRED — env var holding the PAT
  paths:
    archive_root: research/                # str, default "research/"
    scratch_root: research/scratch/        # str, default "research/scratch/"
```

## Fields

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `repo` | string | **yes** | — | Sibling research repository in `owner/name` form. |
| `branch` | string | no | `main` | Branch to read from in the research repository. |
| `platform` | string | no | `github` | Hosting platform. `github` or `ado`. |
| `auth.env_var` | string | **yes** | — | Name of the environment variable holding a PAT for the research repo. |
| `paths.archive_root` | string | no | `research/` | Root directory in the research repo for archived artefacts. POSIX-style, relative. |
| `paths.scratch_root` | string | no | `research/scratch/` | Scratch directory in the source repo for ephemeral artefacts. POSIX-style, relative. |

## Validation Rules

| Rule | Severity | Description |
|------|----------|-------------|
| R-1 | error | `research.repo` is required (non-empty, non-whitespace). |
| R-2 | error | `research.repo` must be in `owner/name` form — exactly two non-empty, non-whitespace segments separated by `/`. |
| R-3 | error | `research.platform` must be `github` or `ado`. |
| R-4 | error | `research.auth` block is required. |
| R-5 | error | `research.auth.env_var` must be non-empty when `auth` is present. |
| R-6 | error | Path fields (`archive_root`, `scratch_root`) must be POSIX-style (no backslashes) and relative (not absolute). |

## Defaults

When fields are omitted, the loader applies these defaults:

- `branch` → `"main"`
- `platform` → `"github"`
- `paths.archive_root` → `"research/"`
- `paths.scratch_root` → `"research/scratch/"`

The `paths` block itself is created if absent.

## Examples

### Minimal valid block

```yaml
research:
  repo: PolyphonyRequiem/polyphony-research
  auth:
    env_var: RESEARCH_PAT
```

### Full block with all fields

```yaml
research:
  repo: PolyphonyRequiem/polyphony-research
  branch: develop
  platform: ado
  auth:
    env_var: MY_RESEARCH_TOKEN
  paths:
    archive_root: docs/research/
    scratch_root: tmp/scratch/
```

## Loader Behaviour

- **File missing:** `LoadOrDefault` returns `null` — no exception.
- **No `research:` key:** Returns `null`.
- **Block present, valid:** Returns `ResearchConfig` with defaults applied.
- **Block present, invalid:** Throws `InvalidOperationException` with all
  validation errors concatenated.

## Round-Trip

`ResearchConfigLoader.Serialize` → `ResearchConfigLoader.Parse` preserves
all user-supplied values plus applied defaults. The serializer emits only
the `research:` block (other `profile.yaml` keys are not round-tripped).
