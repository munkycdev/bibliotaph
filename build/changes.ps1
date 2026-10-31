<#
.SYNOPSIS
    Says which of CI's checks a change needs: the Windows app's, the web app's, both or neither.

.DESCRIPTION
    Each changed file is matched to the projects that build it: the project whose folder it is in, and any project
    that takes it from elsewhere in the repository (a linked source file, an embedded resource, a bundled program).
    A project belongs to the Windows checks when the app, a Windows-run test project or a tool reaches it through
    project references, and to the web checks when the server or its tests reach it. Shared projects such as Core and
    Processing belong to both, so a change to them runs both.

    A file no project builds is matched by the rules below: the docs and other markdown no project embeds need
    neither, the server's infrastructure needs the web checks, and anything else (build scripts, workflows,
    Directory.*.props, global.json) needs both, so a file this script doesn't know runs everything.

    In GitHub Actions it writes windows=true|false and web=true|false to the step's outputs and a table to the run's
    summary.

.EXAMPLE
    ./build/changes.ps1 -Base origin/main
    What CI would check for this branch.
.EXAMPLE
    ./build/changes.ps1 -All
    Everything, as for Run workflow.
#>
[CmdletBinding(DefaultParameterSetName = 'Diff')]
param(
    # The commit the change is measured from: the files changed between where it and -Head meet, and -Head.
    [Parameter(Mandatory, ParameterSetName = 'Diff')]
    [string] $Base,

    [Parameter(ParameterSetName = 'Diff')]
    [string] $Head = 'HEAD',

    # Both sets of checks, with no diff.
    [Parameter(Mandatory, ParameterSetName = 'All')]
    [switch] $All
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path.Replace('\', '/').TrimEnd('/')

# What the web checks build and run: the server, and its tests on Linux. The Windows checks build the whole solution
# and run every other test project, so their entry points are the app, those test projects and the tools.
$webRoots = 'src/Bibliotaph.Server/Bibliotaph.Server.csproj', 'tests/Bibliotaph.Server.Tests/Bibliotaph.Server.Tests.csproj'

# Files no project builds, in order; the first match wins.
$rules = @(
    @{ Pattern = '^(docs|spikes)/'; Sides = @(); Why = 'docs or spikes' }
    @{ Pattern = '^[^/]+\.md$|^LICENSE|^\.gitignore$'; Sides = @(); Why = 'not built' }
    @{ Pattern = '^(infra/|\.dockerignore$|src/Bibliotaph\.Extension/)'; Sides = @('web'); Why = 'web server only' }
    @{ Pattern = '.'; Sides = @('windows', 'web'); Why = 'build or CI' }
)

function ConvertTo-RepoPath([string] $path) {
    $full = [IO.Path]::GetFullPath($path).Replace('\', '/')
    if (-not $full.StartsWith("$root/", [StringComparison]::Ordinal)) { return $null }
    $full.Substring($root.Length + 1)
}

function ConvertTo-Pattern([string] $glob) {
    # MSBuild wildcards: ** spans folders, * and ? stay within one.
    $pattern = [regex]::Escape($glob) -replace '\\\*\\\*/', '(.*/)?' -replace '\\\*\\\*', '.*' -replace '\\\*', '[^/]*' -replace '\\\?', '[^/]'
    "^$pattern$"
}

# Every project in the solution: its folder, the projects it references or bundles, and the files it takes from
# outside its folder.
$projects = @{}
$solution = [xml](Get-Content (Join-Path $root 'Bibliotaph.slnx'))
foreach ($path in $solution.SelectNodes('//Project/@Path').Value) {
    $folder = Split-Path $path -Parent
    $project = @{ Folder = $folder.Replace('\', '/'); References = [Collections.Generic.List[string]]::new(); Takes = [Collections.Generic.List[string]]::new() }
    $xml = [xml](Get-Content (Join-Path $root $path))
    foreach ($item in $xml.SelectNodes('//ItemGroup/*[@Include]')) {
        foreach ($include in $item.Include -split ';') {
            $include = $include.Trim().Replace('$(RepoRoot)', "$root/").Replace('\', '/')
            # Package names and items built from other properties aren't files this can place.
            if (-not $include.Contains('/') -or $include -match '[$@%]\(') { continue }
            $target = ConvertTo-RepoPath ([IO.Path]::Combine($root, $folder, $include))
            if (-not $target) { continue }
            if ($target.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) { $project.References.Add($target) }
            elseif (-not $target.StartsWith("$($project.Folder)/", [StringComparison]::Ordinal)) { $project.Takes.Add((ConvertTo-Pattern $target)) }
        }
    }
    $projects[$path] = $project
}

function Get-Reached([string[]] $start) {
    $reached = [Collections.Generic.HashSet[string]]::new()
    $queue = [Collections.Generic.Queue[string]]::new([string[]] $start)
    while ($queue.Count) {
        $next = $queue.Dequeue()
        if (-not $reached.Add($next) -or -not $projects.ContainsKey($next)) { continue }
        foreach ($reference in $projects[$next].References) { $queue.Enqueue($reference) }
    }
    $reached
}

$windowsRoots = @($projects.Keys | Where-Object { $_ -notlike 'src/*' -and $_ -notin $webRoots }) + 'src/Bibliotaph.App/Bibliotaph.App.csproj'
$windows = Get-Reached $windowsRoots
$web = Get-Reached $webRoots

function Get-Sides([string] $file) {
    $owners = @($projects.Keys | Where-Object {
        $project = $projects[$_]
        $file.StartsWith("$($project.Folder)/", [StringComparison]::Ordinal) -or ($project.Takes | Where-Object { $file -match $_ })
    })
    if ($owners) {
        $sides = @()
        if ($owners | Where-Object { $windows.Contains($_) }) { $sides += 'windows' }
        if ($owners | Where-Object { $web.Contains($_) }) { $sides += 'web' }
        $names = ($owners | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) -replace '^Bibliotaph\.', '' } | Sort-Object) -join ', '
        return @{ Sides = $sides; Why = $names }
    }
    $rule = $rules | Where-Object { $file -match $_.Pattern } | Select-Object -First 1
    @{ Sides = $rule.Sides; Why = $rule.Why }
}

$rows = [Collections.Generic.List[object]]::new()
if ($All) { $rows.Add([pscustomobject]@{ File = '(everything)'; Windows = $true; Web = $true; Why = 'no diff' }) }
else {
    # Renames count at both ends, so moving a file out of a project still checks that project.
    $files = @(git -C $root diff --name-only --no-renames "$Base...$Head")
    if ($LASTEXITCODE -ne 0) { throw "Listing the files changed between $Base and $Head failed." }
    foreach ($file in $files | Where-Object { $_ }) {
        $match = Get-Sides $file
        $rows.Add([pscustomobject]@{ File = $file; Windows = 'windows' -in $match.Sides; Web = 'web' -in $match.Sides; Why = $match.Why })
    }
}

$needsWindows = [bool]($rows | Where-Object Windows)
$needsWeb = [bool]($rows | Where-Object Web)
$mark = { param($on) if ($on) { 'yes' } else { '' } }

$rows | Format-Table @{ Label = 'Windows'; Expression = { & $mark $_.Windows } }, @{ Label = 'Web'; Expression = { & $mark $_.Web } }, File, Why -AutoSize |
    Out-String -Width 200 | Write-Host
Write-Host "Windows app checks: $(if ($needsWindows) { 'yes' } else { 'no' })"
Write-Host "Web app checks: $(if ($needsWeb) { 'yes' } else { 'no' })"

if ($env:GITHUB_OUTPUT) {
    "windows=$($needsWindows.ToString().ToLowerInvariant())" >> $env:GITHUB_OUTPUT
    "web=$($needsWeb.ToString().ToLowerInvariant())" >> $env:GITHUB_OUTPUT
}
if ($env:GITHUB_STEP_SUMMARY) {
    $summary = @(
        "Windows app checks: **$(if ($needsWindows) { 'yes' } else { 'no' })** · Web app checks: **$(if ($needsWeb) { 'yes' } else { 'no' })**"
        ''
        '| Windows | Web | File | Built by |'
        '|---|---|---|---|'
    ) + ($rows | ForEach-Object { "| $(& $mark $_.Windows) | $(& $mark $_.Web) | ``$($_.File)`` | $($_.Why) |" })
    $summary -join "`n" >> $env:GITHUB_STEP_SUMMARY
}
