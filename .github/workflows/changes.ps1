<#
.SYNOPSIS
    Decides whether the changed tree needs the build and test suites.

.DESCRIPTION
    Only Markdown is exempt. A renamed source must still count as a source change, so renames are
    read as a deletion and an addition. A pull request is compared with its merge base, while a push
    is compared with the previous tip. An initial push has no previous tree and runs the suites.
    The source key identifies every non-Markdown entry in the checked-out tree, including its mode
    and blob. CI remembers successful validation under that key, so a documentation update to a
    source-bearing pull request can reuse it without hiding an untested source or base change.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Base,

    [Parameter(Mandatory)]
    [string] $Head,

    [switch] $PullRequest,

    [string] $SourceTree = 'HEAD',

    [Parameter(Mandatory)]
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$entries = @(git -c core.quotepath=false ls-tree -r --full-tree $SourceTree)
if ($LASTEXITCODE -ne 0) {
    throw 'Cannot read the source tree.'
}
$sourceEntries = @($entries | Where-Object { $_ -notmatch '\t.*\.md$' })
$sourceBytes = [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $sourceEntries))
$sourceKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes)).ToLowerInvariant()
"source_key=$sourceKey" | Add-Content -LiteralPath $OutputPath

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
    'The diff includes source or build inputs; the suites are needed unless this source tree already passed.'
} else {
    'Only Markdown changed; the required checks pass without running the suites.'
}
