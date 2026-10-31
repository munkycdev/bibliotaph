<#
.SYNOPSIS
    Cuts a release: counts the commits on main, shows the version that gives, asks, then tags and pushes.

.DESCRIPTION
    A release is a tag vMAJOR.MINOR.N on main, where N is the count of commits on main at the tagged commit; the
    release workflow refuses any other. This script does the steps CONTRIBUTING.md lists, in order, from your PC:

      1. Fetches the remote and reads MAJOR.MINOR from Directory.Build.props.
      2. Counts the commits on the remote's main: that is N, and the version is MAJOR.MINOR.N.
      3. Checks that CHANGELOG.md has the "## MAJOR.MINOR" section and that no tag for the version exists.
      4. If "## Unreleased" holds bullets, moves them into a "New in MAJOR.MINOR.N (date):" block at the top of the
         section, as a commit "Release notes for MAJOR.MINOR.N" pushed to main first, so the notes are in the tagged
         commit (N counts that commit). The commit is made in a temporary worktree; your checkout isn't touched.
      5. Shows the commit, the version and what will be pushed, and asks before anything leaves your PC.
      6. Tags the commit and pushes the tag. The release workflow then builds, runs the tests on that commit beside the
         build and leaves a draft release: publish it on GitHub, keeping every file (Setup.exe, the full and delta packages and
         releases.win.json), or installed copies can't update.

    A new MAJOR.MINOR is a pull request (Directory.Build.props, CHANGELOG.md, README.md, CONTRIBUTING.md and the
    architecture doc), not this script.

.EXAMPLE
    ./build/release.ps1
.EXAMPLE
    ./build/release.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # The remote and branch releases are cut from.
    [string] $Remote = 'origin',
    [string] $Branch = 'main'
)

$ErrorActionPreference = 'Stop'
# git writes progress to stderr; that isn't an error.
$PSNativeCommandUseErrorActionPreference = $false

# Windows PowerShell 5.1 lacks utf8NoBOM and ternaries. Run in PowerShell 7 instead.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if (-not $pwsh) { throw 'release.ps1 needs PowerShell 7. Install it with: winget install Microsoft.PowerShell' }
    $command = "& '$($PSCommandPath.Replace("'", "''"))' -Remote '$Remote' -Branch '$Branch'"
    if ($WhatIfPreference) { $command += ' -WhatIf' }
    Write-Host 'Running in PowerShell 7 (pwsh), since this is Windows PowerShell.' -ForegroundColor Yellow
    & $pwsh.Source -NoProfile -Command $command
    exit $LASTEXITCODE
}

$root = Split-Path $PSScriptRoot -Parent

function Invoke-Git {
    param([string] $Directory, [Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)
    $output = & git -C $Directory @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed." }
    $output
}

function Write-Step([string] $Text) { Write-Host $Text -ForegroundColor Cyan }

# 1. The remote as it is now, and MAJOR.MINOR.
Write-Step "Fetching $Remote…"
Invoke-Git $root fetch --quiet --tags $Remote $Branch
$props = [xml] (Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
$local = $props.SelectSingleNode('//Version').InnerText
if ($local -notmatch '^(\d+)\.(\d+)\.0$') { throw "Directory.Build.props has Version $local; a local build is MAJOR.MINOR.0." }
$minor = "$($Matches[1]).$($Matches[2])"

$remoteUrl = Invoke-Git $root remote get-url $Remote
$slug = if ($remoteUrl -match 'github\.com[:/](.+?)(?:\.git)?/?$') { $Matches[1] } else { $null }
$gh = Get-Command gh -ErrorAction SilentlyContinue

# 2. The count on main: the next build number.
$tip = "$Remote/$Branch"
$count = [int] (Invoke-Git $root rev-list --count $tip)
$tipSha = Invoke-Git $root rev-parse --short $tip
$tipSubject = Invoke-Git $root log -1 --format=%s $tip

# 3. The notes. Bullets still under Unreleased become the new build's block, which is one more commit on main.
$changelogPath = Join-Path $root 'CHANGELOG.md'
# git writes the file as UTF-8. Read in the console's code page, its dashes and accents came out garbled, and each
# release wrote them back worse.
$consoleEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try { $changelog = Invoke-Git $root show "${tip}:CHANGELOG.md" }
finally { [Console]::OutputEncoding = $consoleEncoding }
$lines = [string[]] @($changelog)
$unreleased = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -match '^## Unreleased\s*$' })
if ($unreleased -lt 0) { throw 'CHANGELOG.md has no "## Unreleased" section.' }
$section = [array]::FindIndex($lines, $unreleased + 1, [Predicate[string]] { param($l) $l.StartsWith('## ') })
if ($section -lt 0) { throw 'CHANGELOG.md has no version section after "## Unreleased".' }
if ($lines[$section] -notmatch "^## $([regex]::Escape($minor))( |$)") {
    throw "CHANGELOG.md's first version section is '$($lines[$section])', not '## $minor'. A new MAJOR.MINOR is a pull request; see CONTRIBUTING.md."
}
$bullets = @($lines[($unreleased + 1)..($section - 1)] | Where-Object { $_.Trim().Length -gt 0 })
$notesCommit = $bullets.Count -gt 0
$number = if ($notesCommit) { $count + 1 } else { $count }
$version = "$minor.$number"
$tag = "v$version"

# No tag for the version, on the remote or here.
$remoteTag = Invoke-Git $root ls-remote --tags $Remote "refs/tags/$tag"
if ($remoteTag) { throw "$tag already exists on $Remote." }
if (Invoke-Git $root tag --list $tag) { throw "$tag already exists here. Delete it with: git tag -d $tag" }

$published = $null
if ($gh -and $slug) {
    $published = & $gh.Source api "repos/$slug/releases/latest" --jq .tag_name 2>$null
    if ($LASTEXITCODE -ne 0) { $published = $null }
}

# 5. What will happen, then the question.
Write-Host ''
Write-Host "Release $tag" -ForegroundColor Green
Write-Host "  from      $tip at $tipSha  $tipSubject  (commit $count)"
if ($notesCommit) {
    $bulletCount = @($bullets | Where-Object { $_ -match '^- ' }).Count
    $word = if ($bulletCount -eq 1) { '1 bullet' } else { "$bulletCount bullets" }
    Write-Host "  notes     $word move from Unreleased into 'New in $version'; a commit 'Release notes for $version' is pushed to $Branch first (commit $number)"
} else {
    Write-Host "  notes     nothing under Unreleased; the release's notes are the '## $minor' section as it stands"
}
Write-Host "  published $(if ($published) { "$published is the latest release" } elseif ($gh) { 'no release is published yet' } else { 'unknown; gh is not installed' })"
Write-Host '  tests     the release workflow runs the tests on the tagged commit beside its build'
Write-Host ''

$action = if ($notesCommit) { "push the release notes commit to $Branch, then tag it $tag and push the tag" } else { "tag $tipSha $tag and push the tag" }
if (-not $PSCmdlet.ShouldProcess("$slug $Branch", $action)) { return }
$answer = Read-Host "Go ahead and $action? [y/N]"
if ($answer -notmatch '^y(es)?$') { Write-Host 'Nothing was pushed.'; return }

# 4. The notes commit, in a worktree of its own so the checkout here stays as it is.
$sha = $tipSha
if ($notesCommit) {
    Write-Step "Committing the release notes for $version…"
    $date = (Get-Date).ToString('d MMMM yyyy', [cultureinfo]::InvariantCulture)
    $block = @("New in $version ($date):", '') + $bullets + @('')
    # Unreleased keeps its heading and one blank line; the block goes under the version heading and its blank line.
    $rest = @($lines[($section + 1)..($lines.Count - 1)])
    if ($rest.Count -gt 0 -and $rest[0].Trim().Length -eq 0) { $rest = @($rest | Select-Object -Skip 1) }
    $kept = @($lines[0..$unreleased]) + @('') + @($lines[$section], '') + $block + $rest
    $work = Join-Path ([IO.Path]::GetTempPath()) 'bibliotaph-release'
    if (Test-Path $work) { Invoke-Git $root worktree remove --force $work }
    Invoke-Git $root worktree prune
    Invoke-Git $root worktree add --detach --quiet $work $tip
    try {
        [IO.File]::WriteAllText((Join-Path $work 'CHANGELOG.md'), ($kept -join "`n") + "`n")
        Invoke-Git $work add CHANGELOG.md
        Invoke-Git $work commit --quiet "--message=Release notes for $version"
        Invoke-Git $work push --quiet $Remote "HEAD:refs/heads/$Branch"
        $sha = Invoke-Git $work rev-parse --short HEAD
    }
    finally {
        Invoke-Git $root worktree remove --force $work
    }
    Invoke-Git $root fetch --quiet $Remote $Branch
    $pushed = [int] (Invoke-Git $root rev-list --count $tip)
    if ($pushed -ne $number) {
        throw "$Branch is at commit $pushed, not ${number}: something else was pushed meanwhile. Fix the 'New in' heading in CHANGELOG.md and run this again."
    }
}

# 6. The tag.
Write-Step "Tagging $sha as $tag and pushing it…"
Invoke-Git $root tag $tag $sha
Invoke-Git $root push --quiet $Remote "refs/tags/$tag"

Write-Host ''
Write-Host "$tag is pushed. The release workflow is building it; the draft appears once CI passes on $sha." -ForegroundColor Green
if ($slug) {
    Write-Host "  workflow  https://github.com/$slug/actions/workflows/release.yml"
    Write-Host "  releases  https://github.com/$slug/releases"
}
Write-Host '  Publish the draft keeping every file: Setup.exe, the full and delta packages and releases.win.json.'
