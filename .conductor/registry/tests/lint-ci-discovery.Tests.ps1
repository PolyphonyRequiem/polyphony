#requires -Version 7

# lint-ci-discovery.Tests.ps1
#
# CI discovery sentinel: enforces that every `*.Tests.ps1` file under
# `.conductor/registry/tests/` is either invoked by a named CI step in
# `.github/workflows/ci.yml`, or explicitly deferred via the
# `$DeferredTests` map below (each deferral must reference an ADO work
# item tracking the orphan).
#
# Background: the main Pester run in `ci.yml` scans only `tests/`. Any
# `*.Tests.ps1` file under `.conductor/registry/tests/` must be wired
# into CI via a named step or it never runs and silently rots. PR #475
# fixed four lint test files that had been broken without anyone
# noticing because none of them were invoked. This sentinel exists so
# that a new orphan test file added to that directory fails CI on the
# PR that adds it, not weeks later when someone notices the drift.
#
# Maintenance:
# - When a deferred test is wired into ci.yml as a named step, remove
#   its entry from $DeferredTests. The corresponding ADO item should
#   then close out incrementally; AB#3269 closes when $DeferredTests
#   reaches empty.
# - When adding a new `*.Tests.ps1` file: either invoke it from ci.yml
#   immediately (preferred) or add a deferral entry referencing a new
#   ADO sub-item under AB#3269.

BeforeAll {
    $script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')
    $script:TestsDir = Join-Path $script:RepoRoot '.conductor' 'registry' 'tests'
    $script:CiYamlPath = Join-Path $script:RepoRoot '.github' 'workflows' 'ci.yml'

    # Orphan tests deferred via AB#3269. Each entry is filename → ADO
    # ref. Keep the list in lex order. Drop an entry when the test gets
    # wired into ci.yml.
    $script:DeferredTests = [ordered]@{
        'integrate-target-drift.Tests.ps1'                = 'AB#3269'
        'lint-actionable.Tests.ps1'                       = 'AB#3269'
        'lint-ado-pr.Tests.ps1'                           = 'AB#3269'
        'lint-apex-driver.Tests.ps1'                      = 'AB#3269'
        'lint-feature-pr.Tests.ps1'                       = 'AB#3269'
        'lint-github-pr.Tests.ps1'                        = 'AB#3269'
        'lint-implement-merge-group.Tests.ps1'            = 'AB#3269'
        'lint-plan-level.Tests.ps1'                       = 'AB#3269'
        'lint-root-fallback-gate.Tests.ps1'               = 'AB#3269'
        'lint-scope-reviewer-empty-merge-group.Tests.ps1' = 'AB#3269'
        'manifest-bootstrap.Tests.ps1'                    = 'AB#3269'
        'resolve-pr-policy.Tests.ps1'                     = 'AB#3269'
        'resolve-research-policy.Tests.ps1'               = 'AB#3269'
        'resolve-unattended-cap-mode.Tests.ps1'           = 'AB#3269'
        'verb-signature-contracts.Tests.ps1'              = 'AB#3269'
        'wave-dispatch-guard.Tests.ps1'                   = 'AB#3269'
    }
}

# Pester v5 `-ForEach` is evaluated at **discovery time** (before any
# `BeforeAll` runs), so the test-case arrays must be built here at script
# scope, not inside a `BeforeAll` block. We also can't rely on
# $script:CiYamlPath being set yet — recompute locally.
$discoveryRepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')
$discoveryTestsDir = Join-Path $discoveryRepoRoot '.conductor' 'registry' 'tests'
$discoveryAllTestFiles = Get-ChildItem -LiteralPath $discoveryTestsDir -Filter '*.Tests.ps1' -Recurse |
    ForEach-Object { $_.Name } |
    Sort-Object
$discoveryTestCases = $discoveryAllTestFiles | ForEach-Object { @{ FileName = $_ } }

$discoveryDeferredTests = [ordered]@{
    'integrate-target-drift.Tests.ps1'                = 'AB#3269'
    'lint-actionable.Tests.ps1'                       = 'AB#3269'
    'lint-ado-pr.Tests.ps1'                           = 'AB#3269'
    'lint-apex-driver.Tests.ps1'                      = 'AB#3269'
    'lint-feature-pr.Tests.ps1'                       = 'AB#3269'
    'lint-github-pr.Tests.ps1'                        = 'AB#3269'
    'lint-implement-merge-group.Tests.ps1'            = 'AB#3269'
    'lint-plan-level.Tests.ps1'                       = 'AB#3269'
    'lint-root-fallback-gate.Tests.ps1'               = 'AB#3269'
    'lint-scope-reviewer-empty-merge-group.Tests.ps1' = 'AB#3269'
    'manifest-bootstrap.Tests.ps1'                    = 'AB#3269'
    'resolve-pr-policy.Tests.ps1'                     = 'AB#3269'
    'resolve-research-policy.Tests.ps1'               = 'AB#3269'
    'resolve-unattended-cap-mode.Tests.ps1'           = 'AB#3269'
    'verb-signature-contracts.Tests.ps1'              = 'AB#3269'
    'wave-dispatch-guard.Tests.ps1'                   = 'AB#3269'
}
$discoveryDeferralRefCases = @($discoveryDeferredTests.Keys) | ForEach-Object {
    @{ FileName = $_; Reference = $discoveryDeferredTests[$_] }
}
$discoveryDeferralFileCases = @($discoveryDeferredTests.Keys) | ForEach-Object {
    @{ FileName = $_; Path = (Join-Path $discoveryTestsDir $_) }
}

Describe 'CI discovery sentinel (.conductor/registry/tests/*.Tests.ps1)' {

    It 'finds the ci.yml workflow file' {
        $script:CiYamlPath | Should -Exist
    }

    It 'finds the .conductor/registry/tests directory' {
        $script:TestsDir | Should -Exist
    }

    Context 'Per-file discovery contract' {

        BeforeAll {
            $script:CiYamlText = Get-Content -LiteralPath $script:CiYamlPath -Raw
            $script:AllTestFiles = Get-ChildItem -LiteralPath $script:TestsDir -Filter '*.Tests.ps1' -Recurse |
                ForEach-Object { $_.Name } |
                Sort-Object
        }

        It 'enumerates at least one test file' {
            $script:AllTestFiles.Count | Should -BeGreaterThan 0
        }

        # The sentinel itself must be invoked from ci.yml. It is not
        # eligible for deferral — if no one invokes the sentinel, no
        # one enforces the contract.
        It 'asserts the sentinel itself is invoked from ci.yml' {
            $script:CiYamlText | Should -Match 'lint-ci-discovery\.Tests\.ps1'
        }

        It 'asserts <FileName> is invoked from ci.yml or formally deferred' -ForEach $discoveryTestCases {
            $invoked = $script:CiYamlText -match ([regex]::Escape($FileName))
            $deferral = $script:DeferredTests[$FileName]

            if (-not $invoked -and [string]::IsNullOrEmpty($deferral)) {
                throw "Test file '$FileName' under .conductor/registry/tests/ is not " +
                      'invoked by any step in .github/workflows/ci.yml and is not ' +
                      'in the $DeferredTests list of lint-ci-discovery.Tests.ps1. ' +
                      'Either add a named CI step that invokes it (preferred), or ' +
                      'add a deferral entry pointing to an ADO sub-item under AB#3269.'
            }

            if ($invoked -and $deferral) {
                throw "Test file '$FileName' is BOTH invoked by ci.yml AND listed in " +
                      "`$DeferredTests with deferral '$deferral'. Remove its entry " +
                      'from $DeferredTests in lint-ci-discovery.Tests.ps1 — the ' +
                      'deferrals list should track only currently-orphaned files.'
            }

            # If we reach here, either invoked-only (good) or deferred-only
            # (acknowledged debt). Both pass.
        }
    }

    Context 'Deferrals contract' {

        It 'requires every deferred entry to carry an ADO reference (AB#NNNN)' -ForEach $discoveryDeferralRefCases {
            $Reference | Should -Match '^AB#\d+$' -Because (
                "deferred test '$FileName' must reference an ADO work item " +
                'so the orphan-test debt is tracked formally')
        }

        It 'requires every deferred file to actually exist under .conductor/registry/tests/' -ForEach $discoveryDeferralFileCases {
            $Path | Should -Exist -Because (
                "deferred test '$FileName' is in `$DeferredTests but the file " +
                'does not exist. Either restore the file or remove its entry ' +
                'from $DeferredTests.')
        }
    }
}
