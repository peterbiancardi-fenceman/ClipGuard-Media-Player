# ClipGuard Media Player: LAV Filters (GPL v2+, https://github.com/Nevcairiel/LAVFilters),
# the official x64 release, unpacked into the given filters folder.
# Usage: get_lav.ps1 <filters folder>
param([Parameter(Mandatory = $true)][string]$Out)
$ErrorActionPreference = 'Stop'
try {
    $headers = @{ 'User-Agent' = 'ClipGuard-build' }
    if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
    $rel = Invoke-RestMethod -Headers $headers -Uri 'https://api.github.com/repos/Nevcairiel/LAVFilters/releases?per_page=5'
    $asset = $null
    foreach ($r in $rel) {
        if ($r.draft -or $r.prerelease) { continue }
        $asset = $r.assets | Where-Object { $_.name -match '(?i)x64\.zip$' } | Select-Object -First 1
        if ($asset) { Write-Host "[INFO] LAV Filters $($r.tag_name): $($asset.name)"; break }
    }
    if (-not $asset) { Write-Host "[ERROR] no LAV Filters x64 zip found"; exit 1 }
    $tmp = Join-Path $env:TEMP ('lav_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $zip = Join-Path $tmp $asset.name
    Invoke-WebRequest -UseBasicParsing -Headers @{ 'User-Agent' = 'ClipGuard-build' } -Uri $asset.browser_download_url -OutFile $zip
    $sz = "$env:ProgramFiles\7-Zip\7z.exe"
    if (-not (Test-Path $sz)) { $sz = '7z' }
    & $sz x -y "-o$tmp\x" $zip | Out-Null
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    $n = 0
    Get-ChildItem -Path "$tmp\x" -Recurse -File | Where-Object { $_.Extension -match '(?i)^\.(ax|dll|manifest)$' } | ForEach-Object {
        Copy-Item -Force $_.FullName (Join-Path $Out $_.Name); $n++
    }
    $lic = Get-ChildItem -Path "$tmp\x" -Recurse -File | Where-Object { $_.Name -match '(?i)^(copying|license)' } | Select-Object -First 1
    if ($lic) { Copy-Item -Force $lic.FullName (Join-Path $Out 'LAVFilters-LICENSE.txt') }
    if (-not (Test-Path (Join-Path $Out 'LAVVideo.ax'))) { Write-Host "[ERROR] LAVVideo.ax not in $($asset.name)"; exit 1 }
    Write-Host "[INFO] LAV Filters: $n files -> $Out"
} catch {
    Write-Host "[ERROR] LAV Filters download failed: $($_.Exception.Message)"
    exit 1
}
exit 0
