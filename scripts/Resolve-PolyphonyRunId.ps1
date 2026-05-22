<#
.SYNOPSIS
    Run-id discovery + minting for Invoke-PolyphonySdlc.ps1 (W1, AB#3275).

.DESCRIPTION
    The launcher must export a stable POLYPHONY_RUN_ID for every
    `conductor run` and every nested `polyphony` invocation. Lineage rule:

      1. If `.polyphony/run.yaml` exists and carries a `run_id:` line, use
         it (resume / replan / reset continuity).
      2. Otherwise mint a fresh ULID (26-char Crockford base32, 48-bit ms
         timestamp + 80 bits random) and persist it through the conductor
         input metadata. The C# manifest-init path (W2) will persist that
         minted id into `.polyphony/run.yaml` schema-v2 on the first save.

    Implementation lives in a sibling file so Pester can unit-test the
    pure helpers without dot-sourcing the entire launcher.

    The minted shape matches `Polyphony.Journal.RunIdMint` so a launcher-
    minted id round-trips through `polyphony manifest show / init` without
    re-shape on the C# side.
#>

# Crockford base32 alphabet — same one Polyphony.Journal.RunIdMint uses.
# No I / L / O / U.
$script:PolyphonyCrockfordAlphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'.ToCharArray()
$script:PolyphonyUlidLength = 26

function Read-PolyphonyManifestRunId {
    <#
    .SYNOPSIS
        Returns the run_id stamped in a `.polyphony/run.yaml`, or $null.

    .DESCRIPTION
        Hand-parses raw YAML (no PowerShell-Yaml dep — keeps the launcher
        portable). Looks for a top-level `run_id:` key. Trims surrounding
        quotes and whitespace; returns $null if the file is absent, the
        key is missing, or the value is empty / `null`.
    #>
    param([Parameter(Mandatory)][string]$ManifestPath)

    if ([string]::IsNullOrWhiteSpace($ManifestPath)) { return $null }
    if (-not (Test-Path -LiteralPath $ManifestPath)) { return $null }

    foreach ($line in Get-Content -LiteralPath $ManifestPath -ErrorAction SilentlyContinue) {
        # Only top-level keys (no leading whitespace) count — nested
        # `run_id:` under `rebases:` or similar must not match.
        if ($line -match '^run_id:\s*(.+)$') {
            $value = $matches[1].Trim()
            # Strip an inline `# …` YAML comment if present.
            if ($value -match '^(.*?)\s+#') { $value = $matches[1].Trim() }
            # Strip matched quotes.
            if ($value -match '^"(.*)"$' -or $value -match "^'(.*)'$") {
                $value = $matches[1]
            }
            if ([string]::IsNullOrWhiteSpace($value) -or $value -ieq 'null' -or $value -ieq '~') {
                return $null
            }
            return $value
        }
    }
    return $null
}

function Test-PolyphonyRunIdWellFormed {
    <#
    .SYNOPSIS
        True when the id matches the 26-char Crockford-base32 ULID shape.
        Mirrors `RunIdMint.IsWellFormed` on the C# side.
    #>
    param([string]$RunId)
    if ([string]::IsNullOrEmpty($RunId)) { return $false }
    if ($RunId.Length -ne $script:PolyphonyUlidLength) { return $false }
    $alphabet = [string]::new($script:PolyphonyCrockfordAlphabet)
    foreach ($ch in $RunId.ToCharArray()) {
        if ($alphabet.IndexOf([char]([string]$ch).ToUpperInvariant()) -lt 0) {
            return $false
        }
    }
    return $true
}

function New-PolyphonyRunId {
    <#
    .SYNOPSIS
        Mints a fresh ULID matching `Polyphony.Journal.RunIdMint`.

    .DESCRIPTION
        48-bit unix-ms timestamp followed by 80 bits of cryptographic
        randomness, base32-encoded into 26 characters using the Crockford
        alphabet (no I / L / O / U). Lexicographically sortable by mint
        time — useful for "newest run first" listings.

    .PARAMETER TimestampMs
        Override the timestamp half (deterministic tests). Defaults to
        `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`. Pass a non-
        negative value <= 2^48 - 1.
    #>
    [CmdletBinding()]
    param(
        [Parameter()]
        [long]$TimestampMs = -1
    )

    if ($TimestampMs -lt 0) {
        $ts = [System.DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    } else {
        $ts = $TimestampMs
    }
    if ($ts -gt 0xFFFFFFFFFFFF) {
        throw "ULID timestamp must fit in 48 bits (0..2^48 - 1); got $ts."
    }

    $randomness = [byte[]]::new(10)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($randomness)

    # Pack into 128 bits = 16 bytes: first 6 bytes timestamp (big-endian),
    # then 10 bytes randomness. Then base32-encode 130 bits worth — the
    # canonical ULID encoding pads to 26 base32 chars (5 bits each = 130
    # bits, top 2 bits are zero by construction since 48 + 80 = 128).
    $bytes = [byte[]]::new(16)
    $bytes[0] = [byte](($ts -shr 40) -band 0xFF)
    $bytes[1] = [byte](($ts -shr 32) -band 0xFF)
    $bytes[2] = [byte](($ts -shr 24) -band 0xFF)
    $bytes[3] = [byte](($ts -shr 16) -band 0xFF)
    $bytes[4] = [byte](($ts -shr 8)  -band 0xFF)
    $bytes[5] = [byte]( $ts          -band 0xFF)
    [System.Array]::Copy($randomness, 0, $bytes, 6, 10)

    # Treat the 16 bytes as a 128-bit big-endian integer and emit 26
    # base32 digits, most-significant first. We process the value in two
    # 64-bit halves to avoid BigInteger overhead.
    $high = ([uint64]$bytes[0] -shl 56) -bor
            ([uint64]$bytes[1] -shl 48) -bor
            ([uint64]$bytes[2] -shl 40) -bor
            ([uint64]$bytes[3] -shl 32) -bor
            ([uint64]$bytes[4] -shl 24) -bor
            ([uint64]$bytes[5] -shl 16) -bor
            ([uint64]$bytes[6] -shl 8)  -bor
            ([uint64]$bytes[7])
    $low  = ([uint64]$bytes[8]  -shl 56) -bor
            ([uint64]$bytes[9]  -shl 48) -bor
            ([uint64]$bytes[10] -shl 40) -bor
            ([uint64]$bytes[11] -shl 32) -bor
            ([uint64]$bytes[12] -shl 24) -bor
            ([uint64]$bytes[13] -shl 16) -bor
            ([uint64]$bytes[14] -shl 8)  -bor
            ([uint64]$bytes[15])

    $chars = [char[]]::new($script:PolyphonyUlidLength)
    for ($i = $script:PolyphonyUlidLength - 1; $i -ge 0; $i--) {
        $idx = [int]($low -band 0x1F)
        $chars[$i] = $script:PolyphonyCrockfordAlphabet[$idx]
        # Shift the 128-bit value right by 5, carrying from high → low.
        $carry = ($high -band 0x1F) -shl 59
        $low  = (($low -shr 5) -bor $carry)
        $high = ($high -shr 5)
    }
    return [string]::new($chars)
}

function Resolve-PolyphonyRunId {
    <#
    .SYNOPSIS
        Returns @{ RunId; Source } where Source is one of `manifest`,
        `minted`, or `external` (POLYPHONY_RUN_ID already exported by an
        outer wrapper — preserved for nested invocations).

    .PARAMETER ManifestPath
        Candidate path to `.polyphony/run.yaml`. May be $null when the
        worktree does not yet exist (fresh `new` run).

    .PARAMETER RespectExisting
        When $true (default), an already-set $env:POLYPHONY_RUN_ID is
        honored verbatim (returns Source=external). When $false, the
        helper always re-resolves from the manifest / mint path — useful
        for tests.
    #>
    [CmdletBinding()]
    param(
        [string]$ManifestPath,
        [bool]$RespectExisting = $true
    )

    if ($RespectExisting -and -not [string]::IsNullOrWhiteSpace($env:POLYPHONY_RUN_ID)) {
        return @{ RunId = $env:POLYPHONY_RUN_ID; Source = 'external' }
    }

    if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
        $existing = Read-PolyphonyManifestRunId -ManifestPath $ManifestPath
        if (-not [string]::IsNullOrWhiteSpace($existing)) {
            return @{ RunId = $existing; Source = 'manifest' }
        }
    }

    return @{ RunId = (New-PolyphonyRunId); Source = 'minted' }
}
