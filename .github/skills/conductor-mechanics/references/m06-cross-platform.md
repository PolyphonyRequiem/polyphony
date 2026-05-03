# M6: Cross-Platform Subprocess Footguns

Conductor invokes `command:` agents through Python's `subprocess` without
`shell=True`. On Windows, this means **PATHEXT is not honored** — only
files with an `.exe` extension are resolved by name. `.cmd`, `.bat`,
`.ps1` wrappers on PATH look invisible to conductor.

## Symptom

```
FileNotFoundError: [WinError 2] The system cannot find the file specified
```

…or a slightly less obvious failure where the agent looks like it ran but
the workflow's `state_detector` etc. emits a `phase=error` because the
nested subprocess couldn't find the wrapper.

## Why

`subprocess.run(["foo", ...])` on Windows performs an exact-name lookup on
PATH. Without `shell=True` or explicit `.exe` resolution, the OS does not
attempt PATHEXT-style extensions. PowerShell's own command resolution
**does** honor PATHEXT — which is why `pwsh -c "foo ..."` works against
the same `foo.cmd` that `command: foo` cannot find.

## Reliable patterns

### Publish a real `.exe`

The cleanest fix. For .NET, use a self-contained single-file publish:

```powershell
dotnet publish src\Polyphony\Polyphony.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o C:\Users\$env:USERNAME\.local\bin
```

Drop `polyphony.exe` on PATH, replace any `polyphony.cmd` shim. `command: polyphony`
in the workflow now works directly.

### Shell out via `pwsh`

If publishing an `.exe` isn't an option, route through PowerShell so the
shell does the resolution:

```yaml
- name: state_detector
  type: script
  command: pwsh
  args:
    - "-NoProfile"
    - "-Command"
    - "polyphony route --work-item {{ workflow.input.work_item_id }}"
```

Slower (pwsh startup) and adds a quoting layer, but resolves PATHEXT.

### Use the absolute path

```yaml
command: C:\Users\dangreen\.local\bin\polyphony.cmd
```

Works but breaks portability across machines and users.

## Other cross-platform footguns

- **Path separators.** Templates that build paths with `/` may work on
  Linux but break on Windows scripts that pass them to native APIs. Use
  `os.path.join` in scripts and let pwsh accept either form.
- **Line endings in heredocs.** PowerShell here-strings with `\r\n` may
  munge JSON sent to other tools. Pipe through `Out-String -Stream` or
  use `--body-file` for `gh`.
- **Single-file binary first-run latency.** A single-file publish self-
  extracts on first invocation per session — first call may be 15-20s,
  subsequent calls 3-5s. Bake this into agent timeouts.

## Don'ts

- ❌ Drop a `.cmd` or `.bat` wrapper on PATH and assume `command: <name>`
  finds it.
- ❌ Rely on `where.exe foo` to confirm conductor will find `foo` —
  conductor's lookup is narrower than `where`'s.
- ❌ Test workflow changes only on Linux/macOS if Windows is in scope.

## Dos

- ✅ Publish real `.exe` artifacts to PATH for any tool a `command:` agent
  invokes.
- ✅ When stuck, inspect the actual command line conductor builds (look
  for `Popen` args in conductor logs at `--log-level debug`).
- ✅ Smoke-test new `command:` invocations on Windows before merging.

## Discovery
Polyphony self-hosting dogfood iteration 1: `polyphony.cmd` on PATH worked
when invoked from PowerShell scripts (e.g. `detect-state.ps1`) but failed
the moment a workflow tried `command: polyphony` directly. Resolved by
publishing a real `polyphony.exe`.
