<#
.SYNOPSIS
    Decides whether the changed tree needs the build and test suites.

.DESCRIPTION
    Only Markdown is exempt. A renamed source must still count as a source change, so renames are
    read as a deletion and an addition. A pull request is compared with its merge base, while a push
    is compared with the previous tip. An initial push has no previous tree and runs the suites.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Base,

    [Parameter(Mandatory)]
    [string] $Head,

    [switch] $PullRequest,

    [Parameter(Mandatory)]
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runTests = $true
if ($Base -notmatch '^0+$') {
    if ($PullRequest) {
        $baseCommit = git merge-base $Base $Head
        if ($LASTEXITCODE -ne 0) {
            throw 'Cannot find the pull request merge base.'
        }
    } else {
        $baseCommit = $Base
    }

    $paths = @(git -c core.quotepath=false diff --name-only --no-renames --no-ext-diff --no-textconv $baseCommit $Head --)
    if ($LASTEXITCODE -ne 0) {
        throw 'Cannot read the changed paths.'
    }

    $runTests = @($paths | Where-Object { $_ -notmatch '\.md$' }).Count -gt 0
}

"run_tests=$($runTests.ToString().ToLowerInvariant())" | Add-Content -LiteralPath $OutputPath
if ($runTests) {
    'Source or build inputs changed; running the suites.'
} else {
    'Only Markdown changed; the required checks pass without running the suites.'
}
