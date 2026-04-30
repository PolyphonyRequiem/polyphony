BeforeAll {
    . "$PSScriptRoot/../lib/ado-helpers.ps1"
}

Describe 'ado-helpers.ps1 — ADO workspace derivation (#2651)' {
    Context 'Get-AdoOrg' {
        It 'Returns organization from twig config' {
            Mock twig {
                $global:LASTEXITCODE = 0
                '{"info":"my-org"}'
            } -ParameterFilter { $args -contains 'organization' }
            $result = Get-AdoOrg
            $result | Should -Be 'my-org'
        }

        It 'Returns empty string when twig config fails' {
            Mock twig { $global:LASTEXITCODE = 1; return $null }
            $result = Get-AdoOrg
            $result | Should -Be ''
        }

        It 'Returns empty string when info is null' {
            Mock twig {
                $global:LASTEXITCODE = 0
                '{"info":null}'
            } -ParameterFilter { $args -contains 'organization' }
            $result = Get-AdoOrg
            $result | Should -Be ''
        }
    }

    Context 'Get-AdoProject' {
        It 'Returns project from twig config' {
            Mock twig {
                $global:LASTEXITCODE = 0
                '{"info":"MyProject"}'
            } -ParameterFilter { $args -contains 'project' }
            $result = Get-AdoProject
            $result | Should -Be 'MyProject'
        }

        It 'Returns empty string when twig config fails' {
            Mock twig { $global:LASTEXITCODE = 1; return $null }
            $result = Get-AdoProject
            $result | Should -Be ''
        }
    }

    Context 'Get-AdoWorkspace' {
        BeforeEach {
            Mock twig {
                param()
                $global:LASTEXITCODE = 0
                if ($args -contains 'organization') { return '{"info":"my-org"}' }
                if ($args -contains 'project') { return '{"info":"MyProject"}' }
                return $null
            }
        }

        It 'Returns org/project format when both available' {
            $result = Get-AdoWorkspace
            $result | Should -Be 'my-org/MyProject'
        }

        It 'Returns empty string when org is missing' {
            Mock twig {
                param()
                if ($args -contains 'organization') { $global:LASTEXITCODE = 1; return $null }
                $global:LASTEXITCODE = 0
                if ($args -contains 'project') { return '{"info":"MyProject"}' }
                return $null
            }
            $result = Get-AdoWorkspace
            $result | Should -Be ''
        }

        It 'Returns empty string when project is missing' {
            Mock twig {
                param()
                if ($args -contains 'project') { $global:LASTEXITCODE = 1; return $null }
                $global:LASTEXITCODE = 0
                if ($args -contains 'organization') { return '{"info":"my-org"}' }
                return $null
            }
            $result = Get-AdoWorkspace
            $result | Should -Be ''
        }
    }

    Context 'No hardcoded ADO references' {
        It 'ado-helpers.ps1 contains no hardcoded org or project values' {
            $content = Get-Content "$PSScriptRoot/../lib/ado-helpers.ps1" -Raw
            $content | Should -Not -Match 'dangreen-msft'
            $content | Should -Not -Match 'PolyphonyRequiem'
        }

        It 'All production scripts contain no hardcoded ADO org/project' {
            $scripts = Get-ChildItem "$PSScriptRoot/../*.ps1" -Exclude '*.Tests.ps1'
            foreach ($script in $scripts) {
                $content = Get-Content $script.FullName -Raw
                $content | Should -Not -Match 'dangreen-msft' -Because "$($script.Name) should not hardcode org"
                $content | Should -Not -Match 'PolyphonyRequiem' -Because "$($script.Name) should not hardcode repo slug"
            }
        }

        It 'Shared libraries contain no hardcoded ADO org/project' {
            $libs = Get-ChildItem "$PSScriptRoot/../lib/*.ps1" -Exclude '*.Tests.ps1'
            foreach ($lib in $libs) {
                $content = Get-Content $lib.FullName -Raw
                $content | Should -Not -Match 'dangreen-msft' -Because "$($lib.Name) should not hardcode org"
                $content | Should -Not -Match 'PolyphonyRequiem' -Because "$($lib.Name) should not hardcode repo slug"
            }
        }
    }
}
