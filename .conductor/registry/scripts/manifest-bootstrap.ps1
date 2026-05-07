#requires -Version 7.0
<#
.SYNOPSIS
Initialize the per-run manifest (`.polyphony/run.yaml`) for an apex run,
or validate an existing manifest matches the requested apex.

.DESCRIPTION
Routing-style helper used by the `init_manifest` agent in
`apex-driver.yaml`. Responsible for one of two outcomes:

1. **Manifest absent** → synthesizes `--platform-project` from the
   workflow's `organization` and `project` inputs (the work-item
   tracker is always ADO via twig today, regardless of which PR
   platform the workflow targets), validates inputs are non-empty,
   and calls `polyphony manifest init`.

2. **Manifest present** → calls `polyphony manifest read` and verifies
   `root_id` matches the requested `-ApexId`. A mismatch is a fatal
   resume error: someone is trying to start a fresh apex run inside a
   working directory still locked to a previous root.

Always exits 0 and writes a single JSON envelope to stdout. Routing
in the workflow keys off `output.success`. Failure variants surface
via `error_code`:

  invalid_inputs              — organization or project missing.
  manifest_read_failed        — `polyphony manifest read` exited non-zero.
  manifest_parse_failed       — `polyphony manifest read` stdout wasn't JSON.
  manifest_root_mismatch      — manifest root_id != ApexId.
  manifest_init_failed        — `polyphony manifest init` exited non-zero.
  manifest_init_parse_failed  — `polyphony manifest init` stdout wasn't JSON.
  polyphony_unavailable       — polyphony not on PATH.

.NOTES
Topology-hash drift on resume (manifest topology vs current ADO tree) is
intentionally NOT validated here. That is a deferred follow-up — see
`docs/decisions/branch-model.md` for the resume contract. Tracked in
the apex-driver pipeline-audit-fix PR body.

.PARAMETER ApexId
ADO work-item id of the apex (run-root) being executed.

.PARAMETER Organization
ADO organization name. Required.

.PARAMETER Project
ADO project name. Required.

.PARAMETER ManifestPath
Path to the manifest file. Defaults to `.polyphony/run.yaml`.

.PARAMETER PolyphonyExe
Override for the polyphony executable path. Defaults to `polyphony`.
#>
param(
    [Parameter(Mandatory)]
    [int]$ApexId,

    [string]$Organization = '',
    [string]$Project = '',
    [string]$ManifestPath = '.polyphony/run.yaml',
    [string]$PolyphonyExe = 'polyphony'
)

$ErrorActionPreference = 'Stop'

function Emit-Envelope {
    param([hashtable]$Fields)
    [pscustomobject]$Fields | ConvertTo-Json -Compress -Depth 8
}

function Invoke-Polyphony {
    param([string[]]$Arguments)
    $stderrFile = [System.IO.Path]::GetTempFileName()
    try {
        $stdout = & $PolyphonyExe @Arguments 2>$stderrFile
        $exit = $LASTEXITCODE
        $stderr = Get-Content -Raw $stderrFile -ErrorAction SilentlyContinue
        return [pscustomobject]@{
            Stdout = ($stdout -join "`n")
            Stderr = $stderr
            Exit   = $exit
        }
    }
    finally {
        Remove-Item $stderrFile -ErrorAction SilentlyContinue
    }
}

# ── Polyphony availability ──────────────────────────────────────────────
if (-not (Get-Command $PolyphonyExe -ErrorAction SilentlyContinue)) {
    Emit-Envelope @{
        success    = $false
        error_code = 'polyphony_unavailable'
        error      = "polyphony executable '$PolyphonyExe' not found on PATH"
    }
    exit 0
}

# ── Existing manifest path ──────────────────────────────────────────────
if (Test-Path $ManifestPath) {
    $read = Invoke-Polyphony @('manifest', 'read', '--path', $ManifestPath)
    if ($read.Exit -ne 0) {
        Emit-Envelope @{
            success    = $false
            error_code = 'manifest_read_failed'
            error      = "polyphony manifest read exited $($read.Exit). stderr: $($read.Stderr) stdout: $($read.Stdout)"
        }
        exit 0
    }

    try {
        $manifest = $read.Stdout | ConvertFrom-Json
    }
    catch {
        Emit-Envelope @{
            success    = $false
            error_code = 'manifest_parse_failed'
            error      = "could not parse manifest read JSON: $($_.Exception.Message)"
        }
        exit 0
    }

    if ($manifest.manifest.root_id -ne $ApexId) {
        Emit-Envelope @{
            success            = $false
            error_code         = 'manifest_root_mismatch'
            error              = "manifest root_id=$($manifest.manifest.root_id) does not match requested apex_id=$ApexId"
            manifest_root_id   = $manifest.manifest.root_id
            apex_id            = $ApexId
            manifest_path      = $ManifestPath
        }
        exit 0
    }

    # NOTE: topology-hash validation against the current ADO tree is
    # deferred — see docstring. For now, matching root_id is sufficient
    # to consider the manifest reusable.
    Emit-Envelope @{
        success          = $true
        action           = 'reused'
        path             = $ManifestPath
        root_id          = $manifest.manifest.root_id
        platform_project = $manifest.manifest.platform_project
    }
    exit 0
}

# ── Manifest absent — synthesize platform-project, then init ────────────
# Work items always come from ADO via twig today; the `platform` workflow
# input refers to the PR target platform, not the work-item tracker, so
# it is not consulted here. If polyphony ever supports non-ADO trackers
# this branch needs a switch.
if (-not $Organization -or -not $Project) {
    Emit-Envelope @{
        success    = $false
        error_code = 'invalid_inputs'
        error      = "manifest init requires non-empty organization and project (got organization='$Organization', project='$Project')"
    }
    exit 0
}
$platformProject = "dev.azure.com/$Organization/$Project"

$init = Invoke-Polyphony @('manifest', 'init', '--root-id', "$ApexId", '--platform-project', $platformProject, '--path', $ManifestPath)
if ($init.Exit -ne 0) {
    Emit-Envelope @{
        success    = $false
        error_code = 'manifest_init_failed'
        error      = "polyphony manifest init exited $($init.Exit). stderr: $($init.Stderr) stdout: $($init.Stdout)"
    }
    exit 0
}

try {
    $created = $init.Stdout | ConvertFrom-Json
}
catch {
    Emit-Envelope @{
        success    = $false
        error_code = 'manifest_init_parse_failed'
        error      = "could not parse manifest init JSON: $($_.Exception.Message). stdout: $($init.Stdout)"
    }
    exit 0
}

Emit-Envelope @{
    success          = $true
    action           = 'created'
    path             = $created.path
    root_id          = $created.root_id
    platform_project = $created.platform_project
}
exit 0
