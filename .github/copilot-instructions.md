# GitHub Copilot Instructions

See [`AGENTS.md`](../AGENTS.md) at the repo root for the canonical agent guidance for this repo. Copilot will load both this file (path-specific instructions) and any matching `AGENTS.md`; they are kept in sync deliberately.

Copilot-specific notes:

- **Skills auto-load**: `.github/skills/*/SKILL.md` files are activated per the skill's `description:` field. Don't manually `/load` a skill that the runtime will load for you.
- **Vocabulary lint**: PRs that introduce forbidden synonyms (see `docs/glossary.md`) will fail CI. The cleanup was AB#3259; there are no aliases.
- **Type-agnosticism (P5)**: Never put a work-item type name (Epic / Issue / Task / Bug / custom) in any workflow routing condition. Types are configured per consumer-repo under `.polyphony-config/work-item-types/`.
