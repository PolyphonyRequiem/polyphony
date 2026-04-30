BeforeAll {
    . "$PSScriptRoot/../lib/gh-helpers.ps1"
}

Describe 'gh-helpers.ps1 — GitHub repo slug derivation (#2650)' {
    Context 'Get-RepoSlug' {
        It 'Derives slug from HTTPS remote URL' {
            Mock git { 'https://github.com/TestOrg/TestRepo.git' }
            $result = Get-RepoSlug
            $result | Should -Be 'TestOrg/TestRepo'
        }

        It 'Derives slug from SSH remote URL' {
            Mock git { 'git@github.com:TestOrg/TestRepo.git' }
            $result = Get-RepoSlug
            $result | Should -Be 'TestOrg/TestRepo'
        }

        It 'Derives slug from HTTPS URL without .git suffix' {
            Mock git { 'https://github.com/TestOrg/TestRepo' }
            $result = Get-RepoSlug
            $result | Should -Be 'TestOrg/TestRepo'
        }

        It 'Returns empty string when remote URL is not GitHub' {
            Mock git { 'https://dev.azure.com/org/project/_git/repo' }
            $result = Get-RepoSlug
            $result | Should -Be ''
        }

        It 'Returns empty string when git command fails' {
            Mock git { $null }
            $result = Get-RepoSlug
            $result | Should -Be ''
        }

        It 'Handles URL with extra path segments gracefully' {
            Mock git { 'https://github.com/MyOrg/my-repo.git' }
            $result = Get-RepoSlug
            $result | Should -Be 'MyOrg/my-repo'
        }
    }

    Context 'No hardcoded references' {
        It 'gh-helpers.ps1 contains no hardcoded repo slug' {
            $content = Get-Content "$PSScriptRoot/../lib/gh-helpers.ps1" -Raw
            $content | Should -Not -Match 'PolyphonyRequiem'
            $content | Should -Not -Match 'dangreen-msft'
        }
    }
}
