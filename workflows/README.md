# Workflows have moved

The conductor workflow YAMLs (and their supporting `prompts/`) that used to live
in this directory now live in
[`polyphony-conductor-workflows`](https://github.com/PolyphonyRequiem/polyphony-conductor-workflows).

They are deployed to the conductor `polyphony` registry. Once the registry is
configured locally:

```powershell
conductor registry add polyphony C:/path/to/polyphony-conductor-workflows --type path
```

invoke the entry workflows by name:

```powershell
conductor run plan-level@polyphony           --input work_item_id=<ID> --web
conductor run implement-pg@polyphony         --input work_item_id=<ID> --web
```

## Why a separate repo?

- Polyphony's own `scripts/` (e.g. `load-agent-guidance.ps1`,
  `scope-closer.ps1`) are referenced by the workflows via relative paths
  resolved at the **consumer repo's** working directory. When polyphony
  dogfoods itself, those paths just work.
- Other consumer repos will need to either expose the same `scripts/` layout
  or wrap polyphony invocations with an env-var resolver. That's a deferred
  v0.2 concern — not solved by duplicating the scripts.

See `polyphony-conductor-workflows/README.md` for design principles (P5/P8).
