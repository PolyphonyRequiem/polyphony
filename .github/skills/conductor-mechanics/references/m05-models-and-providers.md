# M5: Provider & Model Namespaces

Conductor talks to LLMs through a **provider** abstraction. Each provider has
its own model namespace; the canonical context-window/pricing table lives in
`engine/pricing.py:DEFAULT_PRICING`, not in any provider file.

## Providers

Three values are accepted at top-level `runtime.provider:` (`config/schema.py:734`):

| Provider | Status | Default model |
|---|---|---|
| `copilot` | Working | `gpt-4o` (`providers/copilot.py:198`) |
| `claude` | Working (direct Anthropic API) | `claude-3-5-sonnet-latest` (`providers/claude.py:173`) |
| `openai-agents` | **TRAP** — accepted at config load, raises `NotImplementedError` at first agent execution (`providers/factory.py:73-77`) |

Two values are accepted at per-agent `agent.provider:` override (`config/schema.py:429`): `copilot` and `claude`. **Per-agent override is allowed** — you can route high-precision agents to Claude and keep cheap probes on Copilot inside one workflow.

`agent.model` is a Jinja template (`executor/agent.py:163-165`) — e.g. `model: "{{ workflow.input.tier }}"` works.

## Symptom of a wrong model name

```
Model "claude-opus-4-1m" is not available.
```

…or a `pricing.context_window = None`, in which case the engine cannot trim
context or estimate cost — but the run silently proceeds without those
guardrails.

## No CLI override

`conductor run` does **not** accept `--model`. Whatever the workflow YAML
declares is what gets sent to the provider.

## Models are not validated at workflow load

The validator (`config/validator.py`) does not check `agent.model` against any
allowlist. Bad model names fail on first agent execution, not at `conductor
validate`.

The `claude` provider does call `client.models.list()` on `validate_connection`
and **warns** if the default model is missing (`providers/claude.py:307-332`).
The `copilot` provider does not enumerate models — typos die silently until
the first call.

## Canonical model table (`engine/pricing.py:33-211`)

This is the table the engine actually consults. Anything **not** here returns
`None` for context window and pricing.

| Model name (key) | Context | Provider class |
|---|---|---|
| `gpt-4-turbo`, `gpt-4o`, `gpt-4o-mini`, `gpt-4`, `gpt-3.5-turbo` | 128k–8k | OpenAI / Copilot |
| `gpt-4.1`, `gpt-4.1-mini` | 1,047,576 | OpenAI / Copilot |
| `gpt-5.1`, `gpt-5.2` | 400k | OpenAI / Copilot |
| `o1`, `o1-mini`, `o1-preview`, `o3-mini` | 128k–200k | OpenAI |
| `claude-opus-4-5`, `claude-sonnet-4-5`, `claude-haiku-4-5` | 200k | Anthropic |
| `opus-4.5`, `sonnet-4.5`, `haiku-4.5` (aliases) | 200k | Anthropic |
| `claude-opus-4.6`, `claude-opus-4.6-1m`, `claude-sonnet-4.6` | **1,000,000** | Anthropic |
| `claude-opus-4`, `claude-sonnet-4`, `claude-haiku-4` | 200k | Anthropic |
| `claude-3-7-sonnet`, `claude-3.7-sonnet` | 200k | Anthropic |
| `claude-3-5-sonnet`, `claude-3.5-sonnet`, `claude-3-5-haiku`, `claude-3.5-haiku` | 200k | Anthropic |
| `claude-3-opus`, `claude-3-sonnet`, `claude-3-haiku` | 200k | Anthropic |
| `gemini-3.1-pro-preview` | 1,000,000 | (no provider implemented) |

## Fuzzy match (the "but it works" trap)

`get_pricing(model)` (`engine/pricing.py:240-262`) doesn't require an exact
match. It tries, in order:

1. Exact lookup.
2. Longest-prefix `startswith` match.
3. Strip suffix `-(\d{8}|latest|preview)$`, exact lookup.
4. Strip + longest-prefix.
5. Otherwise return `None`.

So `claude-sonnet-4-20250514` resolves (suffix-strip → `claude-sonnet-4`),
`claude-3-5-sonnet-latest` resolves (suffix-strip), even
`gpt-4o-2024-08-06` resolves (longest-prefix on `gpt-4o`).

**This is why the Polyphony "Copilot namespace" rename is fragile.** Names
like `claude-opus-4.7-1m-internal`, `claude-opus-4.7`, `claude-opus-4.7-high`,
`claude-sonnet-4.6` (note: `4.6` *is* in the table; `4.7` is not),
`gpt-5.5`, `gpt-5.4-mini`, `gpt-5.3-codex` are **not in the canonical table**.
They will run on the providers if those APIs accept the names — but
`pricing.context_window` returns `None`, so the engine cannot trim or
estimate cost. Fuzzy match may rescue some of them (e.g. `claude-opus-4.7`
collides with `claude-opus-4` via longest-prefix), but the resolved entry's
context window will be wrong (200k for the table's Opus 4 vs 1M+ for the
real Opus 4.7).

## Choosing per-agent

Match the model class to the role:

| Role | Recommended (Copilot tier) | Why |
|---|---|---|
| Cross-cutting reviewers, large-context coders | `claude-opus-4.7-1m-internal` | 1M context for whole-codebase reasoning (note: not in pricing table — costs not estimated) |
| Root planners, architecture decisions | `claude-opus-4.7-high` | High reasoning beats raw context size |
| Routine reviewers, intermediate steps | `claude-sonnet-4.6` | Cost/quality sweet spot |
| Trivial deterministic transforms | `type: script` agent | LLMs are wrong tool for deterministic logic |

## Copilot MCP env-var bug

Per-server `env:` in MCP server configs is **dropped** by the Copilot SDK
(`providers/copilot.py:184-186`; tracked at copilot-sdk#163). MCP tools that
need environment variables silently fail. Workarounds:

- Bake the env into a wrapper script that the MCP server invokes.
- Use the `claude` provider for that workflow's MCP tools.

## Future-proofing

Two designs reduce coupling:

1. **Provider alias layer in conductor.** Map workflow-declared names to
   provider-native names at the provider boundary. Pro: workflows stay
   portable. Con: hides the actual model from the author.

2. **Externalize models to per-environment config.** Reference models by
   role (`model: roles.opus_long_context`) and resolve roles in conductor's
   config. Pro: workflows become provider-agnostic. Con: adds a layer of
   indirection.

## Don'ts

- ❌ Use `runtime.provider: openai-agents` — accepted at validate, dies at run.
- ❌ Assume an "unknown" model fails at workflow load — it doesn't.
- ❌ Trust `pricing.context_window` for any name not in `DEFAULT_PRICING`.
- ❌ Copy/paste Anthropic API names into a Copilot-bound workflow.
- ❌ Hardcode the same model everywhere — costs you $1+/agent on heavy
  reviewers when Sonnet would do the job.

## Dos

- ✅ Pin per-agent models to the smallest model that gets the job done.
- ✅ Use per-agent `provider:` override to mix Claude and Copilot in one workflow.
- ✅ When introducing a new provider, audit every `model:` line against
  that provider's namespace before running.
- ✅ Cross-check non-table model names against `engine/pricing.py:DEFAULT_PRICING`
  at PR review.

## Validation gap (idea for upstream)

Conductor could fail (or warn) at `conductor validate` time when an agent's
`model:` is not present in `DEFAULT_PRICING` (after suffix-strip). Today the
warning fires only at first agent execution — well after partial workflow
progress.

## Discovery

Polyphony AB#2925. Architect agent failed at first invocation under
`--provider copilot`; required rewriting 17 model directives across 7
workflow YAMLs and 3 lint scripts. Subsequent research surfaced that
`engine/pricing.py:DEFAULT_PRICING` (not `_CLAUDE_CONTEXT_WINDOWS`) is the
canonical table and that fuzzy match disguises namespace mismatches.
