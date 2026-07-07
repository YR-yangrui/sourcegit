$ErrorActionPreference = 'Stop'

function Require-Env {
    param([Parameter(Mandatory = $true)][string]$Name)

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Name is required."
    }

    return $value
}

$version = Require-Env 'PACKAGE_VERSION'
$runtime = Require-Env 'RUNTIME'
$sourceGitBuild = [Environment]::GetEnvironmentVariable('SOURCEGIT_BUILD')
$updaterBuild = [Environment]::GetEnvironmentVariable('UPDATER_BUILD')

if ([string]::IsNullOrWhiteSpace($sourceGitBuild)) {
    $sourceGitBuild = Join-Path 'build' 'SourceGit'
}

if ([string]::IsNullOrWhiteSpace($updaterBuild)) {
    $updaterBuild = Join-Path 'build' 'Updater'
}

if (-not (Test-Path -LiteralPath $sourceGitBuild -PathType Container)) {
    throw "SourceGit publish output not found: $sourceGitBuild"
}

$updaterExe = Join-Path $updaterBuild 'sourcegit-updater.exe'
if (-not (Test-Path -LiteralPath $updaterExe -PathType Leaf)) {
    throw "Updater executable not found: $updaterExe"
}

$packageRoot = Join-Path 'build' "package-$runtime"
$sourceGitRoot = Join-Path $packageRoot 'SourceGit'
$zipPath = Join-Path 'build' "sourcegit_$version.$runtime.zip"

Remove-Item -LiteralPath $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $sourceGitRoot -Force | Out-Null

Copy-Item -Path (Join-Path $sourceGitBuild '*') -Destination $sourceGitRoot -Recurse -Force
Get-ChildItem -LiteralPath $sourceGitRoot -Recurse -Filter '*.pdb' -File | Remove-Item -Force
Copy-Item -LiteralPath $updaterExe -Destination (Join-Path $packageRoot 'sourcegit-updater.exe') -Force

Compress-Archive -LiteralPath @((Join-Path $packageRoot 'sourcegit-updater.exe'), $sourceGitRoot) -DestinationPath $zipPath -Force
Write-Host "Created $zipPath"
