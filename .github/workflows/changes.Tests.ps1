<#
.SYNOPSIS
    Checks CI change detection against real commit histories.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$detector = Join-Path $PSScriptRoot 'changes.ps1'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$workspace = Join-Path $temporaryRoot ('protocross-ci-' + [Guid]::NewGuid().ToString('N'))
$repository = Join-Path $workspace 'repository'
$outputPath = Join-Path $workspace 'output'
$checks = 0

function Invoke-Git {
    $result = git @args
    if ($LASTEXITCODE -ne 0) { throw "git $args failed" }
    return $result
}

function Save-Commit {
    Invoke-Git add -A | Out-Null
    Invoke-Git -c user.name=CI -c user.email=ci@example.invalid commit -qm checkpoint | Out-Null
    return Invoke-Git rev-parse HEAD
}

function Assert-Decision {
    param([string] $Name, [string] $Base, [string] $Head, [bool] $Expected, [switch] $PullRequest)
    Set-Content -LiteralPath $outputPath -Value ''
    & $detector -Base $Base -Head $Head -PullRequest:$PullRequest -OutputPath $outputPath | Out-Null
    $actual = (Get-Content -Raw -LiteralPath $outputPath).Trim()
    $wanted = "run_tests=$($Expected.ToString().ToLowerInvariant())"
    if ($actual -ne $wanted) { throw "$Name expected $wanted, got $actual" }
    $script:checks++
}

New-Item -ItemType Directory -Path $repository | Out-Null
Push-Location $repository
try {
    Invoke-Git init -q | Out-Null
    Invoke-Git config core.autocrlf false | Out-Null
    Set-Content README.md 'initial'
    Set-Content source.cs 'initial'
    $initial = Save-Commit

    Set-Content README.md 'updated'
    Set-Content 'Guide with spaces.MD' 'new documentation'
    $docs = Save-Commit
    Assert-Decision 'documentation push' $initial $docs $false
    Assert-Decision 'documentation pull request' $initial $docs $false -PullRequest

    Set-Content source.cs 'updated'
    Set-Content README.md 'mixed change'
    $mixed = Save-Commit
    Assert-Decision 'mixed change' $docs $mixed $true -PullRequest
    Set-Content README.md 'last commit is documentation'
    $tip = Save-Commit
    Assert-Decision 'earlier source change in pull request' $initial $tip $true -PullRequest
    Assert-Decision 'documentation push after source push' $mixed $tip $false

    Move-Item -LiteralPath source.cs -Destination renamed.md
    $renamed = Save-Commit
    Assert-Decision 'source renamed to documentation' $tip $renamed $true
    Set-Content build.yml 'workflow change'
    $workflow = Save-Commit
    Assert-Decision 'workflow change' $renamed $workflow $true
    Remove-Item -LiteralPath build.yml
    $deleted = Save-Commit
    Assert-Decision 'deleted build input' $workflow $deleted $true
    Assert-Decision 'initial push' ('0' * 40) $docs $true

    Invoke-Git checkout -qb docs-branch $docs | Out-Null
    Set-Content README.md 'branch documentation'
    $branchDocs = Save-Commit
    Invoke-Git checkout -qb advanced-base $docs | Out-Null
    Set-Content source.cs 'base advanced'
    $advancedBase = Save-Commit
    Assert-Decision 'base changes are not pull request changes' $advancedBase $branchDocs $false -PullRequest

    "Passed $checks change-detection checks."
} finally {
    Pop-Location
    $resolvedWorkspace = [IO.Path]::GetFullPath($workspace)
    if (!$resolvedWorkspace.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedWorkspace) -notmatch '^protocross-ci-[a-f0-9]{32}$') {
        throw 'Refusing to remove a temporary directory outside the test workspace.'
    }
    Remove-Item -LiteralPath $resolvedWorkspace -Recurse -Force
}
