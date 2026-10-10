<#
.SYNOPSIS
    Downloads the built-in AI's engine, llama.cpp's llama-server (Vulkan build, MIT licence), into artifacts\aiworker,
    and the tiny model the engine test runs, into artifacts\test-models. Both are pinned in build\engine.json and
    checked by SHA-256; a file that doesn't match is deleted and the script fails.

.DESCRIPTION
    The app build copies artifacts\aiworker into aiworker\ beside Bibliotaph.exe (Bibliotaph.App.csproj), so run this
    before building. Downloads are kept in artifacts\cache, so a second run only checks hashes. Windows only: the
    engine is a Windows program.

    With an empty sha256 in engine.json, the script looks up the newest llama.cpp release (or the model's newest
    revision), downloads it, and fails printing what to pin. That is how a pin is first made or moved on.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$pin = Get-Content (Join-Path $PSScriptRoot 'engine.json') -Raw | ConvertFrom-Json
$cache = Join-Path $root 'artifacts/cache'
New-Item -ItemType Directory -Force $cache | Out-Null
$inCi = $env:GITHUB_ACTIONS -eq 'true'

function Get-Sha256([string] $path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Save-Pinned([string] $url, [string] $sha256, [string] $path) {
    if (-not (Test-Path $path) -or (Get-Sha256 $path) -ne $sha256) {
        Write-Host "Downloading $url"
        Invoke-WebRequest $url -OutFile "$path.part"
        Move-Item -Force "$path.part" $path
    }
    $actual = Get-Sha256 $path
    if ($actual -ne $sha256) {
        Remove-Item $path
        throw "$url has SHA-256 $actual, not the $sha256 pinned in build/engine.json."
    }
}

function Write-Pin([string] $what) {
    # An annotation as well, so the values can be read off the PR's checks.
    if ($inCi) { Write-Host "::error title=Pin $($what.Split(':')[0])::$what" }
    throw "Nothing pinned yet. Put this in build/engine.json: $what"
}

# The engine: llama.cpp's Windows Vulkan build. Vulkan runs on Nvidia, AMD and Intel cards, and on the processor
# when there's no usable card (Built-in AI plan, choice 2).
if (-not $pin.engine.sha256) {
    $headers = @{ 'User-Agent' = 'bibliotaph-build' }
    if ($env:GH_TOKEN) { $headers.Authorization = "Bearer $env:GH_TOKEN" }
    $release = Invoke-RestMethod 'https://api.github.com/repos/ggml-org/llama.cpp/releases/latest' -Headers $headers
    $asset = $release.assets | Where-Object { $_.name -match '-bin-win-vulkan-x64\.zip$' } | Select-Object -First 1
    if (-not $asset) { throw "llama.cpp $($release.tag_name) has no Windows Vulkan build." }
    $file = Join-Path $cache $asset.name
    Invoke-WebRequest $asset.browser_download_url -OutFile $file
    Write-Pin "engine: release=$($release.tag_name) asset=$($asset.name) sha256=$(Get-Sha256 $file) size=$($asset.size)"
}
$zip = Join-Path $cache $pin.engine.asset
Save-Pinned "https://github.com/ggml-org/llama.cpp/releases/download/$($pin.engine.release)/$($pin.engine.asset)" $pin.engine.sha256 $zip
$engine = Join-Path $root 'artifacts/aiworker'
if (Test-Path $engine) { Remove-Item $engine -Recurse -Force }
Expand-Archive $zip $engine
# Some releases put everything in a folder of its own; the app expects llama-server.exe at the top.
$server = Get-ChildItem $engine -Recurse -Filter llama-server.exe | Select-Object -First 1
if (-not $server) { throw "$($pin.engine.asset) has no llama-server.exe." }
if ($server.DirectoryName -ne (Resolve-Path $engine).Path) {
    Get-ChildItem $server.DirectoryName | Move-Item -Destination $engine
}
Set-Content (Join-Path $engine 'engine.txt') "llama.cpp $($pin.engine.release)" -Encoding utf8NoBOM
Write-Host "llama-server from llama.cpp $($pin.engine.release) is in $engine."

# The engine test's model: a 260K-parameter story model from llama.cpp's own test set, small enough for the processor.
$model = $pin.testModel
if (-not $model.sha256) {
    $info = Invoke-RestMethod "https://huggingface.co/api/models/$($model.repo)"
    $file = Join-Path $cache (Split-Path $model.path -Leaf)
    Invoke-WebRequest "https://huggingface.co/$($model.repo)/resolve/$($info.sha)/$($model.path)" -OutFile $file
    Write-Pin "testModel: revision=$($info.sha) sha256=$(Get-Sha256 $file)"
}
$models = Join-Path $root 'artifacts/test-models'
New-Item -ItemType Directory -Force $models | Out-Null
Save-Pinned "https://huggingface.co/$($model.repo)/resolve/$($model.revision)/$($model.path)" $model.sha256 (Join-Path $models (Split-Path $model.path -Leaf))
Write-Host "The engine test's model is in $models."
