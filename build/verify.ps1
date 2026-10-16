<#
.SYNOPSIS
    Runs the same checks as CI. CI calls this script one step at a time, so a green run here means a green run there.

.DESCRIPTION
    Steps, in CI's order:
      Restore    dotnet restore
      Engine     downloads the built-in AI's engine and the engine test's model (fetch-engine.ps1; Windows only)
      Build      Debug build; warnings are errors
      Licenses   writes licenses\packages beside the Debug build; fails on a licence that isn't GPL-3.0-compatible
      Test       every test project; off Windows, all but the app's
      Smoke      the app's --smoke-test on the real window (Windows only)
      BannedApi  proves that a write API in Core fails the build with RS0030
      Release    Release build, which leaves out the worker's fault-injection requests

    With no -Step, every step runs. Off Windows, Engine, Smoke and the app's tests are skipped with a warning, and the
    engine test skips itself.

.EXAMPLE
    ./build/verify.ps1
.EXAMPLE
    ./build/verify.ps1 -SkipSmoke
.EXAMPLE
    ./build/verify.ps1 -Step Build, Test
#>
[CmdletBinding()]
param(
    [ValidateSet('Restore', 'Engine', 'Build', 'Licenses', 'Test', 'Smoke', 'BannedApi', 'Release')]
    [string[]] $Step = @('Restore', 'Engine', 'Build', 'Licenses', 'Test', 'Smoke', 'BannedApi', 'Release'),

    # Leaves out the smoke test, which opens the app's window for a few minutes.
    [switch] $SkipSmoke
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 has no $IsWindows, so the Windows-only steps (Engine, Smoke) would quietly skip themselves.
# Run in PowerShell 7 instead.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if (-not $pwsh) { throw 'verify.ps1 needs PowerShell 7. Install it with: winget install Microsoft.PowerShell' }
    $command = "& '$($PSCommandPath.Replace("'", "''"))'"
    if ($PSBoundParameters.ContainsKey('Step')) { $command += " -Step $($Step -join ',')" }
    if ($SkipSmoke) { $command += ' -SkipSmoke' }
    Write-Host 'Running in PowerShell 7 (pwsh), since this is Windows PowerShell.' -ForegroundColor Yellow
    & $pwsh.Source -NoProfile -Command $command
    exit $LASTEXITCODE
}

$root = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $root 'Bibliotaph.slnx'
$inCi = $env:GITHUB_ACTIONS -eq 'true'
$explicitSteps = $PSBoundParameters.ContainsKey('Step')
$skipped = [Collections.Generic.List[string]]::new()

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed with exit code $LASTEXITCODE." }
}

function Invoke-Smoke {
    if ($SkipSmoke) { Write-Warning 'Smoke test skipped (-SkipSmoke).'; $skipped.Add('Smoke'); return }
    if (-not $IsWindows) {
        if ($explicitSteps) { throw 'The smoke test needs Windows: it opens the real WPF window.' }
        Write-Warning 'Smoke test skipped: it needs Windows. CI still runs it.'
        $skipped.Add('Smoke')
        return
    }

    $exe = Join-Path $root 'src/Bibliotaph.App/bin/Debug/net11.0-windows10.0.19041.0/Bibliotaph.exe'
    if (-not (Test-Path $exe)) { throw "Build the Debug configuration first: $exe is missing." }
    # A fresh data root each run, so a library left by an earlier run can't change what the smoke test sees.
    $temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
    $run = Join-Path $temp "bibliotaph-smoke-$([DateTime]::Now.ToString('yyyyMMdd-HHmmss'))"
    $data = Join-Path $run 'data'
    $files = Join-Path $run 'files'
    # The fixtures are copied out of the checkout so the app's folder holds nothing else.
    New-Item -ItemType Directory -Force $files | Out-Null
    Copy-Item (Join-Path $root 'tests/fixtures/smoke/smoke-book.pdf'), (Join-Path $root 'tests/fixtures/smoke/smoke-map.png') $files
    # A made-up second PC's library sync file, in the folder the smoke test syncs with (beside the data root, never in it).
    $sync = Join-Path $run 'sync/Bibliotaph sync'
    New-Item -ItemType Directory -Force $sync | Out-Null
    Copy-Item (Join-Path $root 'tests/fixtures/smoke/smoke-sync.json') (Join-Path $sync 'SMOKE-PC-2.json')
    # A made-up online library page saved with its images, which the smoke test imports (beside the data root, never in it).
    Copy-Item (Join-Path $root 'tests/fixtures/online') (Join-Path $run 'online') -Recurse

    $app = Start-Process -FilePath $exe -ArgumentList '--smoke-test', '--data-root', "`"$data`"", '--smoke-files', "`"$files`"" -PassThru
    # The checks take about two minutes on a developer's PC and grow with each feature, so the limit leaves room: it is
    # there to stop a hung window, not to time the checks.
    if (-not $app.WaitForExit(600000)) {
        $app.Kill()
        @(Get-ChildItem (Join-Path $data 'logs') -Filter *.log -ErrorAction SilentlyContinue | Get-Content) | Select-Object -Last 20
        throw 'Smoke test did not finish within 10 minutes.'
    }
    $log = @(Get-ChildItem (Join-Path $data 'logs') -Filter *.log | Get-Content)
    if ($app.ExitCode -eq 0) {
        if ($inCi) { $log }
        # The in-app browser's processes can hold its profile for a moment after the app closes.
        foreach ($try in 1..10) {
            try { Remove-Item $run -Recurse -Force -ErrorAction Stop; break }
            catch { if ($try -eq 10) { Write-Warning "The smoke test's folder $run couldn't be removed: $($_.Exception.Message)" } else { Start-Sleep 1 } }
        }
        Write-Host 'Smoke test passed.'
        return
    }

    $log
    if ($inCi) {
        # Errors also become annotations, readable on the PR's checks without opening the log.
        for ($i = 0; $i -lt $log.Count; $i++) {
            if ($log[$i] -match '\[(ERR|FTL)\]') {
                $detail = ($log[$i..([Math]::Min($i + 12, $log.Count - 1))] -join '%0A')
                Write-Host "::error title=Smoke test::$detail"
            }
        }
    }
    throw "Smoke test failed with exit code $($app.ExitCode). Its data and logs are in $run."
}

function Invoke-Engine {
    if (-not $IsWindows) {
        if ($explicitSteps) { throw 'The built-in AI engine is a Windows program.' }
        Write-Warning 'Engine skipped: it is a Windows program. CI still downloads it and runs the engine test.'
        $skipped.Add('Engine')
        return
    }
    & (Join-Path $PSScriptRoot 'fetch-engine.ps1')
}

function Invoke-Licenses {
    # Every package that ships gets licenses\packages\<id>.txt, which the About popup lists (the smoke test looks).
    # The release workflow runs the same tool on the published app.
    $app = Join-Path $root 'src/Bibliotaph.App/bin/Debug/net11.0-windows10.0.19041.0'
    $tool = Join-Path $root 'tools/LicenseNotices/bin/Debug/net11.0/LicenseNotices.dll'
    if (-not (Test-Path $tool) -or -not (Test-Path (Join-Path $app 'Bibliotaph.deps.json'))) { throw 'Build the Debug configuration first.' }
    Invoke-Dotnet $tool $app
}

function Invoke-BannedApi {
    # Source files are read-only by construction: a write API in Core must fail the build.
    $probe = Join-Path $root 'src/Bibliotaph.Core/BannedApiProbe.cs'
    Set-Content $probe 'namespace Bibliotaph.Core; static class BannedApiProbe { static void Write() => System.IO.File.WriteAllText("x", "y"); }'
    try {
        $output = & dotnet build (Join-Path $root 'src/Bibliotaph.Core/Bibliotaph.Core.csproj') --configuration Debug --no-restore 2>&1
        $failed = $LASTEXITCODE -ne 0
    }
    finally {
        Remove-Item $probe
    }
    if (-not $failed) { throw 'File.WriteAllText in Core built successfully; the banned-API analyzer is not working.' }
    if (-not ($output -match 'RS0030')) { $output; throw 'Core failed to build, but not because of RS0030.' }
    Write-Host 'Core rejected File.WriteAllText with RS0030, as it should.'
    # The expected build failure left a non-zero exit code behind; the step itself passed.
    $global:LASTEXITCODE = 0
}

$order = 'Restore', 'Engine', 'Build', 'Licenses', 'Test', 'Smoke', 'BannedApi', 'Release'
# From the repository root wherever it's started: dotnet finds global.json, which picks the SDK and the test runner,
# by looking up from the current folder.
Push-Location $root
try {
    foreach ($name in $order | Where-Object { $_ -in $Step }) {
        Write-Host "== $name" -ForegroundColor Cyan
        switch ($name) {
            'Restore' { Invoke-Dotnet restore $solution }
            'Engine' { Invoke-Engine }
            # Debug builds contain the worker's fault-injection requests that the isolation tests use.
            'Build' { Invoke-Dotnet build $solution --configuration Debug --no-restore }
            'Licenses' { Invoke-Licenses }
            'Test' {
                $options = '--configuration', 'Debug', '--no-build', '--report-xunit-trx', '--results-directory', (Join-Path $root 'TestResults')
                if ($IsWindows) { Invoke-Dotnet test --solution $solution @options }
                else {
                    # The app's tests need WPF, which runs only on Windows; the Build step still compiled them.
                    Write-Warning 'App tests skipped: they need Windows. CI still runs them.'
                    $skipped.Add('App tests')
                    $projects = ([xml](Get-Content $solution)).Solution.Folder.Project.Path |
                        Where-Object { $_ -like 'tests/*.Tests.csproj' -and $_ -notlike '*/Bibliotaph.App.Tests.csproj' }
                    foreach ($project in $projects) { Invoke-Dotnet test --project (Join-Path $root $project) @options }
                }
            }
            'Smoke' { Invoke-Smoke }
            'BannedApi' { Invoke-BannedApi }
            'Release' { Invoke-Dotnet build $solution --configuration Release --no-restore }
        }
    }
}
finally {
    Pop-Location
}
$passed = $order | Where-Object { $_ -in $Step -and $_ -notin $skipped }
Write-Host "Passed: $($passed -join ', ')." -ForegroundColor Green
if ($skipped.Count -gt 0) { Write-Host "Skipped: $($skipped -join ', ')." -ForegroundColor Yellow }
