#requires -Version 7
<#
.SYNOPSIS
  Idempotent seeder for plan-level child work items.

.DESCRIPTION
  Consumes a structured `tasks` array (from architect.output.tasks) and
  reconciles it against the existing children of $WorkItemId in ADO. Creates
  missing children, reuses matched ones, and stamps the parent with the
  `polyphony:planned` tag on success.

  Match precedence per task:
    1. Existing child whose description contains
       `<!-- polyphony:plan-task-id=task-N -->` matching the architect's id
       → reused (no create).
    2. Existing child with matching (title, type) under the parent → reused
       with a warning logged (marker damaged or missing).
    3. No match → created via `twig new`, with the marker embedded as the
       last line of the description.

  When the run completes with zero errors, merges `polyphony:planned` into
  the parent's `System.Tags`. The polyphony route command reads this tag to
  recognize the planned state without touching process-config.

  Outputs a single line of JSON to stdout. Conductor merges the parsed JSON
  into the script-agent's `output` dict so downstream templates and routes
  can reference fields like `seeder.output.seeded_count`,
  `seeder.output.errors`, etc.

.PARAMETER WorkItemId
  ADO work item ID of the parent under which child tasks are seeded.

.PARAMETER TasksJson
  JSON array of task objects from `architect.output.tasks`. Each task must
  have at minimum: task_id, title, type, description. Optional fields:
  acceptance_criteria, pg, depends_on. Empty array means atomic — no
  children to seed; tag is still set.

.PARAMETER PlannedTag
  Tag value to apply to the parent on success. Defaults to
  `polyphony:planned`. Override only for testing.

.PARAMETER TwigCommand
  Name of the twig executable. Defaults to `twig`. Tests override to a
  mock function.
#>
param(
    [Parameter(Mandatory)] [int]    $WorkItemId,
    [Parameter(Mandatory)] [string] $TasksJson,
    [string] $PlannedTag = 'polyphony:planned',
    [string] $TwigCommand = 'twig'
)

$ErrorActionPreference = 'Stop'

# ─── Helpers ──────────────────────────────────────────────────────────────

function Get-MarkerId([string] $description) {
    if ([string]::IsNullOrWhiteSpace($description)) { return $null }
    if ($description -match '<!--\s*polyphony:plan-task-id=(task-\d+)\s*-->') {
        return $Matches[1]
    }
    return $null
}

function Build-Description($task) {
    $body = if ($task.description) { $task.description.TrimEnd() } else { '' }

    $acBlock = ''
    if ($task.acceptance_criteria -and $task.acceptance_criteria.Count -gt 0) {
        $bullets = ($task.acceptance_criteria | ForEach-Object { "- $_" }) -join "`n"
        $acBlock = "`n`n## Acceptance Criteria`n$bullets"
    }

    $marker = "<!-- polyphony:plan-task-id=$($task.task_id) -->"

    return "$body$acBlock`n`n$marker"
}

function Invoke-Twig {
    # Wrapper so tests can mock invocation without redefining the global twig
    # function for every call site. Returns stdout as a single string; throws
    # on nonzero exit.
    param([Parameter(Mandatory)] [string[]] $TwigArgs)

    $output = & $TwigCommand @TwigArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "twig $($TwigArgs -join ' ') failed (exit $LASTEXITCODE): $output"
    }
    return ($output | Out-String)
}

function Get-ExistingChildren([int] $parentId) {
    $raw = Invoke-Twig @('show', "$parentId", '--tree', '-o', 'json')
    $tree = $raw | ConvertFrom-Json
    if ($null -eq $tree.children) { return @() }
    return @($tree.children)
}

function Get-ParentTags([int] $parentId) {
    $raw = Invoke-Twig @('show', "$parentId", '-o', 'json')
    $item = $raw | ConvertFrom-Json
    $tagsField = $item.tags
    if ([string]::IsNullOrWhiteSpace($tagsField)) { return @() }
    # ADO tags are returned as a string with '; ' separators in twig's output.
    return @($tagsField -split ';\s*' | Where-Object { $_ })
}

function Set-ParentTags([int] $parentId, [string[]] $tags) {
    $merged = ($tags | Where-Object { $_ } | Select-Object -Unique) -join '; '
    $patch = @{ 'System.Tags' = $merged } | ConvertTo-Json -Compress
    Invoke-Twig @('patch', '--id', "$parentId", '--json', $patch) | Out-Null
}

function Create-Child([int] $parentId, $task) {
    $desc = Build-Description $task
    $createArgs = @(
        'new',
        '--type', $task.type,
        '--title', $task.title,
        '--description', $desc,
        '--parent', "$parentId",
        '-o', 'json'
    )
    $raw = Invoke-Twig $createArgs
    return ($raw.Trim() | ConvertFrom-Json)
}

# ─── Main ─────────────────────────────────────────────────────────────────

$tasks = @($TasksJson | ConvertFrom-Json)

# Defensive: if architect emits {} or null, treat as empty list.
if ($null -eq $tasks) { $tasks = @() }

$existing = Get-ExistingChildren -parentId $WorkItemId

# Build lookup tables once.
$markerIndex = @{}
$titleTypeIndex = @{}
foreach ($child in $existing) {
    $desc = $null
    if ($child.fields -and $child.fields.PSObject.Properties.Name -contains 'System.Description') {
        $desc = $child.fields.'System.Description'
    }
    $mid = Get-MarkerId $desc
    if ($mid) { $markerIndex[$mid] = $child }
    $key = "$($child.type)|$($child.title)"
    $titleTypeIndex[$key] = $child
}

$seeded = New-Object System.Collections.Generic.List[object]
$reused = New-Object System.Collections.Generic.List[object]
$errors = New-Object System.Collections.Generic.List[object]
$warnings = New-Object System.Collections.Generic.List[string]

foreach ($task in $tasks) {
    if (-not $task.task_id) {
        $errors.Add(@{ task_id = $null; title = $task.title; error = 'task missing required task_id' })
        continue
    }
    if (-not $task.title -or -not $task.type) {
        $errors.Add(@{ task_id = $task.task_id; title = $task.title; error = 'task missing required title or type' })
        continue
    }

    try {
        # 1. Marker match (preferred).
        if ($markerIndex.ContainsKey($task.task_id)) {
            $hit = $markerIndex[$task.task_id]
            $reused.Add(@{
                task_id      = $task.task_id
                work_item_id = $hit.id
                matched_by   = 'marker'
            })
            continue
        }

        # 2. Title+type fallback.
        $key = "$($task.type)|$($task.title)"
        if ($titleTypeIndex.ContainsKey($key)) {
            $hit = $titleTypeIndex[$key]
            $warnings.Add("task $($task.task_id) matched #$($hit.id) by title fallback (marker damaged or missing)")
            $reused.Add(@{
                task_id      = $task.task_id
                work_item_id = $hit.id
                matched_by   = 'title'
            })
            continue
        }

        # 3. Create.
        $created = Create-Child -parentId $WorkItemId -task $task
        $seeded.Add(@{
            task_id      = $task.task_id
            work_item_id = $created.id
            matched_by   = 'created'
        })
    }
    catch {
        $errors.Add(@{
            task_id = $task.task_id
            title   = $task.title
            error   = $_.Exception.Message
        })
    }
}

# Tag the parent only if reconciliation succeeded for every task.
$tagSet = $false
$tagAlreadyPresent = $false
if ($errors.Count -eq 0) {
    try {
        $currentTags = Get-ParentTags -parentId $WorkItemId
        if ($currentTags -contains $PlannedTag) {
            $tagSet = $true
            $tagAlreadyPresent = $true
        }
        else {
            Set-ParentTags -parentId $WorkItemId -tags ($currentTags + $PlannedTag)
            $tagSet = $true
        }
    }
    catch {
        $errors.Add(@{
            task_id = $null
            title   = $null
            error   = "failed to set planned tag on parent #$($WorkItemId): $($_.Exception.Message)"
        })
    }
}

$result = [ordered]@{
    work_item_id        = $WorkItemId
    task_count          = $tasks.Count
    seeded_count        = $seeded.Count
    reused_count        = $reused.Count
    error_count         = $errors.Count
    seeded_items        = $seeded.ToArray()
    reused_items        = $reused.ToArray()
    errors              = $errors.ToArray()
    warnings            = $warnings.ToArray()
    planned_tag_set     = $tagSet
    planned_tag_already = $tagAlreadyPresent
}

# `-Compress` keeps stdout to a single JSON line so the conductor parser
# doesn't have to deal with multi-line output.
$result | ConvertTo-Json -Compress -Depth 10
