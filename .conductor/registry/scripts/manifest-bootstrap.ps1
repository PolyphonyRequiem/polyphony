#requires -Version 7.0
<#
.SYNOPSIS
Initialize the per-run manifest under
`<git-common-dir>/polyphony/<root_id>/run.yaml` for an apex run, or
validate an existing manifest matches the requested apex.

.DESCRIPTION
Routing-style helper used by the `init_manifest` agent in
`apex-driver.yaml`. Responsible for one of two outcomes:

1. **Manifest absent** → synthesizes `--platform-project` from the
   workflow's `organization` and `project` inputs (the work-item
   tracker is always ADO via twig today, regardless of which PR
   platform the workflow targets), validates inputs are non-empty,
   and calls `polyphony manifest init --root-id N`.

2. **Manifest present** → calls `polyphony manifest read --root-id N`,
   which performs the AB#3067 root-mismatch guard internally. The
   verb refuses to proceed when `manifest.root_id != --root-id` —
   the carry-over case where someone resumes against the wrong root.

Probe is performed entirely through the CLI (`manifest read`) rather
than `Test-Path` on a known location: the manifest now lives at
`<git-common-dir>/polyphony/<root_id>/run.yaml`, which the CLI
derives via `git rev-parse --path-format=absolute --git-common-dir`.
Pre-resolving that path here would duplicate the resolver and risk
drift.

Always exits 0 and writes a single JSON envelope to stdout. Routing
in the workflow keys off `output.success`. Failure variants surface
via `error_code`:

  invalid_inputs                       - organization or project missing
                                         (init path) or partially supplied
                                         (reuse path).
  manifest_read_failed                 - `polyphony manifest read` exited non-zero
                                         for a reason OTHER than `manifest_not_found`
                                         or `manifest_root_mismatch`.
  manifest_parse_failed                - `polyphony manifest read` stdout wasn't JSON.
  manifest_root_mismatch               - manifest root_id != ApexId (AB#3067 guard).
  manifest_platform_project_mismatch   - stored manifest platform_project does not
                                         match the invocation's
                                         `dev.azure.com/{org}/{project}` (GH #166).
  manifest_init_failed                 - `polyphony manifest init` exited non-zero.
  manifest_init_parse_failed           - `polyphony manifest init` stdout wasn't JSON.
  polyphony_unavailable                - polyphony not on PATH.

.NOTES
Topology-hash drift on resume (manifest topology vs current ADO tree) is
intentionally NOT validated here. That is a deferred follow-up - see
`docs/decisions/branch-model.md` for the resume contract. Tracked in
the apex-driver pipeline-audit-fix PR body.

.PARAMETER ApexId
ADO work-item id of the apex (run-root) being executed.

.PARAMETER Organization
ADO organization name. Required when initialising a fresh manifest.
On the reuse path, optional — but if supplied, Project must also be
supplied, and the resulting `dev.azure.com/{org}/{project}` is
validated against the stored manifest's `platform_project` (GH #166).

.PARAMETER Project
ADO project name. Required when initialising a fresh manifest. On the
reuse path, same partial-supply / validation rule as Organization.

.PARAMETER PolyphonyExe
Override for the polyphony executable path. Defaults to `polyphony`.
#>
param(
    [Parameter(Mandatory)]
    [int]$ApexId,

    [string]$Organization = '',
    [string]$Project = '',
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

# -- Polyphony availability ---------------------------------------------
if (-not (Get-Command $PolyphonyExe -ErrorAction SilentlyContinue)) {
    Emit-Envelope @{
        success    = $false
        error_code = 'polyphony_unavailable'
        error      = "polyphony executable '$PolyphonyExe' not found on PATH"
    }
    exit 0
}

# -- Probe via CLI (the manifest now lives under the git common dir) ----
$read = Invoke-Polyphony @('manifest', 'read', '--root-id', "$ApexId")

if ($read.Exit -eq 0) {
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

    # NOTE: topology-hash validation against the current ADO tree is
    # deferred - see docstring. For now, a successful root-id-checked
    # read is sufficient to consider the manifest reusable.

    # GH #166: validate platform_project drift. When the invocation
    # supplies Organization+Project, the synthesized
    # `dev.azure.com/{org}/{project}` must match the stored manifest's
    # platform_project. If exactly one of Organization/Project is supplied,
    # reject as invalid_inputs (partial identity is never intentional).
    # If both are absent, skip silently (the resume case where the
    # operator chose not to re-supply them).
    $hasOrganization = -not [string]::IsNullOrWhiteSpace($Organization)
    $hasProject      = -not [string]::IsNullOrWhiteSpace($Project)

    if ($hasOrganization -xor $hasProject) {
        Emit-Envelope @{
            success    = $false
            error_code = 'invalid_inputs'
            error      = "manifest reuse validation requires both organization and project when either is supplied (got organization='$Organization', project='$Project')"
            apex_id    = $ApexId
        }
        exit 0
    }

    $platformProjectValidation = 'skipped_absent'
    if ($hasOrganization -and $hasProject) {
        $expectedPlatformProject = "dev.azure.com/$Organization/$Project"
        $storedPlatformProject   = $manifest.manifest.platform_project
        # OrdinalIgnoreCase is explicit — ADO org/project names are
        # case-insensitive identifiers and PowerShell's default `-ne`
        # is also case-insensitive, but stating it defensively here
        # prevents future "cleanup" from accidentally changing semantics.
        if (-not [System.StringComparer]::OrdinalIgnoreCase.Equals($expectedPlatformProject, $storedPlatformProject)) {
            Emit-Envelope @{
                success                     = $false
                error_code                  = 'manifest_platform_project_mismatch'
                error                       = "manifest platform_project '$storedPlatformProject' does not match invocation '$expectedPlatformProject'; delete the manifest or correct the invocation"
                apex_id                     = $ApexId
                manifest_platform_project   = $storedPlatformProject
                invocation_platform_project = $expectedPlatformProject
            }
            exit 0
        }
        $platformProjectValidation = 'checked'
    }

    Emit-Envelope @{
        success                     = $true
        action                      = 'reused'
        path                        = $manifest.manifest.path
        root_id                     = $manifest.manifest.root_id
        platform_project            = $manifest.manifest.platform_project
        platform_project_validation = $platformProjectValidation
    }
    exit 0
}

# Read failed - distinguish "first run, file absent" from other errors.
$readErrorCode = $null
$readPayload = $null
try {
    $readPayload = $read.Stdout | ConvertFrom-Json
    $readErrorCode = $readPayload.error_code
}
catch {
    # Non-JSON stdout - fall through with $readErrorCode = $null.
}

if ($readErrorCode -eq 'manifest_root_mismatch') {
    # AB#3067 carry-over guard - surface verbatim to the operator.
    Emit-Envelope @{
        success            = $false
        error_code         = 'manifest_root_mismatch'
        error              = "$($readPayload.error)"
        manifest_root_id   = $readPayload.manifest_root_id
        apex_id            = $ApexId
    }
    exit 0
}

if ($readErrorCode -ne 'manifest_not_found') {
    # Anything other than missing-file is a hard read failure.
    Emit-Envelope @{
        success    = $false
        error_code = 'manifest_read_failed'
        error      = "polyphony manifest read exited $($read.Exit) (error_code=$readErrorCode). stderr: $($read.Stderr) stdout: $($read.Stdout)"
    }
    exit 0
}

# -- Manifest absent - synthesize platform-project, then init -----------
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

$init = Invoke-Polyphony @('manifest', 'init', '--root-id', "$ApexId", '--platform-project', $platformProject)
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
