[CmdletBinding()]
param(
    [string]$Remote = "origin",

    [switch]$SkipLocalTagDelete,

    [switch]$SkipRemoteTagDelete,

    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-GitOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return ($output -join "`n").Trim()
}

$insideWorkTree = Get-GitOutput -Arguments @("rev-parse", "--is-inside-work-tree")
if ($insideWorkTree -ne "true") {
    throw "This script must be run inside a git working tree."
}

$latestTags = @(git tag --sort=-creatordate --format="%(refname:short)" | Select-Object -First 1)
if ($LASTEXITCODE -ne 0) {
    throw "git tag failed with exit code $LASTEXITCODE."
}

$latestTag = ($latestTags -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($latestTag)) {
    Write-Host "No local tags were found."
    exit 0
}

$tagCommit = Get-GitOutput -Arguments @("rev-list", "-n", "1", $latestTag)
$tagShortCommit = Get-GitOutput -Arguments @("rev-parse", "--short", $tagCommit)
$tagSubject = Get-GitOutput -Arguments @("log", "-1", "--pretty=%s", $tagCommit)
$remoteUrl = Get-GitOutput -Arguments @("remote", "get-url", $Remote)

Write-Host "Latest tag: $latestTag"
Write-Host "Commit:     $tagShortCommit $tagSubject"
Write-Host "Remote:     $Remote ($remoteUrl)"

if (-not $Force) {
    $answer = Read-Host "Delete this tag locally and from remote? [y/N]"
    if ($answer -notmatch '^(y|yes)$') {
        Write-Host "Canceled."
        exit 1
    }
}

if (-not $SkipLocalTagDelete) {
    Invoke-Git -Arguments @("tag", "-d", $latestTag)
    Write-Host "Deleted local tag '$latestTag'."
}

if (-not $SkipRemoteTagDelete) {
    $remoteTag = & git ls-remote --tags $Remote "refs/tags/$latestTag"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to check remote tag '$latestTag' on '$Remote'."
    }

    if ([string]::IsNullOrWhiteSpace(($remoteTag -join "`n"))) {
        Write-Host "Remote tag '$latestTag' was not found on '$Remote'."
    }
    else {
        Invoke-Git -Arguments @("push", $Remote, "--delete", $latestTag)
        Write-Host "Deleted remote tag '$latestTag' from '$Remote'."
    }
}
