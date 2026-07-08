$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Require-Env {
    param([Parameter(Mandatory = $true)][string]$Name)

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Name is required."
    }

    return $value
}

$version = Require-Env "VERSION"
$runtime = Require-Env "RUNTIME"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$sourceGitBuild = [Environment]::GetEnvironmentVariable("SOURCEGIT_BUILD")
if ([string]::IsNullOrWhiteSpace($sourceGitBuild)) {
    $sourceGitBuild = Join-Path $repoRoot "publish"
} elseif (-not [System.IO.Path]::IsPathRooted($sourceGitBuild)) {
    $sourceGitBuild = Join-Path $repoRoot $sourceGitBuild
}
$singleFileBuild = Join-Path $repoRoot "build\SourceGit-single-$runtime"
$project = Join-Path $repoRoot "src\SourceGit.csproj"
$normalZip = Join-Path $repoRoot "build\sourcegit_$version.$runtime.zip"
$singleFileZip = Join-Path $repoRoot "build\sourcegit_$version.$runtime.single-exe.zip"

if (-not (Test-Path -LiteralPath $sourceGitBuild -PathType Container)) {
    throw "SourceGit publish output not found: $sourceGitBuild"
}

Get-ChildItem -LiteralPath $sourceGitBuild -Recurse -Filter "*.pdb" -File | Remove-Item -Force
Get-ChildItem -LiteralPath $sourceGitBuild -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force
Remove-Item -LiteralPath $normalZip -Force -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath $sourceGitBuild -DestinationPath $normalZip -Force
Write-Host "Created $normalZip"

Remove-Item -LiteralPath $singleFileBuild -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $singleFileZip -Force -ErrorAction SilentlyContinue

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", $runtime,
    "-o", $singleFileBuild,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

# Windows packaging now emits a convenience single-exe archive in addition to
# the normal framework directory package; it must be produced by publish rather
# than by compressing the directory output.
Write-Host "Running: dotnet $($publishArgs -join ' ')"
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "single-exe publish failed with exit code $LASTEXITCODE"
}

$singleExe = Join-Path $singleFileBuild "SourceGit.exe"
if (-not (Test-Path -LiteralPath $singleExe -PathType Leaf)) {
    throw "Single executable not found: $singleExe"
}

$singleFileToolDir = Join-Path $singleFileBuild "Resources\PrefabDiffTool"
if (-not (Test-Path -LiteralPath $singleFileToolDir -PathType Container)) {
    throw "Bundled prefab diff tool not found: $singleFileToolDir"
}

Get-ChildItem -LiteralPath $singleFileBuild -Recurse -Filter "*.pdb" -File | Remove-Item -Force
Get-ChildItem -LiteralPath $singleFileBuild -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force
Compress-Archive -LiteralPath @($singleExe, (Join-Path $singleFileBuild "Resources")) -DestinationPath $singleFileZip -Force
Write-Host "Created $singleFileZip"
