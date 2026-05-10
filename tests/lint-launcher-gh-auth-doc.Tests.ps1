BeforeAll {
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $script:DocPath  = Join-Path $script:RepoRoot 'docs' 'troubleshooting' 'launcher-gh-auth.md'
    $script:ScriptPath = Join-Path $script:RepoRoot 'scripts' 'Resolve-GhIdentity.ps1'
}

Describe 'docs/troubleshooting/launcher-gh-auth.md' {

    It 'File exists' {
        $script:DocPath | Should -Exist
    }

    Context 'Content requirements' {

        BeforeAll {
            $script:Content = Get-Content $script:DocPath -Raw
        }

        It 'Explains the probe runs with -Platform github AND not -DryRun' {
            $script:Content | Should -Match '-Platform github'
            $script:Content | Should -Match '-DryRun'
        }

        It 'Decodes the diagnostic line fields: user, source, token_len' {
            $script:Content | Should -Match "gh identity pinned: user=.*source=.*token_len="
            $script:Content | Should -Match '\bgh-keyring\b'
            $script:Content | Should -Match '\benv\b'
        }

        It 'Names failure mode: competing-worker auth slippage' {
            $script:Content | Should -Match 'gh auth switch'
            $script:Content | Should -Match 'slippage'
        }

        It 'Names failure mode: stale or wrong-scope token' {
            $script:Content | Should -Match '[Ss]tale.*token|wrong-scope'
        }

        It 'Has remediation entry: gh not on PATH' {
            $script:Content | Should -Match 'gh CLI not found on PATH'
            $script:Content | Should -Match 'https://cli\.github\.com/'
        }

        It 'Has remediation entry: no token cached' {
            $script:Content | Should -Match 'no token cached for github\.com'
            $script:Content | Should -Match 'gh auth login --hostname github\.com'
            $script:Content | Should -Match 'gh auth switch --user PolyphonyRequiem'
        }

        It 'Has remediation entry: token validation failed (401/403)' {
            $script:Content | Should -Match 'token validation failed'
            $script:Content | Should -Match 'gh auth refresh --hostname github\.com'
            $script:Content | Should -Match 'gh auth status'
        }

        It 'Has remediation entry: gh auth token timed out' {
            $script:Content | Should -Match 'timed out after'
        }

        It 'Error strings match verbatim strings from Resolve-GhIdentity.ps1' {
            $script:ScriptContent = Get-Content $script:ScriptPath -Raw

            # Extract the key throw-message strings from the script and
            # verify each appears in the doc.
            $verbatimStrings = @(
                'gh CLI not found on PATH'
                'gh has no token cached for github.com'
                'gh token validation failed'
                'gh auth login --hostname github.com'
                'gh auth refresh --hostname github.com -s repo'
            )

            foreach ($str in $verbatimStrings) {
                $escaped = [regex]::Escape($str)
                # Confirm the string exists in both the source script and the doc
                $script:ScriptContent | Should -Match $escaped
                $script:Content       | Should -Match $escaped
            }
        }
    }
}
