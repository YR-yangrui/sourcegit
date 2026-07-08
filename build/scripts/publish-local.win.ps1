param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Output = "publish",
    [switch]$Aot,
    [switch]$SingleFile,
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Resolve paths from this script so it can be run from any working directory.
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$project = Join-Path $repoRoot "src\SourceGit.csproj"
$publishDir = if ([System.IO.Path]::IsPathRooted($Output)) {
    $Output
} else {
    Join-Path $repoRoot $Output
}

Write-Host "Repository: $repoRoot"
Write-Host "Publish output: $publishDir"

$running = Get-Process SourceGit -ErrorAction SilentlyContinue
if ($running) {
    $ids = ($running | ForEach-Object { $_.Id }) -join ", "
    Write-Host "Closing running SourceGit process(es): $ids"
    $running | Stop-Process -Force
} else {
    Write-Host "No running SourceGit process found."
}

if (Test-Path -LiteralPath $publishDir) {
    Write-Host "Cleaning publish output: $publishDir"
    $cleaned = $false
    for ($i = 1; $i -le 5; $i++) {
        try {
            Remove-Item -LiteralPath $publishDir -Recurse -Force
            $cleaned = $true
            break
        } catch {
            if ($i -eq 5) {
                throw
            }

            Write-Host "Publish output is still locked; retrying cleanup ($i/5)..."
            Start-Sleep -Milliseconds 500
        }
    }

    if (-not $cleaned) {
        throw "Failed to clean publish output: $publishDir"
    }
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

$publishArgs = @(
    "publish", $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $publishDir
)

# Local publish defaults to non-AOT to avoid slow native compilation during daily iteration.
if (-not $Aot) {
    $publishArgs += "-p:DisableAOT=true"
}

if ($SingleFile) {
    $publishArgs += @(
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false"
    )
}

if ($SelfContained) {
    $publishArgs += "-p:SelfContained=true"
}

Write-Host "Running: dotnet $($publishArgs -join ' ')"
& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $publishDir "SourceGit.exe"
if (Test-Path $exe) {
    $item = Get-Item $exe
    Write-Host "Published: $($item.FullName)"
    Write-Host "Updated: $($item.LastWriteTime)"
    Write-Host "Size: $($item.Length) bytes"
} else {
    Write-Warning "Publish finished, but SourceGit.exe was not found at $exe"
}
