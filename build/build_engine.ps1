# Builds the engine for ClipGuard Media Player from our public fork of Unplugged's engine
# (GPL v3): s3engine.dll, decklink_monitor.dll and DeckLinkSource.ax into _bin\engine.
# Source: https://github.com/peterbiancardi-fenceman/unplugged-mpc-hc (branch clipguard-media-player),
# Unplugged's engine (spedagadi/unplugged-mpc-hc) with our changes as commits.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
$um = 'C:\um'
$branch = 'clipguard-media-player'

function Step($name) { Write-Host "::group::$name" }
function EndStep { Write-Host "::endgroup::" }

Step 'Engine source'
if (Test-Path $um) { Remove-Item -Recurse -Force $um }
git clone --filter=blob:none --branch $branch https://github.com/peterbiancardi-fenceman/unplugged-mpc-hc.git $um
if ($LASTEXITCODE) { throw 'clone failed' }
git -C $um submodule update --init --depth 1
if ($LASTEXITCODE) {
    Write-Host 'shallow submodules failed, trying full'
    git -C $um submodule update --init
    if ($LASTEXITCODE) { throw 'submodules failed' }
}
EndStep

Step 'Tools'
choco install nasm -y --no-progress | Out-Null
$nasm = 'C:\Program Files\NASM\'
if (-not (Test-Path "$nasm\nasm.exe")) { throw 'nasm not found' }
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
$vsdev = Join-Path $vs 'Common7\Tools\vsdevcmd.bat'
EndStep

$common = @('/p:Configuration=Release', '/p:Platform=x64', "/p:SolutionDir=$um\", '/p:WindowsTargetPlatformVersion=10.0',
            "/p:NasmPath=$nasm", '/m', '/nologo', '/v:minimal')
$tp = "$um\src\thirdparty"
$projects = @(
    "$tp\zlib\zlib.vcxproj",
    "$tp\libiconv\libiconv.vcxproj",
    "$tp\fribidi\libfribidi.vcxproj",
    "$tp\libunibreak\libunibreak.vcxproj",
    "$tp\freetype2\freetype2.vcxproj",
    "$tp\harfbuzz\libharfbuzz.vcxproj",
    "$tp\libass\libass.vcxproj",
    "$tp\BaseClasses\BaseClasses.vcxproj",
    "$tp\unrar\unrar.vcxproj",
    "$um\src\DSUtil\DSUtil.vcxproj",
    "$um\src\Subtitles\Subtitles.vcxproj",
    "$um\src\SubPic\SubPic.vcxproj"
)
foreach ($p in $projects) {
    Step (Split-Path $p -Leaf)
    & $msbuild $p @common /p:BuildProjectReferences=false
    if ($LASTEXITCODE) { throw "build failed: $p" }
    EndStep
}

Step 'mpchc_engine'
& $msbuild "$um\src\mpchc_engine\mpchc_engine.vcxproj" @common "/p:ML_MPCVR_INCLUDE=$env:RENDERER_DIR\Source" `
    "/p:LAV_INCLUDE=$tp\LAVFilters\src\include" "/p:DECKLINK_INCLUDE=$tp\DeckLinkSource"
if ($LASTEXITCODE) { throw 'mpchc_engine failed' }
EndStep

Step 's3engine'
$lib = "$um\bin\lib\Release_x64"
$cl = @"
call "$vsdev" -arch=amd64 -host_arch=amd64 -no_logo
cd /d "$um\tools\s3engine"
cl /nologo /EHsc /std:c++17 /W3 /utf-8 /Ox /MT /DNDEBUG /DWIN32_LEAN_AND_MEAN /D_UNICODE /DUNICODE /I"..\..\src\mpchc_engine" /I"..\..\src\thirdparty\DeckLinkSource" /I"..\..\src\thirdparty" /I"..\..\src\thirdparty\BaseClasses" /I"..\..\src\SubPic" /I"..\..\src\Subtitles" /I"..\..\src\DSUtil" /I"..\..\src\filters\renderer\VideoRenderers" s3engine.cpp /LD /Fe:s3engine.dll /link "$lib\mpchc_engine.lib" /LIBPATH:"$lib" /LIBPATH:"..\..\src\thirdparty\BaseClasses\bin\lib\Release_x64" BaseClasses.lib SubPic.lib Subtitles.lib DSUtil.lib unrar.lib libass.lib libharfbuzz.lib freetype2.lib libfribidi.lib libunibreak.lib zlib.lib libiconv.lib strmiids.lib mfuuid.lib mf.lib mfplat.lib ole32.lib oleaut32.lib user32.lib gdi32.lib dbghelp.lib version.lib vfw32.lib wininet.lib
exit /b %errorlevel%
"@
Remove-Item -Force "$um\tools\s3engine\s3engine.dll" -ErrorAction SilentlyContinue   # never ship a prebuilt by mistake
Set-Content -Path "$env:TEMP\s3cl.cmd" -Value $cl -Encoding ASCII
cmd /c "$env:TEMP\s3cl.cmd"
if ($LASTEXITCODE) { throw 's3engine link failed' }
New-Item -ItemType Directory -Force "$repo\_bin\engine" | Out-Null
Copy-Item "$um\tools\s3engine\s3engine.dll" "$repo\_bin\engine\" -Force
EndStep

# decklink_monitor.dll (the engine's DeckLink signal monitor).
Step 'decklink_monitor'
$dm = "$um\tools\decklink_monitor"
Set-Content -Path "$dm\DeckLinkAPI.h" -Value '#include "DeckLinkAPI_h.h"' -Encoding ASCII
$cl2 = @"
call "$vsdev" -arch=amd64 -host_arch=amd64 -no_logo
cd /d "$dm"
cl /nologo /LD /O2 /W3 /MT /D_CRT_SECURE_NO_WARNINGS /I"$dm" /I"$um\src\thirdparty\DeckLinkSource" decklink_monitor.c "$um\src\thirdparty\DeckLinkSource\DeckLinkAPI_i.c" /Fe:decklink_monitor.dll /link ole32.lib oleaut32.lib uuid.lib
exit /b %errorlevel%
"@
Set-Content -Path "$env:TEMP\dmcl.cmd" -Value $cl2 -Encoding ASCII
cmd /c "$env:TEMP\dmcl.cmd"
if ($LASTEXITCODE) { throw 'decklink_monitor build failed' }
Copy-Item "$dm\decklink_monitor.dll" "$repo\_bin\engine\" -Force
EndStep
# DeckLinkSource.ax (the capture card's DirectShow source, with the card's HDMI audio).
Step 'DeckLinkSource'
Remove-Item -Force "$tp\DeckLinkSource\bin\Filters_x64\DeckLinkSource.ax" -ErrorAction SilentlyContinue   # never ship a prebuilt by mistake
& $msbuild "$tp\DeckLinkSource\DeckLinkSource.vcxproj" '/p:Configuration=Release Filter' '/p:Platform=x64' "/p:SolutionDir=$um\" `
    '/p:WindowsTargetPlatformVersion=10.0' '/p:BuildProjectReferences=false' '/m' '/nologo' '/v:minimal'
if ($LASTEXITCODE) { throw 'DeckLinkSource failed' }
$ax = Get-ChildItem -Path $um -Recurse -Filter 'DeckLinkSource.ax' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $ax) { throw 'DeckLinkSource.ax not found' }
Copy-Item $ax.FullName "$repo\_bin\engine\DeckLinkSource.ax" -Force
EndStep
Write-Host "engine built: $repo\_bin\engine\s3engine.dll"
