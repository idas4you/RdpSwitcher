[CmdletBinding()]
param(
    [string]$Tag,

    [string]$Remote = "origin",

    [switch]$SkipRemoteTagCheck
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

function Show-RecentTags {
    $recentTags = @(git tag --sort=-creatordate --format="%(refname:short)  %(creatordate:short)  %(subject)" | Select-Object -First 3)
    if ($LASTEXITCODE -ne 0) {
        throw "git tag failed with exit code $LASTEXITCODE."
    }

    if ($recentTags.Count -eq 0) {
        Write-Host "Recent tags: none"
        return
    }

    Write-Host "Recent tags:"
    foreach ($recentTag in $recentTags) {
        Write-Host "  $recentTag"
    }
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    Show-RecentTags
    $Tag = Read-Host "Release tag (example: v1.0.0)"
}

$Tag = $Tag.Trim()
if ($Tag -match '^V\d+\.\d+\.\d+$') {
    $Tag = "v$($Tag.Substring(1))"
}

if ($Tag -notmatch '^v?\d+\.\d+\.\d+$') {
    throw "Invalid tag '$Tag'. Use v1.2.3 or 1.2.3."
}

$remoteUrl = Get-GitOutput -Arguments @("remote", "get-url", $Remote)
$headSha = Get-GitOutput -Arguments @("rev-parse", "HEAD")
$headShortSha = Get-GitOutput -Arguments @("rev-parse", "--short", "HEAD")
$headSubject = Get-GitOutput -Arguments @("log", "-1", "--pretty=%s")
$status = Get-GitOutput -Arguments @("status", "--short")

Write-Host "Tag:     $Tag"
Write-Host "Commit:  $headShortSha $headSubject"
Write-Host "Remote:  $Remote ($remoteUrl)"

if (-not [string]::IsNullOrWhiteSpace($status)) {
    Write-Warning "There are uncommitted changes. The tag will point to the last commit only, not the working tree."
    $answer = Read-Host "Continue? [y/N]"
    if ($answer -notmatch '^(y|yes)$') {
        Write-Host "Canceled."
        exit 1
    }
}

$localTagCommit = & git rev-parse -q --verify "refs/tags/$Tag^{commit}" 2>$null
if ($LASTEXITCODE -eq 0) {
    $localTagCommit = ($localTagCommit -join "`n").Trim()
    if ($localTagCommit -ne $headSha) {
        throw "Local tag '$Tag' already exists at $localTagCommit, not HEAD $headSha."
    }

    Write-Host "Local tag '$Tag' already points to HEAD."
}
else {
    Invoke-Git -Arguments @("tag", "-a", $Tag, $headSha, "-m", "Release $Tag")
    Write-Host "Created local annotated tag '$Tag'."
}

if (-not $SkipRemoteTagCheck) {
    $remoteTag = & git ls-remote --tags $Remote "refs/tags/$Tag"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to check remote tag '$Tag' on '$Remote'."
    }

    if (-not [string]::IsNullOrWhiteSpace(($remoteTag -join "`n"))) {
        throw "Remote tag '$Tag' already exists on '$Remote'. Delete it manually if you really need to replace it."
    }
}

Invoke-Git -Arguments @("push", $Remote, "refs/tags/$Tag")
Write-Host "Pushed '$Tag'. GitHub Actions will build and publish the MSI release."
