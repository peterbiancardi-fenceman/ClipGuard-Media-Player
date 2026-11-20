# ClipGuard Media Player: fetch MPC-BE's standalone MPC Audio Renderer (x64), the filter
# the player uses for WASAPI exclusive bitstream (TrueHD / Atmos / DTS-HD).
# Usage: get_audio_renderer.ps1 <output .ax path>
# MPC-BE is GPL v3 (https://github.com/Aleksoid1978/MPC-BE). Takes the newest release's
# standalone-filters archive, unpacks it and copies the 64-bit MpcAudioRenderer .ax.
# Exits 0 without a file when it can't (the player then falls back to DirectSound / PCM).
param([Parameter(Mandatory = $true)][string]$Out)
$ErrorActionPreference = 'Stop'
try {
    $headers = @{ 'User-Agent' = 'ClipGuard-build' }
    if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
    $rel = Invoke-RestMethod -Headers $headers -Uri 'https://api.github.com/repos/Aleksoid1978/MPC-BE/releases?per_page=5'
    $asset = $null
    foreach ($r in $rel) {
        if ($r.draft) { continue }
        $asset = $r.assets | Where-Object { $_.name -match '(?i)filter' -and $_.name -match '(?i)(x64|64)' -and $_.name -match '(?i)\.(7z|zip)$' } | Select-Object -First 1
        if ($asset) { Write-Host "[INFO] MPC-BE $($r.tag_name): $($asset.name)"; break }
    }
    if (-not $asset) {
        Write-Host "[WARN] no MPC-BE standalone filters x64 archive found; release assets were:"
        foreach ($r in $rel) { foreach ($a in $r.assets) { Write-Host "       $($r.tag_name) $($a.name)" } }
        exit 0
    }
    $tmp = Join-Path $env:TEMP ('mpcbe_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $arc = Join-Path $tmp $asset.name
    Invoke-WebRequest -Headers @{ 'User-Agent' = 'ClipGuard-build' } -Uri $asset.browser_download_url -OutFile $arc
    $sz = "$env:ProgramFiles\7-Zip\7z.exe"
    if (-not (Test-Path $sz)) { $sz = '7z' }
    & $sz x -y "-o$tmp\x" $arc | Out-Null
    $ax = Get-ChildItem -Path "$tmp\x" -Recurse -Filter 'MpcAudioRenderer*.ax'
    $pick = $ax | Where-Object { $_.Name -match '64' -or $_.FullName -match '(?i)\\x64\\' } | Select-Object -First 1
    if (-not $pick) { $pick = $ax | Select-Object -First 1 }
    if (-not $pick) {
        Write-Host "[WARN] MpcAudioRenderer .ax not in $($asset.name); it holds:"
        Get-ChildItem -Path "$tmp\x" -Recurse -File | ForEach-Object { Write-Host "       $($_.FullName.Substring($tmp.Length))" }
        exit 0
    }
    Copy-Item -Force $pick.FullName $Out
    Write-Host "[INFO] MPC Audio Renderer: $($pick.Name) -> $Out ($([math]::Round($pick.Length / 1KB)) KB)"
} catch {
    Write-Host "[WARN] MPC Audio Renderer download failed: $($_.Exception.Message)"
}
exit 0
