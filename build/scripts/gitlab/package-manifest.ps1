$ErrorActionPreference = 'Stop'

function Require-Env {
    param([Parameter(Mandatory = $true)][string]$Name)

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Name is required."
    }

    return $value
}

function Get-LinuxRuntime {
    param([Parameter(Mandatory = $true)][string]$FileName)

    if ($FileName -match '(amd64|x86_64)') {
        return 'linux-x64'
    }

    if ($FileName -match '(aarch64|arm64)') {
        return 'linux-arm64'
    }

    return 'unknown'
}

function New-Asset {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo]$File,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$BaseUrl
    )

    $hash = Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256
    [ordered]@{
        runtime = $Runtime
        kind = $Kind
        fileName = $File.Name
        url = "$BaseUrl/$($File.Name)"
        sha256 = $hash.Hash.ToLowerInvariant()
    }
}

function Get-GitLabHeaders {
    $headers = @{}
    $releaseToken = [Environment]::GetEnvironmentVariable('GITLAB_RELEASE_TOKEN')
    $jobToken = [Environment]::GetEnvironmentVariable('CI_JOB_TOKEN')

    if (-not [string]::IsNullOrWhiteSpace($releaseToken)) {
        $headers['PRIVATE-TOKEN'] = $releaseToken
    } elseif (-not [string]::IsNullOrWhiteSpace($jobToken)) {
        $headers['JOB-TOKEN'] = $jobToken
    } else {
        throw 'GITLAB_RELEASE_TOKEN or CI_JOB_TOKEN is required.'
    }

    return $headers
}

function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & git @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed.`n$output"
    }

    return @($output)
}

function Resolve-GitCommit {
    param([Parameter(Mandatory = $true)][string]$Ref)

    $commit = Try-ResolveGitCommit -Ref $Ref
    if (-not [string]::IsNullOrWhiteSpace($commit)) {
        return $commit
    }

    Invoke-Git -Arguments @('fetch', '--tags', '--force', '--quiet') | Out-Null
    $commit = Try-ResolveGitCommit -Ref $Ref
    if (-not [string]::IsNullOrWhiteSpace($commit)) {
        return $commit
    }

    throw "Unable to resolve Git ref '$Ref'."
}

function Try-ResolveGitCommit {
    param([AllowEmptyString()][string]$Ref)

    if ([string]::IsNullOrWhiteSpace($Ref)) {
        return $null
    }

    try {
        $output = @(Invoke-Git -Arguments @('rev-parse', "$Ref^{commit}"))
        return $output[0].Trim()
    } catch {
        return $null
    }
}

function Get-GitLabReleases {
    param(
        [Parameter(Mandatory = $true)][string]$ApiUrl,
        [Parameter(Mandatory = $true)][string]$ProjectId
    )

    $headers = Get-GitLabHeaders
    $encodedProject = [System.Uri]::EscapeDataString($ProjectId)
    $baseUrl = "$ApiUrl/projects/$encodedProject/releases"
    $page = 1
    $releases = @()

    while ($true) {
        $uri = "${baseUrl}?order_by=released_at&sort=desc&per_page=100&page=$page"
        $response = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers
        $batch = @($response)
        if ($null -eq $response -or $batch.Count -eq 0) {
            break
        }

        $releases += $batch
        $page += 1
    }

    return $releases
}

function Get-PreviousRelease {
    param(
        [Parameter(Mandatory = $true)][string]$ReleaseKind,
        [Parameter(Mandatory = $true)][string]$ReleaseVersion,
        [Parameter(Mandatory = $true)][string]$ApiUrl,
        [Parameter(Mandatory = $true)][string]$ProjectId
    )

    switch ($ReleaseKind) {
        'stable' { $pattern = '^stable-\d+\.\d+\.\d+\.[0-9a-fA-F]+$' }
        'scheduled-nightly' { $pattern = '^nightly-\d{4}\.\d{2}\.\d{2}\.[0-9a-fA-F]+$' }
        default { return $null }
    }

    $releases = Get-GitLabReleases -ApiUrl $ApiUrl -ProjectId $ProjectId
    foreach ($release in $releases) {
        $tag = [string]$release.tag_name
        if ($tag -ne $ReleaseVersion -and $tag -match $pattern) {
            return $release
        }
    }

    return $null
}

function Get-ReleaseManifestCommit {
    param([Parameter(Mandatory = $true)]$Release)

    $headers = Get-GitLabHeaders
    foreach ($link in @($Release.assets.links)) {
        if ([string]$link.name -ne 'sourcegit-update.json') {
            continue
        }

        $url = [string]$link.url
        if ([string]::IsNullOrWhiteSpace($url)) {
            $url = [string]$link.direct_asset_url
        }
        if ([string]::IsNullOrWhiteSpace($url)) {
            return $null
        }

        try {
            $manifest = Invoke-RestMethod -Method Get -Uri $url -Headers $headers
            $commit = [string]$manifest.commit
            if (-not [string]::IsNullOrWhiteSpace($commit)) {
                return $commit.Trim()
            }
        } catch {
            return $null
        }

        return $null
    }

    return $null
}

function Get-CommitHashes {
    param(
        [string]$PreviousCommit,
        [Parameter(Mandatory = $true)][string]$CurrentCommit
    )

    if ([string]::IsNullOrWhiteSpace($PreviousCommit)) {
        return Invoke-Git -Arguments @('log', '--reverse', '--format=%H', $CurrentCommit)
    }

    return Invoke-Git -Arguments @('log', '--reverse', '--format=%H', "$PreviousCommit..$CurrentCommit")
}

function Normalize-CommitBody {
    param([AllowEmptyString()][string]$Body)

    $normalized = $Body.TrimEnd()
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return ''
    }

    if ($normalized -notmatch "`n" -and ($normalized.Contains('\n') -or $normalized.Contains('\r'))) {
        $normalized = $normalized.Replace('\r\n', "`n").Replace('\n', "`n").Replace('\r', "`n")
    }

    return $normalized
}

function Add-ChangelogItems {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Section,
        [Parameter(Mandatory = $true)]$Target
    )

    if ([string]::IsNullOrEmpty($Section)) {
        return
    }

    foreach ($line in ($Section -split "`r?`n")) {
        $candidate = $line.Trim()
        if ($candidate.StartsWith('- ') -and -not $candidate.EndsWith('(NO CHANGELOG)', [System.StringComparison]::OrdinalIgnoreCase)) {
            $Target.Add($candidate) | Out-Null
        }
    }
}

function Add-CommitBodyToChangelog {
    param(
        [Parameter(Mandatory = $true)][string]$Commit,
        [Parameter(Mandatory = $true)]$EnglishItems,
        [Parameter(Mandatory = $true)]$ChineseItems
    )

    $body = Normalize-CommitBody -Body ((Invoke-Git -Arguments @('show', '-s', '--format=%b', $Commit)) -join "`n")
    if ([string]::IsNullOrWhiteSpace($body)) {
        return
    }

    $separator = [regex]::Match($body, '(?m)^\s*----------------\s*$')
    if ($separator.Success) {
        $english = $body.Substring(0, $separator.Index)
        $chinese = $body.Substring($separator.Index + $separator.Length)
    } else {
        $english = $body
        $chinese = ''
    }

    Add-ChangelogItems -Section $english -Target $EnglishItems
    Add-ChangelogItems -Section $chinese -Target $ChineseItems
}

function New-InitialStableReleaseNotes {
    return "- Initial stable release.`n----------------`n- 首个 stable 发布版本。"
}

function New-InitialScheduledNightlyReleaseNotes {
    return "- Initial scheduled nightly release.`n----------------`n- 首个 scheduled nightly 发布版本。"
}

function New-ReleaseNotesWithPowerShell {
    param(
        [Parameter(Mandatory = $true)][string]$ReleaseKind,
        [Parameter(Mandatory = $true)][string]$ReleaseVersion,
        [Parameter(Mandatory = $true)][string]$Commit,
        [Parameter(Mandatory = $true)][string]$ApiUrl,
        [Parameter(Mandatory = $true)][string]$ProjectId
    )

    if ($ReleaseKind -eq 'manual-nightly') {
        return "- Manual nightly validation build.`n----------------`n- 手动 nightly 验证构建。"
    }

    $previousRelease = Get-PreviousRelease -ReleaseKind $ReleaseKind -ReleaseVersion $ReleaseVersion -ApiUrl $ApiUrl -ProjectId $ProjectId
    if ($ReleaseKind -eq 'stable' -and $null -eq $previousRelease) {
        return New-InitialStableReleaseNotes
    }
    if ($ReleaseKind -eq 'scheduled-nightly' -and $null -eq $previousRelease) {
        return New-InitialScheduledNightlyReleaseNotes
    }

    $previousCommit = Try-ResolveGitCommit -Ref (Get-ReleaseManifestCommit -Release $previousRelease)
    if ([string]::IsNullOrWhiteSpace($previousCommit)) {
        $previousTag = [string]$previousRelease.tag_name
        $previousCommit = Resolve-GitCommit -Ref $previousTag
    }

    $commits = Get-CommitHashes -PreviousCommit $previousCommit -CurrentCommit $Commit
    $englishItems = [System.Collections.Generic.List[string]]::new()
    $chineseItems = [System.Collections.Generic.List[string]]::new()

    foreach ($hash in $commits) {
        Add-CommitBodyToChangelog -Commit $hash -EnglishItems $englishItems -ChineseItems $chineseItems
    }

    if ($englishItems.Count -eq 0 -and $chineseItems.Count -eq 0) {
        return "- Maintenance update.`n----------------`n- 维护更新。"
    }

    $lines = @()
    $lines += $englishItems.ToArray()
    $lines += '----------------'
    $lines += $chineseItems.ToArray()
    return ($lines -join "`n")
}

function Get-PythonCommand {
    foreach ($name in @('python3', 'python')) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            return $command.Source
        }
    }

    return $null
}

function New-ReleaseNotes {
    param(
        [Parameter(Mandatory = $true)][string]$ReleaseKind,
        [Parameter(Mandatory = $true)][string]$ReleaseVersion,
        [Parameter(Mandatory = $true)][string]$Commit,
        [Parameter(Mandatory = $true)][string]$ApiUrl,
        [Parameter(Mandatory = $true)][string]$ProjectId
    )

    if ($ReleaseKind -eq 'manual-nightly') {
        return "- Manual nightly validation build.`n----------------`n- 手动 nightly 验证构建。"
    }

    $helper = Join-Path $PSScriptRoot 'generate-changelog.py'
    $python = Get-PythonCommand
    if ($null -ne $python -and (Test-Path -LiteralPath $helper -PathType Leaf)) {
        $output = & $python $helper --release-kind $ReleaseKind --release-version $ReleaseVersion --commit $Commit 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "generate-changelog.py failed.`n$output"
        }

        return ($output -join "`n").TrimEnd()
    }

    return New-ReleaseNotesWithPowerShell -ReleaseKind $ReleaseKind -ReleaseVersion $ReleaseVersion -Commit $Commit -ApiUrl $ApiUrl -ProjectId $ProjectId
}

$releaseVersion = Require-Env 'RELEASE_VERSION'
$packageVersion = Require-Env 'PACKAGE_VERSION'
$baseVersion = Require-Env 'BASE_VERSION'
$channel = Require-Env 'UPDATE_CHANNEL'
$releaseKind = Require-Env 'RELEASE_KIND'
$commit = Require-Env 'CI_COMMIT_SHA'
$pipelineId = Require-Env 'CI_PIPELINE_ID'
$pipelineIid = Require-Env 'CI_PIPELINE_IID'
$publishedAt = Require-Env 'BUILD_DATE'
$apiUrl = (Require-Env 'CI_API_V4_URL').TrimEnd('/')
$projectId = Require-Env 'CI_PROJECT_ID'

switch ($releaseKind) {
    'stable' { $expectedChannel = 'stable' }
    'scheduled-nightly' { $expectedChannel = 'nightly' }
    'manual-nightly' { $expectedChannel = 'nightly' }
    default { throw 'RELEASE_KIND must be stable, scheduled-nightly, or manual-nightly.' }
}

if ($channel -ne $expectedChannel) {
    throw "UPDATE_CHANNEL must be $expectedChannel for RELEASE_KIND=$releaseKind."
}

$buildDir = Resolve-Path -LiteralPath 'build'
$baseUrl = "$apiUrl/projects/$projectId/packages/generic/sourcegit/$packageVersion"
$escapedPackageVersion = [Regex]::Escape($packageVersion)
$assets = @()

Get-ChildItem -LiteralPath $buildDir -File -Filter "sourcegit_$packageVersion.*.zip" | Sort-Object Name | ForEach-Object {
    if ($_.Name -match "^sourcegit_$escapedPackageVersion\.(win-x64|win-arm64)\.zip$") {
        $assets += New-Asset -File $_ -Runtime $Matches[1] -Kind 'self-update-zip' -BaseUrl $baseUrl
    } elseif ($_.Name -match "^sourcegit_$escapedPackageVersion\.(osx-x64|osx-arm64)\.zip$") {
        $assets += New-Asset -File $_ -Runtime $Matches[1] -Kind 'zip' -BaseUrl $baseUrl
    } else {
        $assets += New-Asset -File $_ -Runtime 'unknown' -Kind 'zip' -BaseUrl $baseUrl
    }
}

Get-ChildItem -LiteralPath $buildDir -File -Filter '*.AppImage' | Where-Object { $_.Name -like "sourcegit-$packageVersion*" } | Sort-Object Name | ForEach-Object {
    $assets += New-Asset -File $_ -Runtime (Get-LinuxRuntime $_.Name) -Kind 'AppImage' -BaseUrl $baseUrl
}

Get-ChildItem -LiteralPath $buildDir -File -Filter '*.deb' | Where-Object { $_.Name -like "sourcegit_$packageVersion*" } | Sort-Object Name | ForEach-Object {
    $assets += New-Asset -File $_ -Runtime (Get-LinuxRuntime $_.Name) -Kind 'deb' -BaseUrl $baseUrl
}

Get-ChildItem -LiteralPath $buildDir -File -Filter '*.rpm' | Where-Object { $_.Name -like "sourcegit-$packageVersion*" } | Sort-Object Name | ForEach-Object {
    $assets += New-Asset -File $_ -Runtime (Get-LinuxRuntime $_.Name) -Kind 'rpm' -BaseUrl $baseUrl
}

$releaseNotes = New-ReleaseNotes -ReleaseKind $releaseKind -ReleaseVersion $releaseVersion -Commit $commit -ApiUrl $apiUrl -ProjectId $projectId

$manifest = [ordered]@{
    version = $releaseVersion
    baseVersion = $baseVersion
    packageVersion = $packageVersion
    channel = $channel
    commit = $commit
    pipelineId = $pipelineId
    pipelineIid = $pipelineIid
    publishedAt = $publishedAt
    releaseNotes = $releaseNotes
    assets = $assets
}

$manifestPath = Join-Path $buildDir 'sourcegit-update.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "Created $manifestPath with $($assets.Count) assets."
