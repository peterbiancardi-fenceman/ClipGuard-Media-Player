# Builds ClipGuard Media Player Setup <version>.exe (Windows, Visual Studio 2022, Inno Setup).
#   1. the renderer from our fork of Unplugged's renderer (MPC Video Renderer based)
#   2. the engine, decklink_monitor.dll and DeckLinkSource.ax from our fork of Unplugged's engine
#   3. the player and the calibration meter (C#, .NET Framework 4.x)
#   4. the decoders and tools fetched from their official releases: LAV Filters, MPC-BE's
#      MPC Audio Renderer, ArgyllCMS
#   5. the installer
# Output: _bin\setup\ClipGuard Media Player Setup <version>.exe
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
$bin = Join-Path $repo '_bin'
$renderer = 'C:\cgmp-renderer'
$rendererBranch = 'clipguard-media-player'

function Step($name) { Write-Host "::group::$name" }
function EndStep { Write-Host "::endgroup::" }

$verLine = Select-String -Path "$repo\src\ClipGuardPlayer.cs" -Pattern 'MediaPlayerVersion = "([0-9.]+)";\s*// the public' | Select-Object -First 1
if (-not $verLine) { throw 'version not found in ClipGuardPlayer.cs' }
$version = $verLine.Matches[0].Groups[1].Value
Write-Host "ClipGuard Media Player $version"

$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
$vsdev = Join-Path $vs 'Common7\Tools\vsdevcmd.bat'

# ---- 1. renderer ------------------------------------------------------------------------
Step 'Renderer'
if (Test-Path $renderer) { Remove-Item -Recurse -Force $renderer }
git clone --branch $rendererBranch --recurse-submodules https://github.com/peterbiancardi-fenceman/unplugged-VideoRenderer.git $renderer
if ($LASTEXITCODE) { throw 'renderer clone failed' }
# ByteTrack-Eigen (MIT) and Eigen 3.4.0 (MPL 2), compiled into the renderer.
$bt = 'C:\Code\byte-track-eigen'
if (-not (Test-Path "$bt\include\BYTETracker.h")) {
    git clone --quiet https://github.com/cj-mills/byte-track-eigen.git $bt
    git -C $bt checkout --quiet a865158
}
if (-not (Test-Path "$bt\external\eigen\Eigen\Dense")) {
    git clone --quiet --depth 1 --branch 3.4.0 https://eigen.googlesource.com/mirror "$bt\external\eigen"
}
if (-not (Test-Path "$bt\external\eigen\Eigen\Dense")) {
    if (Test-Path "$bt\external\eigen") { Remove-Item -Recurse -Force "$bt\external\eigen" }
    git clone --quiet --depth 1 --branch 3.4.0 https://gitlab.com/libeigen/eigen.git "$bt\external\eigen"
}
if (-not (Test-Path "$bt\external\eigen\Eigen\Dense")) { throw 'could not fetch ByteTrack-Eigen / Eigen' }
$cmd = @"
call "$vsdev" -arch=amd64 -host_arch=amd64 -no_logo
cd /d "$renderer"
MSBuild.exe MpcVideoRenderer.sln /nologo /m /v:minimal /target:Build /p:Configuration=Release /p:Platform=x64
exit /b %errorlevel%
"@
Set-Content -Path "$env:TEMP\cgmp_renderer.cmd" -Value $cmd -Encoding ASCII
cmd /c "$env:TEMP\cgmp_renderer.cmd"
if ($LASTEXITCODE) { throw 'renderer build failed' }
$ax = Get-ChildItem -Path "$renderer\_bin" -Recurse -Filter 'unplugged_vr.ax' | Select-Object -First 1
if (-not $ax) { throw 'unplugged_vr.ax was not built' }
EndStep

# ---- 2. engine --------------------------------------------------------------------------
$env:RENDERER_DIR = $renderer
& "$PSScriptRoot\build_engine.ps1"
foreach ($f in 's3engine.dll', 'decklink_monitor.dll', 'DeckLinkSource.ax') {
    if (-not (Test-Path "$bin\engine\$f")) { throw "engine: $f missing" }
}

# ---- 3. player and meter ----------------------------------------------------------------
Step 'Player'
$app = Join-Path $bin 'ClipGuard Media Player'
if (Test-Path $app) { Remove-Item -Recurse -Force $app }
New-Item -ItemType Directory -Force "$app\filters", "$app\licenses" | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll'
$s = "$repo\src"
& $csc /nologo /target:winexe /optimize+ /platform:x64 '/define:TEST;PLAYER;STANDALONE;PUBLIC' /main:ClipGuard.PlayerProgram "/win32icon:$s\clipguard.ico" "/out:$app\ClipGuard Media Player.exe" @refs `
    "$s\ClipGuard.cs" "$s\ClipGuardPlayer.cs" "$s\ClipGuardPlex.cs" "$s\ClipGuardQr.cs"
if ($LASTEXITCODE) { throw 'player did not compile' }
& $csc /nologo /target:winexe /optimize+ '/define:TEST;PUBLIC' /main:ClipGuard.MeterProgram "/win32icon:$s\clipguard.ico" "/out:$app\ClipGuardMeter.exe" @refs `
    "$s\ClipGuard.cs" "$s\ClipGuardMeter.cs" "$s\ClipGuardCal.cs" "$s\ClipGuardPlex.cs" "$s\ClipGuardQr.cs"
if ($LASTEXITCODE) { throw 'meter did not compile' }
EndStep

# ---- 4. the package ---------------------------------------------------------------------
Step 'Package'
Copy-Item "$bin\engine\s3engine.dll", "$bin\engine\decklink_monitor.dll" $app
Copy-Item "$bin\engine\DeckLinkSource.ax" "$app\filters\"
Copy-Item $ax.FullName "$app\filters\"
foreach ($f in 'onnxruntime.dll', 'xgb_gamma_model.plxgb', 'xgb_highlight_rolloff.plxgb', 'xgb_shadow_recovery.plxgb', 'yolo26n-seg_op21.onnx', 'yolo26n-sem.onnx') {
    Copy-Item "$renderer\Source\onnxrt\$f" "$app\filters\"
}
Copy-Item "$s\lipsync.mkv" $app
Copy-Item "$repo\README.txt" $app
Copy-Item "$repo\LICENSE" "$app\LICENSE.txt"
Copy-Item "$repo\licenses\*" "$app\licenses\"
Copy-Item "$PSScriptRoot\ENGINE-LICENSE.txt" "$app\licenses\"
& "$PSScriptRoot\get_lav.ps1" -Out "$app\filters"
if ($LASTEXITCODE) { throw 'LAV Filters' }
& "$PSScriptRoot\get_audio_renderer.ps1" -Out "$app\filters\MpcAudioRenderer.ax"
if (-not (Test-Path "$app\filters\MpcAudioRenderer.ax")) { throw 'MPC Audio Renderer' }
& "$PSScriptRoot\get_argyll.ps1" -Out $app
if (-not (Test-Path "$app\spotread.exe")) { throw 'ArgyllCMS' }
foreach ($f in 'ArgyllCMS-LICENSE.txt', 'ArgyllCMS-SOURCE.txt') { if (Test-Path "$app\$f") { Move-Item -Force "$app\$f" "$app\licenses\" } }
EndStep

# ---- 5. installer -----------------------------------------------------------------------
Step 'Installer'
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { choco install innosetup -y --no-progress | Out-Null }
if (-not (Test-Path $iscc)) { throw 'Inno Setup not found' }
$out = Join-Path $bin 'setup'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
& $iscc /Q "/DAppVer=$version" "/DSrc=$app" "/O$out" "$repo\installer\MediaPlayer.iss"
if ($LASTEXITCODE) { throw 'installer did not build' }
Get-ChildItem $out | ForEach-Object { Write-Host "built: $($_.Name) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
"version=$version" | Out-File -Append -Encoding utf8 $env:GITHUB_OUTPUT
EndStep
