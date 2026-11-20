# ClipGuard+ Player (Test): ArgyllCMS for the projector calibration (2.66.0).
# Usage: get_argyll.ps1 <output folder>
# ArgyllCMS is AGPL v3 (https://www.argyllcms.com). Takes the current Windows 64-bit
# release from argyllcms.com and copies spotread, colprof, collink, the Rec709 / Rec2020
# reference profiles and the licence into the output folder (the player's own copy).
# Exits 0 without files when it can't (calibration then says Argyll was not found).
param([Parameter(Mandatory = $true)][string]$Out)
$ErrorActionPreference = 'Stop'
try {
    $ua = @{ 'User-Agent' = 'ClipGuard-build' }
    $name = $null
    try {
        $page = (Invoke-WebRequest -UseBasicParsing -Headers $ua -Uri 'https://www.argyllcms.com/downloadwin.html').Content
        $m = [regex]::Match($page, 'Argyll_V[0-9.]+_win64_exe\.zip')
        if ($m.Success) { $name = $m.Value }
    } catch { Write-Host "[WARN] argyllcms.com download page: $($_.Exception.Message)" }
    if (-not $name) { $name = 'Argyll_V3.5.0_win64_exe.zip' }
    Write-Host "[INFO] ArgyllCMS: $name"
    $tmp = Join-Path $env:TEMP ('argyll_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $zip = Join-Path $tmp $name
    Invoke-WebRequest -UseBasicParsing -Headers $ua -Uri ("https://www.argyllcms.com/" + $name) -OutFile $zip
    $sz = "$env:ProgramFiles\7-Zip\7z.exe"
    if (-not (Test-Path $sz)) { $sz = '7z' }
    & $sz x -y "-o$tmp\x" $zip | Out-Null
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    $n = 0
    foreach ($f in @('spotread.exe', 'colprof.exe', 'collink.exe', 'Rec709.icm', 'Rec2020.icm')) {
        $hit = Get-ChildItem -Path "$tmp\x" -Recurse -File -Filter $f | Select-Object -First 1
        if ($hit) { Copy-Item -Force $hit.FullName (Join-Path $Out $f); $n++ } else { Write-Host "[WARN] $f not in $name" }
    }
    $lic = Get-ChildItem -Path "$tmp\x" -Recurse -File | Where-Object { $_.Name -match '(?i)^(license|copying|agpl).*\.txt$' } | Select-Object -First 1
    if ($lic) { Copy-Item -Force $lic.FullName (Join-Path $Out 'ArgyllCMS-LICENSE.txt') }
    Set-Content -Path (Join-Path $Out 'ArgyllCMS-SOURCE.txt') -Value ("ArgyllCMS (" + $name + ") by Graeme Gill, AGPL v3.`r`nSource: https://www.argyllcms.com/downloadsrc.html`r`nUsed unmodified by the player's projector calibration.")
    Write-Host "[INFO] ArgyllCMS: $n files -> $Out"
} catch {
    Write-Host "[WARN] ArgyllCMS download failed: $($_.Exception.Message)"
}
exit 0
