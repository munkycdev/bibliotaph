<#
.SYNOPSIS
    Runs the same checks as CI. CI calls this script one step at a time, so a green run here means a green run there.

.DESCRIPTION
    Steps, in CI's order:
      Restore    dotnet restore
      Build      Debug build; warnings are errors
      Licenses   writes licenses\packages beside the Debug build; fails on a licence that isn't GPL-3.0-compatible
      Test       every test project
      Smoke      the app's --smoke-test on the real window (Windows only)
      BannedApi  proves that a write API in Core fails the build with RS0030
      Release    Release build, which leaves out the worker's fault-injection requests

    With no -Step, every step runs. Off Windows, Smoke is skipped with a warning.

.EXAMPLE
    ./build/verify.ps1
.EXAMPLE
    ./build/verify.ps1 -SkipSmoke
.EXAMPLE
    ./build/verify.ps1 -Step Build, Test
#>
[CmdletBinding()]
param(
    [ValidateSet('Restore', 'Build', 'Licenses', 'Test', 'Smoke', 'BannedApi', 'Release')]
    [string[]] $Step = @('Restore', 'Build', 'Licenses', 'Test', 'Smoke', 'BannedApi', 'Release'),

    # Leaves out the smoke test, which opens the app's window for about a minute.
    [switch] $SkipSmoke
)

$ErrorActionPreference = 'Stop'
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

    $exe = Join-Path $root 'src/Bibliotaph.App/bin/Debug/net11.0-windows/Bibliotaph.exe'
    if (-not (Test-Path $exe)) { throw "Build the Debug configuration first: $exe is missing." }
    # A fresh data root each run, so a library left by an earlier run can't change what the smoke test sees.
    $temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
    $run = Join-Path $temp "bibliotaph-smoke-$([DateTime]::Now.ToString('yyyyMMdd-HHmmss'))"
    $data = Join-Path $run 'data'
    $files = Join-Path $run 'files'
    # The fixtures are copied out of the checkout so the app's folder holds nothing else.
    New-Item -ItemType Directory -Force $files | Out-Null
    Copy-Item (Join-Path $root 'tests/fixtures/smoke/smoke-book.pdf'), (Join-Path $root 'tests/fixtures/smoke/smoke-map.png') $files

    $app = Start-Process -FilePath $exe -ArgumentList '--smoke-test', '--data-root', "`"$data`"", '--smoke-files', "`"$files`"" -PassThru
    if (-not $app.WaitForExit(120000)) { $app.Kill(); throw 'Smoke test did not finish within 2 minutes.' }
    $log = @(Get-ChildItem (Join-Path $data 'logs') -Filter *.log | Get-Content)
    if ($app.ExitCode -eq 0) {
        if ($inCi) { $log }
        Remove-Item $run -Recurse -Force
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

function Invoke-Licenses {
    # Every package that ships gets licenses\packages\<id>.txt, which the About popup lists (the smoke test looks).
    # The release workflow runs the same tool on the published app.
    $app = Join-Path $root 'src/Bibliotaph.App/bin/Debug/net11.0-windows'
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

$order = 'Restore', 'Build', 'Licenses', 'Test', 'Smoke', 'BannedApi', 'Release'
foreach ($name in $order | Where-Object { $_ -in $Step }) {
    Write-Host "== $name" -ForegroundColor Cyan
    switch ($name) {
        'Restore' { Invoke-Dotnet restore $solution }
        # Debug builds contain the worker's fault-injection requests that the isolation tests use.
        'Build' { Invoke-Dotnet build $solution --configuration Debug --no-restore }
        'Licenses' { Invoke-Licenses }
        'Test' {
            Invoke-Dotnet test --solution $solution --configuration Debug --no-build --report-xunit-trx `
                --results-directory (Join-Path $root 'TestResults')
        }
        'Smoke' { Invoke-Smoke }
        'BannedApi' { Invoke-BannedApi }
        'Release' { Invoke-Dotnet build $solution --configuration Release --no-restore }
    }
}
$passed = $order | Where-Object { $_ -in $Step -and $_ -notin $skipped }
Write-Host "Passed: $($passed -join ', ')." -ForegroundColor Green
if ($skipped.Count -gt 0) { Write-Host "Skipped: $($skipped -join ', ')." -ForegroundColor Yellow }
